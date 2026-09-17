using Arbitarr.Data.Backup;
using Arbitarr.Host.Backup;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-07jl: <see cref="StagingSweepService"/> does not judge the staging directory until the first
/// maintenance pass has finished, so the automatic backup that pass takes immediately cannot have
/// its snapshot deleted while <c>BackupService.WriteArchiveAsync</c> is still copying into it.
///
/// <para><b>Why the processStartUtc cut-off could not cover this on its own.</b> The cut-off skips
/// any file whose last write is at or after the instant the sweep captured. A snapshot the startup
/// backup created a moment EARLIER, and has not written to since because it is mid-copy, is older
/// than that instant and so reads as an orphan from a previous process. On Linux deleting it while
/// open surfaces as SQLite error 5898 (<c>SQLITE_IOERR_DELETE</c>) failing the startup backup; on
/// Windows the file-sharing rules hide it, which is why this defect was only ever seen in CI and
/// has no deterministic repro. The cut-off is unchanged and still covers every later writer — these
/// tests pin the ordering that covers the earlier one.</para>
///
/// <para><b>The ordering test's positive control comes FIRST and shares the harness.</b> "The file
/// is still there while the pass is in flight" passes just as happily when the sweep never runs at
/// all, for any reason — a wrong staging path, a prefix that does not match, a service that threw
/// on its first line. So the same file, in the same directory, written by the same helper, is first
/// shown to BE deleted once the gate opens. Only that makes the presence assertion bite.</para>
///
/// <para><b>No <c>Task.Delay</c> as synchronisation.</b> The gate is a
/// <see cref="TaskCompletionSource"/> this test opens deliberately, and the clock is a
/// <see cref="FakeTimeProvider"/>, so every "has it happened yet" is answered at an instant the test
/// controls. Where a wait is unavoidable it is bounded by <see cref="CompletionWait"/> and its
/// elapsing FAILS the test naming what did not happen, never a silent continue.</para>
/// </summary>
public sealed class StagingSweepServiceFirstPassOrderingTests : IDisposable
{
    /// <summary>
    /// The bound on every wait here. Generous on purpose: it exists to turn a HANG into a named
    /// failure rather than to police how fast a sweep runs on a loaded parallel runner.
    /// </summary>
    private static readonly TimeSpan CompletionWait = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-sweep-order-" + Guid.NewGuid().ToString("N"));

    private readonly BackupPaths _paths;

    public StagingSweepServiceFirstPassOrderingTests()
    {
        _paths = new BackupPaths(_configDirectory);
        Directory.CreateDirectory(_paths.StagingDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDirectory))
            {
                Directory.Delete(_configDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task A_snapshot_older_than_now_survives_while_the_first_pass_is_in_flight_and_is_swept_once_it_completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // POSITIVE CONTROL, first: with the gate ALREADY open, this exact file -- same prefix, same
        // directory, same write time -- is deleted. Without this, the in-flight assertion below
        // could pass because the sweep never touches this file under any conditions at all.
        var control = PlantSnapshotOlderThanNow();
        var alreadyOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alreadyOpen.SetResult();

        using (var service = CreateService(token => alreadyOpen.Task.WaitAsync(token)))
        {
            await service.StartAsync(CancellationToken.None);
            await WaitForExecuteAsync(service);
        }

        Assert.False(
            File.Exists(control),
            "Positive control: with the first pass already complete, the sweep must delete a snapshot older than its cut-off. " +
            "It did not, so the in-flight assertion below would be vacuous.");

        // The real property: the same file, but the pass is HELD OPEN, standing in for the startup
        // automatic backup still copying into this very snapshot.
        var inFlight = PlantSnapshotOlderThanNow();

        using var gated = CreateService(token => gate.Task.WaitAsync(token));
        await gated.StartAsync(CancellationToken.None);

        Assert.True(
            File.Exists(inFlight),
            "The sweep ran while the first maintenance pass was still in flight and deleted its snapshot mid-copy.");

        gate.SetResult();
        await WaitForExecuteAsync(gated);

        Assert.False(
            File.Exists(inFlight),
            "Once the first pass completed the sweep should have run and reclaimed the orphan; the wait must delay the sweep, not cancel it.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_faulted_or_cancelled_first_pass_still_lets_the_sweep_run_without_throwing(bool faulted)
    {
        // Neither outcome leaves a snapshot open, so both mean "nothing of the first pass is still
        // writing" exactly as a clean completion does. Gating the sweep on the pass having SUCCEEDED
        // would strand the orphan on precisely the runs that produced one.
        var orphan = PlantSnapshotOlderThanNow();

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (faulted)
        {
            waiter.SetException(new InvalidOperationException("the first pass threw"));
        }
        else
        {
            waiter.SetCanceled();
        }

        using var service = CreateService(_ => waiter.Task);
        await service.StartAsync(CancellationToken.None);

        // ExecuteAsync itself must not surface the fault: a BackgroundService that throws takes the
        // host down with it, and a failed maintenance pass is not a reason to refuse to start. The
        // completion is awaited through a continuation rather than directly, so a fault is INSPECTED
        // here and reported as this assertion rather than rethrown as an unrelated test error.
        var execute = service.ExecuteTask;
        Assert.NotNull(execute);
        await execute!.ContinueWith(static _ => { }, TaskScheduler.Default).WaitAsync(CompletionWait);

        Assert.True(
            execute.Exception is null,
            $"ExecuteAsync must absorb a faulted or cancelled first pass, but it surfaced: {execute.Exception}");

        Assert.False(
            File.Exists(orphan),
            "A first pass that faulted or was cancelled holds no snapshot open, so the sweep must still reclaim the orphan.");
    }

    [Fact]
    public async Task Stopping_the_host_while_the_wait_is_pending_ends_promptly_without_sweeping()
    {
        // A shutdown mid-backup IS the hard kill that leaves the orphan behind, and the next start
        // sweeps it with a cut-off that unambiguously post-dates it. Sweeping on the way down would
        // be the same mid-copy delete this fix exists to prevent.
        var orphan = PlantSnapshotOlderThanNow();

        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var service = CreateService(token => neverCompletes.Task.WaitAsync(token));
        await service.StartAsync(CancellationToken.None);

        // StopAsync cancels the stopping token and awaits ExecuteAsync; that it RETURNS is the
        // "promptly" assertion, bounded so a wedge fails by name instead of hanging the run.
        await service.StopAsync(CancellationToken.None).WaitAsync(CompletionWait);

        var execute = service.ExecuteTask;
        Assert.NotNull(execute);
        Assert.Null(execute!.Exception);

        Assert.True(
            File.Exists(orphan),
            "The host stopped before the first pass completed, so the sweep must be skipped rather than run against a directory a shutdown may still be writing.");

        service.Dispose();

        // Releasing the waiter after disposal proves nothing was left observing it: an unobserved
        // fault here would be raised on the finalizer thread, not in this test, so the value of the
        // line is that the waiter is completed rather than abandoned mid-await.
        neverCompletes.SetResult();
    }

    private StagingSweepService CreateService(Func<CancellationToken, Task> waitForFirstPass) =>
        new(_paths,
            new FakeTimeProvider(Now),
            waitForFirstPass,
            NullLogger<StagingSweepService>.Instance);

    /// <summary>
    /// A snapshot-prefixed file whose last write is BEFORE the instant the sweep will capture -- the
    /// shape of a backup created a moment before the sweep started and not written to since because
    /// the copy into it is still in progress. This is the file the cut-off alone does not protect.
    /// </summary>
    private string PlantSnapshotOlderThanNow()
    {
        var path = Path.Combine(
            _paths.StagingDirectory,
            StagingFileNames.SnapshotPrefix + Guid.NewGuid().ToString("N") + ".db");

        File.WriteAllText(path, "a snapshot the startup automatic backup is still copying into");
        File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddMinutes(-1));

        return path;
    }

    /// <summary>
    /// Awaits the service's own <c>ExecuteAsync</c> task, bounded. <c>StartAsync</c> returns at that
    /// task's first await, so it is never evidence the sweep has run.
    /// </summary>
    private static async Task<Task> WaitForExecuteAsync(StagingSweepService service)
    {
        var execute = service.ExecuteTask;
        Assert.NotNull(execute);

        await execute!.WaitAsync(CompletionWait);

        return execute;
    }
}
