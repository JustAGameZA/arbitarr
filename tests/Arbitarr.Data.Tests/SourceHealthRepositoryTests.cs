using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data.CircuitBreaker;
using Arbitarr.Data.Entities;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Arbitarr.Data.Tests;

/// <summary>
/// Proves the thin persistence adapter round-trips <see cref="CircuitBreakerSnapshot"/> to/from a
/// real SQLite-backed <c>SourceHealthRecord</c> row, and that <see cref="PersistentSourceCircuitBreaker"/>
/// survives a simulated "restart" (a fresh <see cref="SourceCircuitBreaker"/> instance backed by the
/// same on-disk database) — i.e. breaker state actually persists across restarts, not just in memory.
/// </summary>
public sealed class SourceHealthRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arr-searcher-circuitbreaker-test");
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task LoadAsync_ReturnsInitialSnapshot_WhenNoRowExists()
    {
        using var context = CreateContext();
        var repository = new SourceHealthRepository(context);

        var snapshot = await repository.LoadAsync("unknown-source");

        Assert.Equal(CircuitBreakerSnapshot.Initial, snapshot);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsSnapshot()
    {
        var snapshot = new CircuitBreakerSnapshot(
            State: CircuitState.Open,
            ConsecutiveFailures: 3,
            BaseBackoff: TimeSpan.FromSeconds(5),
            CurrentBackoff: TimeSpan.FromSeconds(5),
            LastFailureAt: Now,
            LastSuccessAt: null,
            LastError: "boom",
            LastOutcome: SourceStatusOutcome.AuthRejected,
            LastUpstreamStatusCode: 401,
            NextProbeAt: Now + TimeSpan.FromMinutes(5));

        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            await repository.SaveAsync("nzbhydra2", snapshot);
        }

        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            var loaded = await repository.LoadAsync("nzbhydra2");
            Assert.Equal(snapshot, loaded);
            Assert.Equal(TimeSpan.FromSeconds(5), loaded.BaseBackoff);
            // arb-mhd2: named explicitly as well as covered by the record equality above. The
            // equality assertion would still pass if BOTH sides were the enum's default (None),
            // so it alone does not prove the outcome survived the round trip; these two do,
            // because AuthRejected and 401 are not defaults.
            Assert.Equal(SourceStatusOutcome.AuthRejected, loaded.LastOutcome);
            Assert.Equal(401, loaded.LastUpstreamStatusCode);
        }
    }

    /// <summary>
    /// Non-vacuous restart proof: drives a breaker open via real failures, persists via the
    /// adapter, then constructs a brand-new <see cref="SourceCircuitBreaker"/> (simulating a
    /// process restart) hydrated only from the on-disk row, and confirms it still refuses calls
    /// and still honors the correct remaining probe window.
    /// </summary>
    [Fact]
    public async Task PersistentBreaker_SurvivesSimulatedRestart()
    {
        const string source = "nzbhydra2";
        var clock = new FakeTimeProvider(Now);

        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 });
            var persistent = new PersistentSourceCircuitBreaker(breaker, repository);

            await persistent.RecordFailureAsync(source, new InvalidOperationException("f1"));
            await persistent.RecordFailureAsync(source, new InvalidOperationException("f2"));
            await persistent.RecordFailureAsync(source, new InvalidOperationException("f3"));

            Assert.False(await persistent.CanCallAsync(source));
        }

        // Simulate a restart: fresh DbContext, fresh repository, fresh in-memory breaker instance.
        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            var breaker = new SourceCircuitBreaker(clock, new CircuitBreakerOptions { JitterFraction = 0 });
            var persistent = new PersistentSourceCircuitBreaker(breaker, repository);

            Assert.False(await persistent.CanCallAsync(source));

            clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
            Assert.True(await persistent.CanCallAsync(source));

            await persistent.RecordSuccessAsync(source);
            Assert.True(await persistent.CanCallAsync(source));
        }
    }

    /// <summary>
    /// arb-mhd2: the stored outcome is read back by EXPLICIT name matching, never
    /// <c>Enum.TryParse</c> (CLAUDE.md §3 — TryParse accepts the numeric form, so a stored "3" would
    /// mint a member through an input shape no writer is documented to produce, and neither
    /// <c>Enum.IsDefined</c> nor trimming closes that).
    ///
    /// <para>Every row here must land on <c>Unknown</c>: a LEGACY row written before the column
    /// existed (outcome null, error text present), a name a NEWER build wrote that this one does not
    /// know, and the input shapes an unsafe parse would have accepted. None is ever inferred from
    /// the error text, which is why each row carries text naming a DIFFERENT outcome and still
    /// projects as Unknown rather than as Timeout.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("SomeFutureOutcome")]
    [InlineData("")]
    [InlineData("3")]
    [InlineData("upstreamerror")]
    public async Task An_unrecognised_or_absent_stored_outcome_reads_back_as_Unknown(string? stored)
    {
        var source = $"unknown-outcome-{stored ?? "null"}";
        // Text that NAMES a different outcome, so a projection that peeked at the string instead of
        // reading the column would land on Timeout and fail this.
        const string TimeoutFlavouredText = "TaskCanceledException (timed out)";

        using (var context = CreateContext())
        {
            context.SourceHealthRecords.Add(new SourceHealthRecord
            {
                SourceName = source,
                State = CircuitBreakerState.Open,
                ConsecutiveFailures = 3,
                LastError = TimeoutFlavouredText,
                LastOutcome = stored,
            });
            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            var snapshot = await repository.LoadAsync(source);

            // Positive control: the row really is there and really did round-trip, so the outcome
            // assertion below is about the projection rather than about a row that never loaded.
            Assert.Equal(TimeoutFlavouredText, snapshot.LastError);
            Assert.Equal(3, snapshot.ConsecutiveFailures);

            Assert.Equal(SourceStatusOutcome.Unknown, snapshot.LastOutcome);
        }
    }

    /// <summary>
    /// arb-mhd2: the round trip for every name a live writer CAN store. Stored by name, matched by
    /// name, with no member left behind — a member added to the enum without a matching arm in the
    /// reader would silently read back as Unknown, and this is what says so.
    /// </summary>
    [Theory]
    [InlineData(SourceStatusOutcome.None)]
    [InlineData(SourceStatusOutcome.UpstreamError)]
    [InlineData(SourceStatusOutcome.Unreachable)]
    [InlineData(SourceStatusOutcome.Timeout)]
    [InlineData(SourceStatusOutcome.AuthRejected)]
    [InlineData(SourceStatusOutcome.InternalError)]
    public async Task Every_writable_outcome_round_trips_by_name(SourceStatusOutcome outcome)
    {
        var source = $"round-trip-{outcome}";

        using (var context = CreateContext())
        {
            context.SourceHealthRecords.Add(new SourceHealthRecord
            {
                SourceName = source,
                State = CircuitBreakerState.Open,
                ConsecutiveFailures = 1,
                LastError = "irrelevant",
                // Stored as the NAME, the SourceCallOutcome precedent — never the numeric value,
                // which would renumber silently if a member were ever inserted mid-enum.
                LastOutcome = outcome.ToString(),
            });
            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            var repository = new SourceHealthRepository(context);
            var snapshot = await repository.LoadAsync(source);

            Assert.Equal(outcome, snapshot.LastOutcome);
            // Specifically NOT Unknown: that is the value a missing reader arm would produce, so
            // naming it here is what makes a forgotten arm fail loudly rather than degrade quietly.
            Assert.NotEqual(SourceStatusOutcome.Unknown, snapshot.LastOutcome);
        }
    }
}
