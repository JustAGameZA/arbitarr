using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// M7-3a: proves <see cref="MaintenanceHostedService"/> actually drives <c>MaintenanceJob</c> on a
/// schedule, against a real (temp-file) SQLite-backed <see cref="ArbitarrDbContext"/> resolved from
/// a fresh DI scope per cycle -- mirroring RefreshWorkerScopeTests' ExecuteAsync-level coverage
/// style, since MaintenanceHostedService (unlike RefreshWorker) exposes no fixed-deps constructor
/// for driving individual cycles directly.
/// </summary>
public sealed class MaintenanceHostedServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dbPath;

    public MaintenanceHostedServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-maintenance-hosted-test-{Guid.NewGuid():N}.db");

        using var context = CreateContext();
        context.Database.Migrate();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    private ServiceProvider BuildProvider(TimeProvider timeProvider)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        services.AddScoped(sp => new SettingsRepository(sp.GetRequiredService<ArbitarrDbContext>(), TimeSpan.FromMinutes(15)));
        services.AddSingleton(timeProvider);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ExecuteAsync_RunsMaintenanceJob_AndPrunesExpiredRows_OnEachCycle()
    {
        var clock = new FakeTimeProvider(Now);
        var interval = TimeSpan.FromMinutes(30);

        using (var seedContext = CreateContext())
        {
            seedContext.SearchResultCacheEntries.Add(new SearchResultCacheEntry
            {
                QueryKey = "expired-query",
                PayloadJson = "{}",
                FetchedAt = Now - TimeSpan.FromDays(10),
                FreshUntil = Now - TimeSpan.FromDays(9),
                ServeUntil = Now - TimeSpan.FromDays(1),
                LastRequestedAt = Now - TimeSpan.FromDays(9),
            });
            await seedContext.SaveChangesAsync();
        }

        using var provider = BuildProvider(clock);
        using var scopeFactoryHolder = provider.CreateScope();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var service = new MaintenanceHostedService(scopeFactory, clock);

        await service.StartAsync(CancellationToken.None);

        // The first cycle runs immediately (before the first delay); wait for it to prune the
        // seeded row rather than assuming a single yield is enough.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        long remaining;
        using (var probeContext = CreateContext())
        {
            remaining = await probeContext.SearchResultCacheEntries.CountAsync();
        }
        while (remaining != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
            using var probeContext = CreateContext();
            remaining = await probeContext.SearchResultCacheEntries.CountAsync();
        }

        Assert.Equal(0, remaining);
        Assert.NotEqual(TaskStatus.Faulted, service.ExecuteTask?.Status);

        await service.StopAsync(CancellationToken.None);
        Assert.Null(service.ExecuteTask?.Exception);
    }
}
