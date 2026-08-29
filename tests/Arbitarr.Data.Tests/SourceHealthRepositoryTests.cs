using ArrSearcher.Core.Sources.CircuitBreaker;
using ArrSearcher.Data.CircuitBreaker;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ArrSearcher.Data.Tests;

/// <summary>
/// Proves the thin persistence adapter round-trips <see cref="CircuitBreakerSnapshot"/> to/from a
/// real SQLite-backed <c>SourceHealthRecord</c> row, and that <see cref="PersistentSourceCircuitBreaker"/>
/// survives a simulated "restart" (a fresh <see cref="SourceCircuitBreaker"/> instance backed by the
/// same on-disk database) — i.e. breaker state actually persists across restarts, not just in memory.
/// </summary>
public sealed class SourceHealthRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    public SourceHealthRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-circuitbreaker-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArrSearcherDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArrSearcherDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArrSearcherDbContext(optionsBuilder.Options);
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
            CurrentBackoff: TimeSpan.FromSeconds(5),
            LastFailureAt: Now,
            LastSuccessAt: null,
            LastError: "boom",
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
}
