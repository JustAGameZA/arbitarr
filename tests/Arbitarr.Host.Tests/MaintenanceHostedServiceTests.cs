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
    /// THE POSITIVE CONTROL for the cancellation test below, and the reason that test's absence
    /// assertions bite.
    ///
    /// <para>Every assertion the cancellation case makes is an absence one — no exception, no
    /// recorded failure, no staged snapshot — and an empty set satisfies all three. If this fixture
    /// simply never took a backup (a missing registration, a retained count that resolved to 0, a
    /// secret key file the archive could not find), that test would pass while covering nothing.
    /// This test runs the SAME fixture with no stop at all and proves the backup really does
    /// complete here: <c>BackupStateStore.LastBackup</c> advances AND an automatic archive lands in
    /// <see cref="BackupPaths.BackupDirectory"/>. Only then is "none of that happened" evidence
    /// about the stop rather than about the fixture.</para>
    /// </summary>
    [Fact]
    public async Task The_automatic_backup_completes_when_the_service_is_left_running()
    {
        var clock = new FakeTimeProvider(Now);

        var configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-maintenance-backup-control-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        try
        {
            using var provider = BuildBackupProvider(configDirectory, clock, out var paths);
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var service = new MaintenanceHostedService(scopeFactory, clock);
            await service.StartAsync(CancellationToken.None);

            try
            {
                // Polled, not slept: the copy's duration is not ours to predict, and a sleep long
                // enough to be safe on a loaded CI agent is dead time on every run.
                //
                // POLLED ON THE RECORDED STATE, NOT ON THE FILE, and that distinction is
                // load-bearing rather than stylistic. WriteArchiveAsync opens the destination with
                // FileMode.Create and only THEN writes the entries into it, so the archive path
                // becomes enumerable the instant the zip is opened — before the copy is finished and
                // before AutomaticBackupJob calls RecordBackup. Waiting on the file therefore
                // returns while LastBackup is still legitimately null (observed: this test failed
                // exactly that way when it polled HostDisposalDrainsBackgroundWorkTests-style on the
                // directory). RecordBackup runs after WriteArchiveAsync returns, so the recorded
                // state is the signal that a backup actually COMPLETED.
                var store = provider.GetRequiredService<BackupStateStore>();
                var lastBackup = await WaitForRecordedBackupAsync(store);
                Assert.True(
                    lastBackup is not null,
                    "No automatic backup was recorded within the window, so this fixture never " +
                    "completes a backup and the cancellation test's absence assertions prove " +
                    "nothing. Archives present: " +
                    $"{(Directory.Exists(paths.BackupDirectory) ? Directory.EnumerateFiles(paths.BackupDirectory).Count() : 0)}.");
                Assert.True(lastBackup!.Automatic, "The recorded backup was not the automatic one.");

                // And the archive is really on disk — a recorded timestamp with no file would be a
                // state store that lies. Asserted as a completed fact once the state says the write
                // finished, never polled for (see above).
                var archives = Directory.EnumerateFiles(
                    paths.BackupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip").ToArray();
                Assert.True(
                    archives.Length > 0,
                    "An automatic backup was recorded but no archive carrying the automatic prefix exists.");

                var failure = provider.GetRequiredService<BackupStateStore>().LastBackupFailure;
                Assert.True(failure is null, $"The control run recorded a backup failure: {failure?.Reason}");
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            DeleteConfigDirectory(configDirectory);
        }
    }

    /// <summary>
    /// arb-rwhb: stopping the service as its automatic backup pass is reached must complete within
    /// the shutdown bound, must DECLINE the copy rather than begin one, and must NOT report a
    /// backup failure.
    ///
    /// <para><b>What this actually pins, and why the name says "before the copy begins".</b>
    /// <c>ExecuteAsync</c>'s first act is <c>ResolveIntervalAsync</c>, a database read, so
    /// <c>StartAsync</c> returns at that first await and the stop below is signalled before the
    /// backup pass starts — the copy is declined at the token check, not interrupted mid-flight.
    /// The earlier name ("during the automatic backup") claimed an interruption this does not
    /// demonstrate. The staging assertion below is what makes the distinction observable, and
    /// <see cref="The_automatic_backup_completes_when_the_service_is_left_running"/> is the control
    /// proving this fixture takes a real backup when it is not stopped.</para>
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
    public async Task Stopping_before_the_automatic_backup_copy_begins_declines_it_without_recording_a_failure()
    {
        var clock = new FakeTimeProvider(Now);

        var configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-maintenance-stop-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDirectory);

        try
        {
            using var provider = BuildBackupProvider(configDirectory, clock, out var paths);
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

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

            // POSITIVE EVIDENCE that the copy never started, not merely that nothing complained.
            // The staging snapshot is written before the zip, so its absence is the proof
            // BackupServiceTests uses for the same contract; under the pre-fix code the token was
            // never consulted and the full BackupDatabase copy ran, leaving one here.
            var stagedSnapshots = Directory.Exists(paths.StagingDirectory)
                ? Directory.EnumerateFiles(paths.StagingDirectory, StagingFileNames.SnapshotPrefix + "*.db").ToArray()
                : [];
            Assert.True(
                stagedSnapshots.Length == 0,
                "Shutdown left a staging snapshot behind, so the database copy had already begun: " +
                string.Join(", ", stagedSnapshots.Select(Path.GetFileName)));
        }
        finally
        {
            DeleteConfigDirectory(configDirectory);
        }
    }

    /// <summary>
    /// The REAL backup registrations, not the narrow provider the first test uses: without these,
    /// <c>RunAutomaticBackupAsync</c> resolves null and returns early, and both backup tests would
    /// pass without ever touching the code path they exist to cover. Shared by the control and the
    /// cancellation case so the two genuinely exercise the same fixture — a control built from a
    /// different one would not control anything.
    /// </summary>
    private ServiceProvider BuildBackupProvider(
        string configDirectory,
        FakeTimeProvider clock,
        out BackupPaths paths)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        services.AddScoped(sp => new SettingsRepository(sp.GetRequiredService<ArbitarrDbContext>(), TimeSpan.FromMinutes(15)));
        services.AddScoped(sp => new SettingsReader(sp.GetRequiredService<ArbitarrDbContext>()));
        services.AddSingleton<TimeProvider>(clock);

        paths = new BackupPaths(configDirectory);
        services.AddSingleton(paths);
        services.AddSingleton<BackupStateStore>();
        services.AddSingleton(sp => new BackupService(sp.GetRequiredService<BackupPaths>()));
        services.AddSingleton(sp => new AutomaticBackupJob(
            sp.GetRequiredService<BackupPaths>(),
            sp.GetRequiredService<BackupService>(),
            sp.GetRequiredService<BackupStateStore>(),
            NullLogger<AutomaticBackupJob>.Instance,
            clock));

        // The secret key file is copied into every archive unconditionally, so a backup cannot
        // succeed without it.
        File.WriteAllBytes(paths.SecretKeyPath, RandomNumberGenerator.GetBytes(32));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Polls for a COMPLETED automatic backup. Polling rather than sleeping a fixed span: the
    /// backup's duration is not ours to predict, and a sleep long enough to be safe on a loaded CI
    /// agent would be dead time on every run. Returns null on timeout so the caller can fail with a
    /// message about the WINDOW rather than about the property.
    ///
    /// <para>Deliberately NOT the archive-file poll
    /// <c>HostDisposalDrainsBackgroundWorkTests.WaitForAutomaticArchiveAsync</c> uses. That one
    /// answers "has a write begun", which is the right question there; here it races
    /// <c>RecordBackup</c> and reports success mid-copy — see the caller's comment.</para>
    /// </summary>
    private static async Task<LastBackup?> WaitForRecordedBackupAsync(BackupStateStore store)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            var recorded = store.LastBackup;
            if (recorded is not null)
            {
                return recorded;
            }

            await Task.Delay(50);
        }

        return null;
    }

    /// <summary>
    /// BEST-EFFORT removal of a test's config directory; nothing asserts on its success.
    /// <see cref="UnauthorizedAccessException"/> as well as <see cref="IOException"/>, matching the
    /// integration-test factories: Windows raises that one for a file another handle still has
    /// open, and catching only IOException let it escape and fail an otherwise green run.
    /// </summary>
    private static void DeleteConfigDirectory(string configDirectory)
    {
        SqlitePools.ClearPoolsForDirectory(configDirectory);
        try
        {
            Directory.Delete(configDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
