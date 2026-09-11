using System.Security.Cryptography;
using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Maintenance;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly SqliteTestDatabase _database = new("arbitarr-maintenance-hosted-test");

    public MaintenanceHostedServiceTests()
    {

        using var context = CreateContext();
        context.Database.Migrate();
    }

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
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

    /// <summary>
    /// arb-rwhb: stopping the service while its automatic backup is running must complete within
    /// the shutdown bound and must NOT report a backup failure.
    ///
    /// <para><b>The defect.</b> The automatic backup runs on the first pass, immediately, before the
    /// first <c>Task.Delay</c> (the test above pins that ordering). It reaches
    /// <c>BackupService.SnapshotDatabase</c>, which opens a pooled connection to the live database
    /// and calls the blocking, uncancellable <c>SqliteConnection.BackupDatabase</c>. Nothing
    /// consulted the stopping token on the way in, so a host already shutting down still began a
    /// full copy — and whatever owned the config directory then deleted it out from under a live
    /// reader, logging <c>SQLite Error 5898: 'disk I/O error'</c>.</para>
    ///
    /// <para><b>Why "no failure logged" is the assertion.</b> A skipped backup and a FAILED backup
    /// are different outcomes with different consequences: a failure calls
    /// <c>RecordBackupFailure</c>, which surfaces a broken safety net in the Backup tab. Declining
    /// cleanly must do neither. Asserting only "StopAsync returned" would pass in both worlds.</para>
    ///
    /// <para>MUTATION-PROVED outside the repository: with the token check removed from
    /// <c>BackupService.WriteArchiveAsync</c>, the copy runs to completion past the stop. No
    /// mutation was committed.</para>
    /// </summary>
    [Fact]
    public async Task Stopping_during_the_automatic_backup_completes_cleanly_without_recording_a_failure()
    {
        var clock = new FakeTimeProvider(Now);

        var configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-maintenance-stop-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        try
        {
            // The real backup registrations, not the narrow provider the test above uses: without
            // these, RunAutomaticBackupAsync resolves null and returns early, and this test would
            // pass without ever touching the code path it exists to cover.
            var services = new ServiceCollection();
            services.AddScoped(_ => CreateContext());
            services.AddScoped(sp => new SettingsRepository(sp.GetRequiredService<ArbitarrDbContext>(), TimeSpan.FromMinutes(15)));
            services.AddScoped(sp => new SettingsReader(sp.GetRequiredService<ArbitarrDbContext>()));
            services.AddSingleton<TimeProvider>(clock);

            var paths = new BackupPaths(configDirectory);
            services.AddSingleton(paths);
            services.AddSingleton<BackupStateStore>();
            services.AddSingleton(sp => new BackupService(sp.GetRequiredService<BackupPaths>()));
            services.AddSingleton(sp => new AutomaticBackupJob(
                sp.GetRequiredService<BackupPaths>(),
                sp.GetRequiredService<BackupService>(),
                sp.GetRequiredService<BackupStateStore>(),
                NullLogger<AutomaticBackupJob>.Instance,
                clock));

            using var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            // The secret key file is copied into every archive unconditionally, so a backup cannot
            // succeed without it.
            File.WriteAllBytes(paths.SecretKeyPath, RandomNumberGenerator.GetBytes(32));

            var service = new MaintenanceHostedService(scopeFactory, clock);
            await service.StartAsync(CancellationToken.None);

            // Stop while the first pass is in flight. StopAsync awaits ExecuteAsync's task — the
            // backup is awaited inline on it, never dispatched detached — so this returns only once
            // any in-flight copy has finished or been declined.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await service.StopAsync(CancellationToken.None);
            stopwatch.Stop();

            // THE BOUND: a stop that hangs on an uncancellable copy is the failure this guards.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(30),
                $"StopAsync took {stopwatch.Elapsed}, which means shutdown waited on work it should have declined.");

            // NOT FAULTED, and NOT a recorded failure: declining a backup because the host is
            // stopping is a clean stop, not a broken safety net.
            Assert.Null(service.ExecuteTask?.Exception);

            var failure = provider.GetRequiredService<BackupStateStore>().LastBackupFailure;
            Assert.True(
                failure is null,
                $"Shutdown recorded an automatic-backup failure: {failure?.Reason}");
        }
        finally
        {
            SqlitePools.ClearPoolsForDirectory(configDirectory);
            try
            {
                Directory.Delete(configDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
