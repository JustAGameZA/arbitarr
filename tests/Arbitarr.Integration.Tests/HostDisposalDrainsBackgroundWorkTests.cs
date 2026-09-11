using Arbitarr.Data.Backup;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-rwhb: disposing a test host must not leave background work still WRITING to the config
/// directory.
///
/// <para><b>The defect this pins.</b> <c>MaintenanceHostedService</c> (Program.cs:783, registered
/// unconditionally) runs its first maintenance pass IMMEDIATELY — <c>ExecuteAsync</c> calls
/// <c>RunAutomaticBackupAsync</c> before its first <c>Task.Delay(interval)</c>, so the configured
/// interval never defers it — and the automatic backup is on by default
/// (<c>AutomaticBackupRetainedCount</c> = 7). That reaches <c>BackupService.SnapshotDatabase</c>,
/// which opens a pooled connection to the live database and calls the blocking
/// <c>SqliteConnection.BackupDatabase</c>, on a detached <c>BackgroundService</c> task nothing
/// awaited. Disposal then ran while that copy was still in flight, and the orphaned task's
/// continuation resolved from a disposed provider — which at <c>maxParallelThreads: 4</c> surfaced
/// on whichever unrelated test was running beside it (twice on <c>ConfigMaskingTests</c>, both
/// dying before evaluating a single assertion).</para>
///
/// <para><b>Why this asserts on QUIESCENCE and not on the directory being deleted.</b> The obvious
/// assertion — "disposal removed the directory" — would be wrong here, and wrong in the dangerous
/// direction: it would fail for a reason this change is not responsible for. These factories have
/// never been able to delete their config directories at all (arb-dhua: ~22,000 leaked under
/// <c>%TEMP%</c>, and an untouched pre-existing test leaks one while passing green). The
/// <c>arbitarr.db</c> handle is held for the test-process lifetime because every
/// <c>ArbitarrDbContext</c> holds a connection EF has CHECKED OUT of the pool, and a pool clear
/// closes only idle RETURNED connections — so deletion is not achievable in-process by any amount
/// of draining. Tying this test to deletion would make it red for arb-dhua's reason while saying
/// nothing about arb-rwhb's.</para>
///
/// <para>What the fix actually establishes is that once disposal returns, nothing is still writing:
/// the host has been stopped and awaited, and a backup that has not yet entered
/// <c>SnapshotDatabase</c> declines to start rather than beginning a copy that shutdown would pull
/// the files out from under. That is directly observable — sample the directory, wait, sample
/// again, and require the two to match.</para>
///
/// <para><b>Why not assert "no ObjectDisposedException was thrown here".</b> The orphaned work
/// faults someone ELSE's test, never its own, so such an assertion would pass in both the fixed and
/// the broken world and prove nothing. Quiescence is the property that is decidable locally.</para>
///
/// <para><b>MUTATION-PROVED</b> (CLAUDE.md §4), outside the repository, in a throwaway copy holding
/// both implementations — no mutation was made in this repository and none is left behind. With
/// <c>cancellationToken.ThrowIfCancellationRequested()</c> removed from
/// <c>BackupService.WriteArchiveAsync</c> AND the factory ordering reverted (<c>DisposeAsync</c>
/// not overridden, <c>Dispose</c> deleting once inside <c>catch (IOException)</c>), this class
/// FAILS, reporting a <c>backup-staging/arbitarr-snapshot-*.db</c> of 212,992 bytes left behind and
/// <c>arbitarr-logs.db-wal</c> growing from 0 to 16,512 bytes AFTER disposal returned. The fixed
/// build passes. Detection was 3 of 3 runs at the current
/// <see cref="QuiesceWindow"/>; at 2 s it was 3 of 4 — see that field for why the window is the
/// size it is.</para>
///
/// <para><b>The synchronous path is the sensitive one</b>, and that is a property of the defect
/// rather than an accident of the harness: under mutation <c>The_synchronous_disposal_path…</c>
/// failed on every detecting run while the async one failed on some. <c>base.DisposeAsync()</c>
/// already stops and awaits hosted services, so the async path was partly protected even before
/// this fix; <c>base.Dispose</c> cannot await, which is exactly where the drain was missing. Do not
/// "simplify" by collapsing the two tests into one — the async case alone would have let the
/// mutation through.</para>
/// </summary>
public sealed class HostDisposalDrainsBackgroundWorkTests
{
    /// <summary>
    /// How long to watch for a post-disposal write before declaring the directory quiet.
    ///
    /// <para>FIVE SECONDS IS MEASURED, NOT GUESSED, and shortening it weakens this test silently
    /// rather than breaking it — which is why the number is justified here. Against the mutated
    /// build (see the class remarks) a 2 s window detected the defect in only 3 of 4 runs, and only
    /// ever through the SYNCHRONOUS path; at 5 s it detected it in 3 of 3 and caught the
    /// asynchronous path as well. The orphaned writes arrive in bursts — a staging snapshot, then
    /// WAL traffic to <c>arbitarr-logs.db</c> — so a narrow window lands between two of them and
    /// reports quiet.</para>
    ///
    /// <para>The cost is real and accepted: this is dead time on every green run. It buys an
    /// assertion that actually fails when the defect comes back, which the cheaper window did
    /// not.</para>
    /// </summary>
    private static readonly TimeSpan QuiesceWindow = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_disposed_host_stops_writing_to_its_config_directory()
    {
        var factory = new ArbitarrWebApplicationFactory();
        var configDirectory = factory.ConfigDirectory;

        // Start the host. This is what sets MaintenanceHostedService running, and with it the
        // immediate first backup pass.
        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        // NON-VACUITY, and the reason this is not merely a sleep: wait until the automatic backup
        // has actually produced an archive. Until one exists no backup has run, disposal would have
        // nothing to race, and the directory would sit still for reasons that have nothing to do
        // with the fix — passing in the broken world too. Reaching this assertion proves the window
        // this test exists to close was genuinely open.
        var backupDirectory = Path.Combine(configDirectory, BackupPaths.BackupSubdirectoryName);
        Assert.True(
            await WaitForAutomaticArchiveAsync(backupDirectory),
            $"No automatic backup archive appeared under {BackupPaths.BackupSubdirectoryName}/ within the timeout, " +
            "so this test never opened the window it exists to close. Check that MaintenanceHostedService " +
            "still runs its first pass before the first delay and that AutomaticBackupRetainedCount still defaults above 0.");

        await factory.DisposeAsync();

        // THE PROPERTY: disposal drained the host, so nothing is writing any more. Under the
        // pre-fix ordering the detached backup was still copying the database here.
        await AssertDirectoryIsQuiescentAsync(configDirectory);
    }

    [Fact]
    public async Task The_synchronous_disposal_path_also_stops_writing()
    {
        // Both shapes are in live use — `await using` in AdminBackupEndpointsTests and
        // AdminSettingsEndpointsTests, plain `using` in OllamaClassificationErrorStatusTests, and
        // xunit's own fixture disposal for the ~29 injected class fixtures. Fixing only the async
        // path would leave the race live for every caller on the sync one, so it is pinned
        // separately rather than assumed to follow.
        var factory = new ArbitarrWebApplicationFactory();
        var configDirectory = factory.ConfigDirectory;

        using (var client = factory.CreateClient())
        {
            await client.GetAsync("/api/status");
        }

        var backupDirectory = Path.Combine(configDirectory, BackupPaths.BackupSubdirectoryName);
        Assert.True(
            await WaitForAutomaticArchiveAsync(backupDirectory),
            "No automatic backup archive appeared, so the synchronous path was never put under the race either.");

        // Deliberately Dispose(), not DisposeAsync(): this is the path xunit uses for a fixture it
        // disposes synchronously, and the one that cannot await hosted services.
        factory.Dispose();

        await AssertDirectoryIsQuiescentAsync(configDirectory);
    }

    /// <summary>
    /// POSITIVE CONTROL for the two tests above (CLAUDE.md §4). A quiescence assertion is exactly
    /// the shape that passes vacuously: <see cref="AssertDirectoryIsQuiescentAsync"/> would be just
    /// as green if <see cref="Snapshot"/> were blind — if it missed a new file, a growing file, or
    /// a touched one — and the tests above would then prove nothing about disposal at all.
    ///
    /// <para>So this drives the sampler against a directory that IS being written to, in the three
    /// shapes the real defect produces, and requires it to object to each one. Only once the
    /// sampler is shown to bite does its silence after disposal carry information.</para>
    /// </summary>
    [Fact]
    public async Task The_quiescence_check_detects_a_directory_that_is_still_being_written_to()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "arbitarr-quiescence-control",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var nested = Path.Combine(directory, BackupPaths.BackupSubdirectoryName);
            Directory.CreateDirectory(nested);
            var existing = Path.Combine(nested, "auto-existing.zip");
            await File.WriteAllTextAsync(existing, "seed");

            // 1. A NEW file appears — a backup archive being written after disposal.
            await AssertQuiescenceFailsAsync(
                directory,
                async () => await File.WriteAllTextAsync(Path.Combine(nested, "auto-new.zip"), "x"),
                "a new file");

            // 2. An EXISTING file GROWS — a snapshot copy still in progress, which is what
            // SqliteConnection.BackupDatabase actually looks like on disk.
            await AssertQuiescenceFailsAsync(
                directory,
                async () => await File.AppendAllTextAsync(existing, "more bytes"),
                "a file that grew");

            // 3. An existing file is REWRITTEN at the same length, moving only its write time.
            // Caught only because the snapshot carries the write time as well as the size; without
            // that, this case would slip through silently.
            await AssertQuiescenceFailsAsync(
                directory,
                () =>
                {
                    File.SetLastWriteTimeUtc(existing, File.GetLastWriteTimeUtc(existing).AddSeconds(30));
                    return Task.CompletedTask;
                },
                "a file whose write time moved");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // This control directory holds no SQLite handles, so it normally deletes cleanly;
                // tolerated anyway, since cleanup must never fail an otherwise-green run.
            }
        }
    }

    /// <summary>
    /// Runs the quiescence check over a directory that <paramref name="write"/> mutates while the
    /// check is watching, and requires it to FAIL. The write is scheduled shortly after the first
    /// sample so that it lands inside the window rather than before it.
    /// </summary>
    private static async Task AssertQuiescenceFailsAsync(string directory, Func<Task> write, string what)
    {
        var writing = Task.Run(async () =>
        {
            await Task.Delay(200);
            await write();
        });

        await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(
            () => AssertDirectoryIsQuiescentAsync(directory));

        await writing;

        // Guards the guard: had the control write faulted, the assertion above could have been
        // satisfied by the wrong thing. Awaiting it, rather than discarding it, surfaces that.
        Assert.True(writing.IsCompletedSuccessfully, $"The control write ({what}) did not complete.");
    }

    /// <summary>
    /// Samples the directory, waits, samples again, and requires the two to be identical — i.e.
    /// nothing wrote to it during the window. Compares the relative path, length and last write
    /// time of every file beneath it, so a new file, a growing file and a rewritten file are all
    /// visible (each is exercised by the positive control above).
    /// </summary>
    private static async Task AssertDirectoryIsQuiescentAsync(string directory)
    {
        var before = Snapshot(directory);

        await Task.Delay(QuiesceWindow);

        var after = Snapshot(directory);

        Assert.True(
            before.SequenceEqual(after),
            "The config directory was still being written to after disposal returned, which means background work " +
            "outlived the host — the arb-rwhb defect. Disposal must stop and await the host before returning, and a " +
            "backup that has not entered SnapshotDatabase must decline to start once the token is signalled.\n" +
            $"  Before: {Describe(before)}\n" +
            $"  After:  {Describe(after)}");
    }

    /// <summary>
    /// The observable state of the directory: every file beneath it with its size and write time.
    /// Ordered, so two snapshots of an unchanged directory compare equal regardless of enumeration
    /// order. A missing directory yields an empty snapshot rather than throwing: since arb-dhua was
    /// fixed (#230) the delete now succeeds, so that is the usual outcome here — but this
    /// deliberately tolerates either, because quiescence and deletion are pinned separately
    /// (<see cref="ConfigDirectoryIsDeletedOnDisposalTests"/> owns deletion) and this class must
    /// stay green on the property it actually asserts regardless of what the delete did.
    /// </summary>
    private static string[] Snapshot(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return $"{Path.GetRelativePath(directory, path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                })
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A file vanishing mid-enumeration is itself evidence of a write in flight. Returning a
            // distinctive marker rather than throwing keeps that a FAILED assertion with a readable
            // message instead of an opaque exception out of the sampler.
            return [$"<enumeration failed: {ex.GetType().Name}>"];
        }
    }

    private static string Describe(string[] snapshot) =>
        snapshot.Length == 0 ? "(empty)" : string.Join(", ", snapshot);

    /// <summary>
    /// Polls for the automatic backup's archive. Polling rather than sleeping a fixed span: the
    /// backup's duration is not ours to predict, and a sleep long enough to be safe on a loaded CI
    /// agent would be dead time on every run. Returns false on timeout so the caller can fail with
    /// a message about the WINDOW rather than about the property.
    /// </summary>
    private static async Task<bool> WaitForAutomaticArchiveAsync(string backupDirectory)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(backupDirectory) &&
                Directory.EnumerateFiles(backupDirectory, BackupPaths.AutomaticFilePrefix + "*.zip").Any())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}
