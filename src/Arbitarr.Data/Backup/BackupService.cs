using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Arbitarr.Data.Backup;

/// <summary>
/// Produces a backup archive of the instance's durable configuration state (#56): a consistent
/// snapshot of the configuration database plus the per-instance HMAC secret, in one zip.
/// <see cref="BackupArchiveLayout"/> holds what goes in and, importantly, what deliberately does
/// not.
///
/// <para><b>THE SNAPSHOT IS TAKEN THROUGH SQLITE'S OWN BACKUP API, NEVER BY COPYING THE FILE.</b>
/// The database runs in WAL mode with a live writer (the classifier writes continuously), so
/// <c>File.Copy</c> of <c>arbitarr.db</c> captures a torn page image and silently omits everything
/// still in the <c>-wal</c> sidecar. <see cref="SqliteConnection.BackupDatabase"/> is the mechanism
/// SQLite provides for exactly this: it reads a transactionally consistent view and folds the WAL
/// into the destination file, without stopping the process. Copying the three files together is
/// not an equivalent shortcut — it is only correct if nothing writes during the copy, which cannot
/// be guaranteed from inside the running process. <c>BackupServiceTests</c> asserts this claim
/// under concurrent writes rather than assuming it.
/// </para>
///
/// <para>The archive is assembled through a temp path and streamed. It is never written anywhere
/// inside the repository tree and never into a location served statically: the file contains the
/// HMAC secret and, since #53, every configured source's API key.</para>
/// </summary>
public sealed class BackupService
{
    private readonly BackupPaths _paths;
    private readonly TimeProvider _timeProvider;

    public BackupService(BackupPaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The download file name for an archive taken at <paramref name="takenAt"/>: an ISO 8601 UTC
    /// timestamp with the separators removed, since a colon is not a legal file-name character on
    /// Windows and a browser saving the download would otherwise mangle it.
    /// </summary>
    public static string FileNameFor(DateTimeOffset takenAt) =>
        "arbitarr-backup-" +
        takenAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) +
        ".zip";

    /// <summary>
    /// Writes a complete backup archive to <paramref name="destinationPath"/>, overwriting any
    /// file already there. The caller owns the destination and its lifetime.
    /// </summary>
    public async Task WriteArchiveAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        // Arb-3gd: this instance's own BackupPaths.StagingDirectory, not the machine-wide
        // Path.GetTempPath() — see BackupPaths.StagingSubdirectoryName's doc comment.
        var snapshotPath = Path.Combine(
            _paths.EnsureStagingDirectory(),
            "arbitarr-snapshot-" + Guid.NewGuid().ToString("N") + ".db");

        try
        {
            SnapshotDatabase(snapshotPath);
            var migrationId = ReadAppliedMigrationId(snapshotPath);

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await using var archiveStream = new FileStream(
                destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create);

            await CopyIntoArchiveAsync(archive, BackupArchiveLayout.DatabaseEntryName, snapshotPath, cancellationToken)
                .ConfigureAwait(false);

            // The key file is REQUIRED, not best-effort. An archive missing it is rejected on
            // restore (see BackupArchiveValidator) precisely so a half-backup can never be mistaken
            // for a whole one — plan §2's central ruling.
            await CopyIntoArchiveAsync(archive, BackupArchiveLayout.SecretKeyEntryName, _paths.SecretKeyPath, cancellationToken)
                .ConfigureAwait(false);

            await WriteManifestAsync(archive, migrationId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(snapshotPath);
            TryDelete(snapshotPath + "-wal");
            TryDelete(snapshotPath + "-shm");
        }
    }

    /// <summary>
    /// Opens the live database and asks SQLite to back it up into <paramref name="destinationPath"/>.
    /// See the class remarks: this is the whole reason a file copy is not used.
    /// </summary>
    private void SnapshotDatabase(string destinationPath)
    {
        // From DatabaseConnectionStrings, never formatted inline: this names the LIVE database, so
        // its pool is one of the pools a restore must clear before swapping the file. Building the
        // string here instead would create a pool SqlitePoolCleaner does not know about (arb-n21).
        using var source = new SqliteConnection(
            DatabaseConnectionStrings.Maintenance(_paths.DatabasePath));
        source.Open();

        // The DESTINATION is a fresh snapshot file, not the live database, so it is deliberately
        // NOT one of DatabaseConnectionStrings.ForDatabase's shapes — a restore never replaces it.
        // The string is still built by DatabaseConnectionStrings rather than inline here, so that
        // NoInlineDatabaseConnectionStringsTests need not list BackupService among the types
        // allowed to BUILD a connection string. This type is on that test's open-a-connection list
        // and deliberately off its build-a-string one, which is what keeps a future inline string
        // in this file reportable.
        using var destination = new SqliteConnection(
            DatabaseConnectionStrings.SnapshotDestination(destinationPath));
        destination.Open();

        source.BackupDatabase(destination);
    }

    /// <summary>
    /// Reads the newest applied migration id out of a database's own <c>__EFMigrationsHistory</c>
    /// table, or null when it has none. Read from the SNAPSHOT rather than from the live context so
    /// the manifest can never disagree with the file it describes.
    ///
    /// Public because <see cref="BackupArchiveValidator"/> reads the id back out of an uploaded
    /// archive through this same method — one implementation of "which schema is this file at",
    /// used on both the write and the read side, so the two can never drift.
    /// </summary>
    public static string? ReadAppliedMigrationId(string databasePath)
    {
        // From DatabaseConnectionStrings, never formatted inline. This method takes an ARBITRARY
        // path: today's production callers pass a snapshot temp file (SnapshotDatabase's output)
        // and a staged archive's extracted database (BackupArchiveValidator), neither of which a
        // restore replaces — but nothing about the signature stops the LIVE path arriving, and
        // BackupServiceTests already passes it. Taking the string from the one builder makes this
        // site safe whichever path it is handed: if it is the live one, the pool it fills is one
        // SqlitePoolCleaner already knows to clear (arb-n21), and no caller has to know which case
        // it is in.
        using var connection = new SqliteConnection(
            DatabaseConnectionStrings.Maintenance(databasePath));
        connection.Open();

        // Two statements, not one CASE expression. SQLite PREPARES a whole statement before
        // executing any of it, so a single query naming __EFMigrationsHistory inside a
        // never-taken branch still fails to prepare with "no such table" when the table is
        // absent. A database with no history table is a legitimate input here — an archive from
        // before EF migrations, or an empty file — so the existence check must be its own query.
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';";
            if (exists.ExecuteScalar() is null)
            {
                return null;
            }
        }

        using var command = connection.CreateCommand();
        // MigrationId is EF's timestamp-prefixed name, so lexical MAX is chronological.
        command.CommandText = "SELECT MAX(MigrationId) FROM __EFMigrationsHistory;";

        return command.ExecuteScalar() as string;
    }

    private static async Task CopyIntoArchiveAsync(
        ZipArchive archive, string entryName, string sourcePath, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await source.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteManifestAsync(ZipArchive archive, string? migrationId, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(BackupArchiveLayout.ManifestEntryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream);

        // Deliberately NOT a secret-bearing manifest: it names the instant and the schema version
        // and nothing else. The key file's CONTENTS never appear in any text this process writes —
        // BackupSecretExposureTests holds that line with a positive control.
        await writer.WriteLineAsync(
            "Arbitarr configuration backup taken at " +
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) + ".").ConfigureAwait(false);
        await writer.WriteLineAsync("schema=" + (migrationId ?? "unknown")).ConfigureAwait(false);
        await writer.WriteLineAsync(
            "Contains the configuration database and the per-instance release-GUID HMAC secret. " +
            "TREAT THIS FILE AS A CREDENTIAL: it carries source API keys and the secret that " +
            "authenticates every release GUID this instance has issued.").ConfigureAwait(false);
        await writer.WriteLineAsync(
            "The application log database is deliberately not included.").ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
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
            // Best-effort temp cleanup; a locked file must not fail an otherwise successful backup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
