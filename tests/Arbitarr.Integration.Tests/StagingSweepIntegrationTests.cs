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
public sealed class StagingSweepIntegrationTests : IDisposable
{
    private readonly ArbitarrWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

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
