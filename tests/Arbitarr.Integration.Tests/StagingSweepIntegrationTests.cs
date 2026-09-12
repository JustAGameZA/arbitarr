using Arbitarr.Data.Backup;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-fxw: the real Host, on startup, reclaims an orphaned staging file planted before it ever
/// ran — the shape a hard kill mid-restore/backup leaves behind, with nothing left running to hit
/// the writer's own <c>finally</c>.
///
/// <para>This test builds its OWN <see cref="ArbitarrWebApplicationFactory"/> rather than sharing
/// the class fixture every other integration test in this assembly uses: the orphan has to exist
/// in <c>BackupPaths.StagingDirectory</c> BEFORE the host's first request starts it (a shared
/// fixture may already have been started by another test in the collection by the time this one
/// runs). <see cref="ArbitarrWebApplicationFactory.ConfigDirectory"/> is a plain, GUID-distinct
/// path computed in the field initializer -- reading it does not touch the host at all, so this
/// test can create that directory and plant the orphan before anything triggers
/// <c>WebApplicationFactory{T}</c>'s lazy build-and-start (which happen together; there is no
/// public seam between "host built" and "host started").</para>
/// </summary>
public sealed class StagingSweepIntegrationTests : IAsyncLifetime
{
    private readonly ArbitarrWebApplicationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// DRAIN, not delete (arb-yt7j). This class already deleted its config directory correctly: the
    /// factory it builds is the OWNING kind, so its disposal runs
    /// <see cref="TestSupport.ConfigDirectoryTeardown.TryDelete"/>, and since arb-dhua fixed the
    /// connection ownership that had made that delete impossible, it succeeds. Measured — this class
    /// run alone leaked no config directory before this change and leaks none after. The property
    /// fixed here is therefore the OTHER one, and the residue count is not evidence for it.
    ///
    /// <para>It disposed SYNCHRONOUSLY, and <c>base.Dispose</c> cannot await the host's hosted
    /// services the way <c>base.DisposeAsync</c> does. That is the gap
    /// <see cref="HostDisposalDrainsBackgroundWorkTests"/> pins, and it names the synchronous path
    /// as the sensitive one: <c>MaintenanceHostedService</c> begins an automatic backup immediately
    /// on startup, on a detached <c>BackgroundService</c> task nothing awaited, so a synchronous
    /// disposal can return while that copy is still reading the database. The orphaned continuation
    /// then resolves from a disposed provider and faults whichever UNRELATED test is in flight
    /// rather than this one — which is what makes the defect worth fixing despite leaking nothing.
    /// </para>
    ///
    /// <para><see cref="IAsyncLifetime"/>, never bare <c>System.IAsyncDisposable</c>: xunit v2
    /// awaits the former and silently ignores the latter. Awaiting the factory's own
    /// <c>DisposeAsync</c> is what drains the host; the delete that follows inside the factory is
    /// unchanged and remains the factory's own.</para>
    /// </summary>
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task An_orphan_planted_before_startup_is_gone_once_the_host_is_up()
    {
        // ConfigDirectory alone does not start the host -- see the class remarks.
        var configDirectory = _factory.ConfigDirectory;

        var stagingDirectory = Path.Combine(configDirectory, "backup-staging");
        Directory.CreateDirectory(stagingDirectory);

        var orphanPath = Path.Combine(stagingDirectory, StagingFileNames.UploadPrefix + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(orphanPath, "orphaned upload spool from a killed restore");
        // Older than "now" by a comfortable margin so it is unambiguously before process start,
        // regardless of clock resolution between this write and the host actually starting.
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddMinutes(-10));

        // A BYSTANDER with the same prefix, in the system temp directory rather than this
        // instance's staging directory -- the positive control proving the sweep is scoped to
        // BackupPaths.StagingDirectory and not to the prefix (and therefore not to the whole temp
        // tree) alone.
        var bystanderPath = Path.Combine(
            Path.GetTempPath(), StagingFileNames.UploadPrefix + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(bystanderPath, "unrelated file the sweep must never touch");
        File.SetLastWriteTimeUtc(bystanderPath, DateTime.UtcNow.AddMinutes(-10));

        try
        {
            // Starts the real Host, including the hosted-service startup sequence (StagingSweepService among them).
            using var client = _factory.CreateClient();

            var paths = _factory.Services.GetRequiredService<BackupPaths>();
            Assert.Equal(stagingDirectory, paths.StagingDirectory);

            // The hosted service's ExecuteAsync races CreateClient's return in principle, so give
            // it a moment to run before asserting it is done -- a BackgroundService is started, not
            // awaited to completion, by the host's startup sequence.
            await WaitUntilAsync(() => !File.Exists(orphanPath), TimeSpan.FromSeconds(5));

            Assert.False(File.Exists(orphanPath), "The orphaned staging file should have been swept at startup.");
            Assert.True(File.Exists(bystanderPath), "A same-prefix file outside the staging directory must be untouched.");
        }
        finally
        {
            TryDelete(bystanderPath);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
