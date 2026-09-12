using Arbitarr.Core.Security;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
using Arbitarr.Host.Security;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-acy9: a stamp write dispatched just before the host stops must be DRAINED, not orphaned.
///
/// <para><b>The defect.</b> Both throttled recorders dispatch their write with a detached
/// <c>Task.Run</c> — deliberately, since neither may sit on the request path (see the interfaces'
/// remarks). Nothing awaited the result, so a write still in flight when the host stopped had its
/// DI scope's provider disposed out from under it, fell into the recorder's
/// <c>catch (Exception)</c>, and logged "Failed to record activity for session N" (or the key
/// equivalent) at Warning on every shutdown. The stamp was genuinely lost, which for a session is
/// not cosmetic: the value is what idle expiry is measured against.</para>
///
/// <para><b>What these tests assert, and why it is the right property.</b> Not "no Warning was
/// logged" — that is the symptom, and a test for it would pass just as well if the recorder had
/// been made silent, which is exactly the change the bead forbids (the Warning is load-bearing for
/// the genuine failure case). The property is the one that makes the Warning stop being emitted for
/// the wrong reason: <b>after <c>StopAsync</c> returns, the write has landed in the database.</b>
/// That is decidable locally, and it is false in the broken world for a reason no amount of
/// log-level fiddling would change.</para>
///
/// <para><b>How the race is made deterministic rather than hoped for.</b> A test that dispatched a
/// write and immediately stopped would be a coin flip — on an idle machine the write usually wins,
/// and the test would pass in the broken world most of the time. So
/// <see cref="GatedScopeFactory"/> holds the write inside scope creation until the test releases
/// it, which puts the write UNAMBIGUOUSLY in flight at the moment <c>StopAsync</c> is called. The
/// drain is then the only thing that can make the row appear.</para>
///
/// <para><b>NON-VACUITY / POSITIVE CONTROL (CLAUDE.md §4).</b> "The row is present after the drain"
/// would pass vacuously if the row had been written before the stop ever happened — i.e. if the
/// gate did not actually hold the write. <see cref="The_gate_really_does_hold_the_write_in_flight"/>
/// pins that directly: with the gate closed and NO drain, the row is absent and the recorder logs
/// its Warning. That is the broken world's behaviour, reproduced through the gate rather than
/// through a mutation, so it demonstrates both that the window is genuinely open and that these
/// tests would see it close.</para>
///
/// <para><b>MUTATION-PROVED</b> (CLAUDE.md §4) outside the repository, in a throwaway copy of the
/// tree holding both implementations — nothing was mutated in this worktree and nothing is left
/// behind. With <c>StopAsync</c> reverted to <c>Task.CompletedTask</c> (the pre-fix behaviour, the
/// detached <c>Task.Run</c> otherwise untouched), this class FAILS: 4 of 4 runs red, with the two
/// drain tests reporting the stamp still unwritten and the control reporting an empty warning sink.
/// The fixed build passes. Taken alone the key-recorder test detected it in 3 of 3 runs; within the
/// class it occasionally passes while its siblings fail, which is why the two recorders are pinned
/// as SEPARATE tests rather than collapsed into one — the class is what is red, every time.</para>
///
/// <para><b>What this class does NOT cover, stated so the next reader does not assume it does.</b> A
/// second mutation — replacing <c>Dispatch</c>'s gated dispatch with a plain
/// <c>Task.Run(() =&gt; WriteAsync(...))</c>, so the task is added to the in-flight set only after it
/// may already be running — SURVIVES all four tests, 3 runs of 3. That is inherent to the harness
/// rather than an oversight: <see cref="GatedScopeFactory"/> suspends the write inside
/// <c>CreateScope</c>, so the write is still in flight when the drain samples the set no matter which
/// order the registration happened in. The gate in <c>Dispatch</c> is therefore justified by
/// argument, not by this class — see its remarks. A test that could see the difference would have to
/// race the registration against a write that completes immediately, which is the coin flip these
/// tests were deliberately built to avoid.</para>
/// </summary>
public sealed class ThrottledRecorderShutdownDrainTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arbitarr-recorder-drain-test");
    private readonly ServiceProvider _provider;

    public ThrottledRecorderShutdownDrainTests()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var context = new ArbitarrDbContext(
                new DbContextOptionsBuilder<ArbitarrDbContext>()
                    .UseSqlite(_database.ConnectionString)
                    .Options);
            context.Database.Migrate();
            return context;
        });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped(sp => new ApiKeyRepository(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SessionRepository(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>()));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        // Order as in ScopedEventSinkTests: the provider owns the scoped DbContexts, so it goes
        // first; the fixture's pool clear and delete are only correct once nothing holds the file.
        _provider.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task Stopping_the_host_drains_an_in_flight_api_key_last_used_write()
    {
        var keyId = await SeedApiKeyAsync();
        using var gate = new GatedScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var sink = new RecordingLoggerProvider();

        var recorder = new ThrottledApiKeyLastUsedRecorder(
            gate,
            TimeProvider.System,
            sink.CreateLogger<ThrottledApiKeyLastUsedRecorder>());

        var usedAt = DateTimeOffset.UtcNow;
        recorder.RecordUsed(keyId);

        // The write is now blocked inside scope creation, so it is provably in flight — not merely
        // "probably not finished yet". Release it and stop in the same breath: the release makes the
        // write runnable, and StopAsync must be what waits for it.
        await gate.WaitUntilBlockedAsync();
        gate.Release();
        await ((IHostedService)recorder).StopAsync(CancellationToken.None);

        // THE PROPERTY. Pre-fix, StopAsync returned immediately and this row was still unwritten,
        // so LastUsedAt was still null.
        var lastUsed = await ReadApiKeyLastUsedAsync(keyId);
        Assert.NotNull(lastUsed);
        Assert.Equal(usedAt, lastUsed.Value, TimeSpan.FromMinutes(1));
        Assert.Empty(sink.Warnings);
    }

    [Fact]
    public async Task Stopping_the_host_drains_an_in_flight_session_activity_write()
    {
        var sessionId = await SeedSessionAsync();
        using var gate = new GatedScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var sink = new RecordingLoggerProvider();

        var recorder = new ThrottledSessionActivityRecorder(
            gate,
            TimeProvider.System,
            sink.CreateLogger<ThrottledSessionActivityRecorder>());

        var seenAt = DateTimeOffset.UtcNow;
        recorder.RecordSeen(sessionId);

        await gate.WaitUntilBlockedAsync();
        gate.Release();
        await ((IHostedService)recorder).StopAsync(CancellationToken.None);

        Assert.Equal(seenAt, await ReadSessionLastSeenAsync(sessionId), TimeSpan.FromMinutes(1));
        Assert.Empty(sink.Warnings);

        // Pinned separately from the key recorder's case rather than assumed to follow: these are
        // two independent classes that merely look alike, and fixing one would not fix the other.
    }

    /// <summary>
    /// POSITIVE CONTROL for both tests above. They assert that a row IS present after the drain;
    /// that assertion carries information only if the row would have been ABSENT without it. So
    /// this runs the identical scenario and simply never drains — the gate stays closed across the
    /// point where the real tests stop the host — and requires the row to be missing.
    ///
    /// <para>It also proves the gate does what the other two rely on. Were
    /// <see cref="GatedScopeFactory"/> not actually blocking, the write would complete on its own
    /// and this test would fail here rather than silently weakening the two above.</para>
    /// </summary>
    [Fact]
    public async Task The_gate_really_does_hold_the_write_in_flight()
    {
        var sessionId = await SeedSessionAsync();
        using var gate = new GatedScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var sink = new RecordingLoggerProvider();

        var recorder = new ThrottledSessionActivityRecorder(
            gate,
            TimeProvider.System,
            sink.CreateLogger<ThrottledSessionActivityRecorder>());

        var seedLastSeen = await ReadSessionLastSeenAsync(sessionId);
        recorder.RecordSeen(sessionId);
        await gate.WaitUntilBlockedAsync();

        // No drain, and the gate still shut: the write cannot have happened.
        Assert.Equal(seedLastSeen, await ReadSessionLastSeenAsync(sessionId));

        // Now reproduce the pre-fix SHAPE of the failure — the scope resolution faulting the way a
        // disposed provider would — and require the recorder's Warning to appear. Without this the
        // `Assert.Empty(sink.Warnings)` in the tests above would be an absence assertion over a sink
        // never shown capable of recording anything.
        gate.FailInsteadOfReleasing();

        // Await the recorder's own drain rather than the gate's signal: the gate signals when the
        // scope resolution threw, which is BEFORE the recorder's catch has formatted and logged. The
        // drain waits for the write task itself, so the Warning is guaranteed to be on the sink by
        // the time this returns — and the assertion below is about the log, not about the throw.
        await ((IHostedService)recorder).StopAsync(CancellationToken.None);

        var warning = Assert.Single(sink.Warnings);
        Assert.Contains("Failed to record activity for session", warning);
        Assert.Equal(seedLastSeen, await ReadSessionLastSeenAsync(sessionId));
    }

    /// <summary>
    /// The drain is BOUNDED: a write that never finishes must not hang shutdown for ever. Asserts
    /// the timeout arm returns and says so, rather than only the happy path being covered.
    /// </summary>
    [Fact]
    public async Task A_write_that_never_finishes_is_abandoned_rather_than_hanging_shutdown()
    {
        var sessionId = await SeedSessionAsync();
        using var gate = new GatedScopeFactory(_provider.GetRequiredService<IServiceScopeFactory>());
        var sink = new RecordingLoggerProvider();

        var recorder = new ThrottledSessionActivityRecorder(
            gate,
            TimeProvider.System,
            sink.CreateLogger<ThrottledSessionActivityRecorder>());

        recorder.RecordSeen(sessionId);
        await gate.WaitUntilBlockedAsync();

        // Never released. StopAsync must still return, on its own bound.
        var stopping = ((IHostedService)recorder).StopAsync(CancellationToken.None);

        var finished = await Task.WhenAny(
            stopping,
            Task.Delay(ThrottledSessionActivityRecorder.DrainTimeout + TimeSpan.FromSeconds(20)));

        Assert.True(
            ReferenceEquals(finished, stopping),
            "StopAsync did not return within the drain timeout plus a generous margin, so the drain is " +
            "unbounded — a shutdown could hang on a stuck write, which is worse than the lost stamp it " +
            "was trying to prevent.");

        Assert.Contains(sink.Warnings, w => w.Contains("Gave up waiting for", StringComparison.Ordinal));

        // Let the abandoned write unwind so it does not outlive the test class.
        gate.Release();
        await gate.WaitUntilDrainedAsync();
    }

    private async Task<long> SeedApiKeyAsync()
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        var entry = new ApiKeyEntry
        {
            Label = "drain-test",
            KeyHash = "REDACTED",
            // Least authority the enum offers: nothing here exercises scope, and a fixture should
            // not mint an Admin key it has no use for.
            Scope = ApiKeyScope.ReadOnly,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        context.ApiKeys.Add(entry);
        await context.SaveChangesAsync();
        return entry.Id;
    }

    private async Task<long> SeedSessionAsync()
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        var user = new UserEntry
        {
            Username = "drain-test",
            PasswordHash = "REDACTED",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var session = new SessionEntry
        {
            UserId = user.Id,
            TokenHash = "REDACTED",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
            AbsoluteExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        context.Sessions.Add(session);
        await context.SaveChangesAsync();
        return session.Id;
    }

    private async Task<DateTimeOffset?> ReadApiKeyLastUsedAsync(long keyId)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        return (await context.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == keyId)).LastUsedAt;
    }

    private async Task<DateTimeOffset> ReadSessionLastSeenAsync(long sessionId)
    {
        using var scope = _provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        return (await context.Sessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).LastSeenAt;
    }

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> that blocks the FIRST scope creation until the test
    /// says otherwise, so a detached write can be held provably in flight.
    ///
    /// <para>Blocking inside <c>CreateScope</c> rather than inside the repository is deliberate: it
    /// is the earliest point the recorder's detached body reaches, so the write is suspended before
    /// it has touched the database at all. That makes "the row is absent" unambiguous — it is absent
    /// because the write has not run, not because it ran and failed.</para>
    ///
    /// <para><see cref="FailInsteadOfReleasing"/> reproduces the pre-fix failure SHAPE: scope
    /// creation throwing, which is what a provider disposed out from under an orphaned write
    /// actually does. Used by the positive control to prove the log sink can see the Warning.</para>
    /// </summary>
    private sealed class GatedScopeFactory : IServiceScopeFactory, IDisposable
    {
        private readonly IServiceScopeFactory _inner;
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gatedCalls;
        private volatile bool _fail;

        public GatedScopeFactory(IServiceScopeFactory inner) => _inner = inner;

        public IServiceScope CreateScope()
        {
            // Only the first call is gated; the test's own seeding and reading go through the
            // underlying provider directly, but a recorder that ever dispatched twice would
            // otherwise deadlock its second write on a gate already consumed.
            if (Interlocked.Increment(ref _gatedCalls) == 1)
            {
                _blocked.TrySetResult();

                try
                {
                    _released.Task.GetAwaiter().GetResult();

                    if (_fail)
                    {
                        throw new ObjectDisposedException(
                            nameof(IServiceProvider),
                            "Simulates the provider being disposed out from under an orphaned write.");
                    }
                }
                finally
                {
                    if (_fail)
                    {
                        _drained.TrySetResult();
                    }
                }

                var scope = _inner.CreateScope();
                _drained.TrySetResult();
                return scope;
            }

            return _inner.CreateScope();
        }

        /// <summary>Completes once a write has actually reached the gate and is suspended in it.</summary>
        public Task WaitUntilBlockedAsync() => _blocked.Task;

        /// <summary>Completes once the gated write has passed the gate, either way.</summary>
        public Task WaitUntilDrainedAsync() => _drained.Task;

        public void Release() => _released.TrySetResult();

        public void FailInsteadOfReleasing()
        {
            _fail = true;
            _released.TrySetResult();
        }

        public void Dispose()
        {
            // Never leave a gated write parked on a gate nobody will open.
            _released.TrySetResult();
        }
    }

    /// <summary>
    /// Captures Warning-level messages so the tests can assert both their presence (in the positive
    /// control) and their absence (after a successful drain). Formatted rather than structured,
    /// because what is being asserted is which message fired, not its fields.
    /// </summary>
    private sealed class RecordingLoggerProvider
    {
        private readonly List<string> _warnings = [];
        private readonly Lock _sync = new();

        public IReadOnlyList<string> Warnings
        {
            get
            {
                lock (_sync)
                {
                    return _warnings.ToArray();
                }
            }
        }

        public ILogger<T> CreateLogger<T>() => new Recording<T>(this);

        private void Record(string message)
        {
            lock (_sync)
            {
                _warnings.Add(message);
            }
        }

        private sealed class Recording<T>(RecordingLoggerProvider owner) : ILogger<T>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    owner.Record(formatter(state, exception));
                }
            }
        }
    }
}
