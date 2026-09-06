using Arbitarr.Core.Diagnostics;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Host.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// #55 step 2: <see cref="ScopedEventSink"/> writes through the shared store, and — the part that
/// actually matters — never lets a failure of its own reach the caller.
///
/// Recording history is strictly less important than the work that produced it. A search must not
/// 500 because the event table was unwritable, and a worker cycle must not abort mid-refresh
/// because a summary failed validation. These tests pin that, because the natural "fix" for a
/// swallowed exception is to stop swallowing it, and doing so here would put the search path behind
/// a diagnostic write.
/// </summary>
public sealed class ScopedEventSinkTests : IDisposable
{
    private readonly string _dbPath;
    private readonly ServiceProvider _provider;

    public ScopedEventSinkTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-sink-test-{Guid.NewGuid():N}.db");

        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var options = new DbContextOptionsBuilder<ArbitarrDbContext>()
                .UseSqlite($"Data Source={_dbPath}")
                .Options;
            var context = new ArbitarrDbContext(options);
            context.Database.Migrate();
            return context;
        });
        services.AddScoped<EventRepository>();
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ScopedEventSink CreateSink() => new(
        _provider.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ScopedEventSink>.Instance);

    private async Task<List<EventEntry>> ReadAllAsync()
    {
        using var scope = _provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<EventRepository>();
        return await repository.GetAllAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Records_an_event_through_the_shared_store()
    {
        var sink = CreateSink();

        await sink.RecordAsync(
            RecordedEventKind.SourceFailed,
            "Source failed during a refresh",
            reason: "Timed out",
            sourceDisplayName: "Primary NZBHydra");

        var stored = Assert.Single(await ReadAllAsync());
        Assert.Equal(EventKind.SourceFailed, stored.Kind);
        Assert.Equal("Source failed during a refresh", stored.Summary);
        Assert.Equal("Timed out", stored.Reason);
        Assert.Equal("Primary NZBHydra", stored.SourceDisplayName);
    }

    /// <summary>
    /// A rejected write costs the event, never the caller. An empty summary is rejected at the
    /// repository boundary (AC24 — reject, never coerce), and the sink must absorb that.
    /// </summary>
    [Fact]
    public async Task Swallows_a_validation_failure_instead_of_throwing_at_the_caller()
    {
        var sink = CreateSink();

        var recording = async () => await sink.RecordAsync(RecordedEventKind.WorkerCycle, summary: "   ");

        await recording();

        Assert.Empty(await ReadAllAsync());
    }

    /// <summary>
    /// The same guarantee for an infrastructure failure rather than a validation one: a sink whose
    /// whole service provider has been disposed still must not throw at its caller.
    /// </summary>
    [Fact]
    public async Task Swallows_an_infrastructure_failure_instead_of_throwing_at_the_caller()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new ArbitarrDbContext(
            new DbContextOptionsBuilder<ArbitarrDbContext>()
                .UseSqlite("Data Source=file:sink-unwritable?mode=memory")
                .Options));
        services.AddScoped<EventRepository>();
        var provider = services.BuildServiceProvider();
        var sink = new ScopedEventSink(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ScopedEventSink>.Instance);

        // Disposed out from under the sink: resolving a scope now fails hard.
        provider.Dispose();

        await sink.RecordAsync(RecordedEventKind.WorkerCycle, "a cycle that cannot be recorded");
    }

    /// <summary>
    /// Cancellation is the one thing the sink does NOT swallow: a cancelled write means the host is
    /// shutting down or the client went away, which is the caller's business to observe, and
    /// treating it as a recording failure would log a warning on every shutdown.
    /// </summary>
    [Fact]
    public async Task Propagates_cancellation_rather_than_treating_it_as_a_recording_failure()
    {
        var sink = CreateSink();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sink.RecordAsync(
                RecordedEventKind.WorkerCycle,
                "a cycle interrupted by shutdown",
                cancellationToken: cancellation.Token));
    }
}
