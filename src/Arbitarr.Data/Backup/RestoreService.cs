using Microsoft.Data.Sqlite;

namespace Arbitarr.Data.Backup;

/// <summary>What a restore attempt did, and what the operator must do next.</summary>
/// <param name="Succeeded">False when validation refused the archive; nothing was touched.</param>
/// <param name="Message">Operator-facing outcome. Never carries file contents.</param>
/// <param name="Failure">The validation failure, when <paramref name="Succeeded"/> is false.</param>
/// <param name="PreRestoreBackupTaken">
/// True when the pre-restore safety copy was written. A restore never applies anything without
/// this first, so on a successful restore it is always true — it is reported so the UI can state
/// the guarantee as a fact rather than as a promise.
/// </param>
/// <param name="RestartRequested">
/// True when the host was asked to stop so the container's restart policy brings it back on the
/// restored state. See <c>RestoreCoordinator</c> for why a restart is the mechanism and not a
/// nicety.
/// </param>
public sealed record RestoreResult(
    bool Succeeded,
    string Message,
    BackupValidationFailure? Failure,
    bool PreRestoreBackupTaken,
    bool RestartRequested);

/// <summary>
/// Applies a validated backup archive over the live configuration state (#56, plan §3.2). This is
/// the destructive half of the feature and its ordering is the design:
///
/// <list type="number">
///   <item>Validate the archive COMPLETELY (<see cref="BackupArchiveValidator"/>). A refusal here
///   touches nothing at all, which is what makes an invalid upload safe rather than merely
///   detected.</item>
///   <item>Take a pre-restore backup of the CURRENT state, so a mistaken restore is itself
///   recoverable. Written under the config directory, never anywhere served.</item>
///   <item>Extract the archive's two files over the live ones.</item>
/// </list>
///
/// <para><b>THE REPLACED FILES INCLUDE THE HMAC SECRET, AND THAT IS NOT A DETAIL.</b> Restoring an
/// older <c>release-guid-secret.key</c> invalidates every release GUID issued since that backup was
/// taken — Sonarr and Radarr hold proxy guids computed from the old secret and will stop matching
/// anything they previously saw from this instance. The API surface requires a typed confirmation
/// naming this before it will call in here; this class does not restate that gate, it assumes the
/// caller passed it.</para>
///
/// <para><b>ON DISK ONLY.</b> This type writes files and does not touch the running process's
/// state: the DbContext pool still holds connections to the file it replaced, and
/// <c>ReleaseGuid</c> still holds the OLD secret in memory from startup. Making the running process
/// adopt restored state is <c>RestoreCoordinator</c>'s job, and it does it the only way that is
/// actually verifiable — by stopping the host.</para>
/// </summary>
public sealed class RestoreService
{
    /// <summary>
    /// Serialises the whole validate -> safety copy -> apply sequence to ONE restore at a time.
    ///
    /// <para><b>WITHOUT THIS, TWO CONCURRENT RESTORES CORRUPT EACH OTHER, AND NOT MERELY ONE OF
    /// THEM.</b> This type is a singleton and <see cref="BackupPaths.PreRestorePath"/> is a FIXED
    /// filename, so two overlapping calls collide in three separate ways: both write the same
    /// safety-copy path, and the second write opens it with <c>FileShare.None</c> so one caller
    /// takes an IOException from a file the other is mid-write; worse, if B takes its safety copy
    /// after A has begun replacing the live files, B's "previous state" captures A's HALF-APPLIED
    /// state - so the one artefact that exists to make a mistaken restore recoverable becomes a
    /// snapshot of a broken instance. That is silent, and it is discovered only when someone tries
    /// to use it.</para>
    ///
    /// <para>Held across all three steps rather than around the apply alone: the safety copy is only
    /// meaningful if it describes the state the apply is about to replace, which is exactly the
    /// invariant an interleaving breaks. Callers that cannot take it immediately are told so
    /// (<see cref="TryRestoreAsync"/>) rather than queued - a restore stops the host, so a queued
    /// second one would be applied to a process on its way down.</para>
    /// </summary>
    private readonly SemaphoreSlim _restoreGate = new(1, 1);

    private readonly BackupPaths _paths;
    private readonly BackupService _backupService;

    public RestoreService(BackupPaths paths, BackupService backupService)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    }

    /// <summary>
    /// Validates and applies the archive at <paramref name="archivePath"/>. Returns a failed
    /// result (having changed nothing) when validation refuses it.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(
        string archivePath,
        IReadOnlyCollection<string> knownMigrationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        await _restoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RestoreCoreAsync(archivePath, knownMigrationIds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    /// <summary>
    /// Runs a restore only if no other restore is in flight. Returns null when one is - the caller
    /// turns that into a 409 rather than waiting.
    ///
    /// <para>Not queued, deliberately: a successful restore stops the host, so a second restore that
    /// waited its turn would be applied to a process already shutting down, and its result reported
    /// to an operator whose instance is about to vanish. Refusing immediately is the honest
    /// answer.</para>
    /// </summary>
    public async Task<RestoreResult?> TryRestoreAsync(
        string archivePath,
        IReadOnlyCollection<string> knownMigrationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!await _restoreGate.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            return await RestoreCoreAsync(archivePath, knownMigrationIds, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _restoreGate.Release();
        }
    }

    /// <summary>
    /// The sequence itself. Only ever entered with <see cref="_restoreGate"/> held - see its
    /// remarks for what an interleaving does to the pre-restore safety copy.
    /// </summary>
    private async Task<RestoreResult> RestoreCoreAsync(
        string archivePath,
        IReadOnlyCollection<string> knownMigrationIds,
        CancellationToken cancellationToken)
    {
        using var validation = BackupArchiveValidator.Validate(archivePath, knownMigrationIds);
        if (!validation.IsValid)
        {
            // Nothing has been written at this point, and nothing will be. The pre-restore backup
            // is deliberately NOT taken for a refused archive: it would be a copy of a state that
            // is about to remain exactly as it is.
            return new RestoreResult(
                Succeeded: false,
                Message: validation.Message,
                Failure: validation.Failure,
                PreRestoreBackupTaken: false,
                RestartRequested: false);
        }

        // Step 2 before step 3, always. If this throws, the restore does not proceed — an
        // unrecoverable restore is worse than a refused one.
        await _backupService.WriteArchiveAsync(_paths.PreRestorePath, cancellationToken).ConfigureAwait(false);

        // The VALIDATED files, never the uploaded zip. See ApplyValidatedFiles.
        ApplyValidatedFiles(validation.StagedDatabasePath!, validation.StagedSecretKeyPath!);

        // The wording is deliberate and is the honest form of plan AC6. This process can guarantee
        // that it STOPS; it cannot guarantee that anything starts it again. Under the shipped
        // deployment it does (docker-compose.yml declares restart: unless-stopped), but under a bare
        // `dotnet run` it does not — so the copy states the condition instead of promising a restart
        // that a given deployment may not provide.
        return new RestoreResult(
            Succeeded: true,
            Message:
                "Restore applied. A pre-restore backup of the previous state was saved as " +
                BackupPaths.BackupSubdirectoryName + "/" + BackupPaths.PreRestoreFileName +
                " in the config directory. Arbitarr is shutting down now so the restored " +
                "configuration and release-GUID secret are loaded; it will come back automatically " +
                "if your deployment restarts it (the supplied compose file sets " +
                "restart: unless-stopped). If it does not come back on its own, start it again.",
            Failure: null,
            PreRestoreBackupTaken: true,
            RestartRequested: true);
    }

    /// <summary>
    /// Puts the two ALREADY-VALIDATED files over the live ones.
    ///
    /// <para><b>THE UPLOADED ZIP IS NEVER RE-OPENED HERE, AND THAT IS A SECURITY PROPERTY.</b> An
    /// earlier shape validated the archive, closed it, then re-opened the same path to extract. That
    /// is a time-of-check-to-time-of-use window: the staged upload sits in the system temp directory
    /// between the two reads, so what was validated and what was applied need not be the same bytes.
    /// The validator now extracts once, under a decompression bound, and hands back the files it
    /// actually checked; this method only moves them. There is no second read to disagree with the
    /// first.</para>
    ///
    /// <para><b>THE CONNECTION POOL IS CLEARED FIRST, AND WITHOUT THAT THIS CANNOT WORK.</b>
    /// Microsoft.Data.Sqlite pools connections: disposing a <see cref="SqliteConnection"/> returns
    /// its native handle to the pool rather than closing the file. On Windows those handles hold a
    /// share lock, so overwriting <c>arbitarr.db</c> fails outright with a sharing violation - and
    /// on Linux, where the overwrite would be permitted, it is worse: the pooled handles keep
    /// reading the replaced inode, so the process would carry on serving the OLD database while the
    /// new one sat on disk looking applied. <see cref="SqlitePoolCleaner.ClearPoolsFor"/> closes
    /// them for real, which is the only reason the replacement below is safe to attempt at all.
    ///
    /// <para>It clears the pool for EVERY connection string naming this database
    /// (<see cref="DatabaseConnectionStrings.ForDatabase"/>), not just the one the application
    /// itself opens: pools are keyed by the full connection string, so a second string naming the
    /// same file is a second pool that clearing the first does not touch. It is deliberately NOT
    /// <see cref="SqliteConnection.ClearAllPools"/>, which would also close the log store's
    /// connections and every other unrelated pool in the process — the over-reach behind
    /// arb-cbc/arb-5ba (arb-n21).</para>
    ///
    /// <para>The WAL and shared-memory sidecars of the OLD database are then deleted alongside it.
    /// Leaving them beside a replaced main file is the one way this operation can corrupt rather
    /// than restore: SQLite would try to recover the previous database's journal onto the new
    /// file's pages. The restored snapshot came out of <see cref="SqliteConnection.BackupDatabase"/>
    /// with its WAL already folded in, so it needs no sidecars of its own.</para>
    ///
    /// <para><b>THE TWO REPLACEMENTS ARE NARROWED TO BACK-TO-BACK RENAMES, NOT ELIMINATED.</b> Both
    /// files are first copied into place under temporary names beside their targets - the slow,
    /// failure-prone part, and one that touches neither live file - and only then moved over the
    /// originals. A crash during the copies leaves the live pair untouched and two stray temp files;
    /// a crash between the two moves is the residual window, and it leaves a restored database
    /// beside the previous secret. That window is two rename calls wide rather than two file copies
    /// wide, which is as far as this can be taken without a transactional filesystem. It is not
    /// silent: the RECOVERY PATH is the pre-restore safety copy written moments earlier at
    /// <see cref="BackupPaths.PreRestorePath"/>, which holds the matching pair and restores the
    /// instance to its pre-restore state.</para>
    ///
    /// <para>Replacing the files does not make the running process adopt them - EF's scoped
    /// contexts and the in-memory release-GUID secret are still the old ones. That is
    /// <c>RestoreCoordinator</c>'s job, and it does it by restarting; see
    /// <c>docs/adr/0007-restart-rather-than-reload-after-restore.md</c>.</para>
    /// </summary>
    private void ApplyValidatedFiles(string stagedDatabasePath, string stagedSecretKeyPath)
    {
        var incomingDatabase = _paths.DatabasePath + ".incoming";
        var incomingSecretKey = _paths.SecretKeyPath + ".incoming";

        try
        {
            // Copy beside the targets first. Same volume as the destination, so the moves below are
            // renames rather than another copy - a rename is the closest this gets to atomic.
            File.Copy(stagedDatabasePath, incomingDatabase, overwrite: true);
            File.Copy(stagedSecretKeyPath, incomingSecretKey, overwrite: true);

            SqlitePoolCleaner.ClearPoolsFor(_paths.DatabasePath);

            TryDelete(_paths.DatabasePath + "-wal");
            TryDelete(_paths.DatabasePath + "-shm");

            // The narrow window: these two are adjacent on purpose and nothing may be added between
            // them.
            File.Move(incomingDatabase, _paths.DatabasePath, overwrite: true);
            File.Move(incomingSecretKey, _paths.SecretKeyPath, overwrite: true);
        }
        finally
        {
            // A failure before the moves must not leave half-written files loitering in the config
            // directory looking like a restore that partly happened.
            TryDelete(incomingDatabase);
            TryDelete(incomingSecretKey);
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
            // A sidecar still held open by a live connection cannot be removed here; the restart
            // that follows releases it, and SQLite recovers consistently from a checkpointed WAL.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
