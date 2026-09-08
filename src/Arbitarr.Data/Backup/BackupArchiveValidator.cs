using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Arbitarr.Data.Backup;

/// <summary>
/// Why an uploaded archive was rejected. A distinct value per failure so the UI can say which of
/// the four things went wrong rather than "invalid backup" — plan §5 requires four distinct
/// messages, and a single opaque one would leave an operator guessing which half of the archive
/// they broke.
/// </summary>
public enum BackupValidationFailure
{
    /// <summary>The upload is not a readable zip archive at all.</summary>
    NotAnArchive,

    /// <summary>The archive is readable but is missing the database or the secret key entry.</summary>
    MissingEntry,

    /// <summary>The database entry is present but is not a usable SQLite file.</summary>
    CorruptDatabase,

    /// <summary>
    /// An entry is larger than <see cref="BackupArchiveValidator.MaxEntryBytes"/>, or its stream
    /// kept producing bytes past that bound. A decompression bomb, or a backup from an instance far
    /// larger than this feature is scoped for; either way it is refused before it can fill the disk.
    /// </summary>
    EntryTooLarge,

    /// <summary>
    /// The database is readable but was taken from a build with a NEWER schema than this one.
    /// Refused rather than applied: the running build cannot read a schema it has no migrations
    /// for, and the failure would otherwise surface later and somewhere else.
    /// </summary>
    SchemaTooNew,
}

/// <summary>The outcome of validating an uploaded archive, before anything has been applied.</summary>
/// <param name="Failure">Null when the archive is valid.</param>
/// <param name="Message">Operator-facing explanation. Never carries file contents.</param>
/// <param name="ArchiveMigrationId">The migration id found in the archive, when it could be read.</param>
/// <param name="StagedDatabasePath">
/// Where the validated database was extracted, or null on failure. The caller OWNS this file and
/// must delete it; <see cref="BackupValidationResult.Dispose"/> does so.
/// </param>
/// <param name="StagedSecretKeyPath">The validated key file, same ownership.</param>
public sealed record BackupValidationResult(
    BackupValidationFailure? Failure,
    string Message,
    string? ArchiveMigrationId,
    string? StagedDatabasePath = null,
    string? StagedSecretKeyPath = null) : IDisposable
{
    public bool IsValid => Failure is null;

    /// <summary>
    /// Deletes the staged files. Safe to call twice and safe on a failed result, which has none.
    /// </summary>
    public void Dispose()
    {
        BackupArchiveValidator.TryDelete(StagedDatabasePath);
        BackupArchiveValidator.TryDelete(StagedSecretKeyPath);
    }
}

/// <summary>
/// Validates an uploaded backup archive COMPLETELY BEFORE a restore applies any of it (#56, plan
/// §3.2). A restore that half-applies leaves an unusable database, so every check that can be made
/// against the staged copy is made here first and the restore proceeds only on a clean result.
///
/// <para><b>The messages never quote file contents.</b> The archive holds the HMAC secret and
/// source API keys; a validator that echoed the bytes it rejected would publish them into an API
/// response and the log store. Messages name entries, schema ids and shapes only —
/// <c>BackupSecretExposureTests</c> holds that line with a positive control.</para>
/// </summary>
public static class BackupArchiveValidator
{
    /// <summary>
    /// Ceiling on a single DECOMPRESSED entry. The upload cap bounds the compressed bytes only, and
    /// a zip of a few hundred megabytes of zeros expands to orders of magnitude more, so without a
    /// bound here a bounded upload still fills the config volume.
    ///
    /// 512 MB is far above any real Arbitarr configuration database (single-digit megabytes) and
    /// far below anything that could exhaust a sane volume.
    /// </summary>
    public const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Validates the archive at <paramref name="archivePath"/> against the migrations
    /// <paramref name="knownMigrationIds"/> the running build can apply.
    /// </summary>
    public static BackupValidationResult Validate(string archivePath, IReadOnlyCollection<string> knownMigrationIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(knownMigrationIds);

        var stem = Path.Combine(
            Path.GetTempPath(), "arbitarr-restore-validate-" + Guid.NewGuid().ToString("N"));
        var stagedDatabase = stem + ".db";
        var stagedSecretKey = stem + ".key";
        var succeeded = false;

        try
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(archivePath);
            }
            catch (InvalidDataException)
            {
                return new BackupValidationResult(
                    BackupValidationFailure.NotAnArchive,
                    "That file is not a readable Arbitarr backup archive. A backup is the .zip this " +
                    "page produces; upload it unmodified and unextracted.",
                    null);
            }

            using (archive)
            {
                var databaseEntry = archive.GetEntry(BackupArchiveLayout.DatabaseEntryName);
                var secretEntry = archive.GetEntry(BackupArchiveLayout.SecretKeyEntryName);

                if (databaseEntry is null || secretEntry is null)
                {
                    var missing = databaseEntry is null
                        ? BackupArchiveLayout.DatabaseEntryName
                        : BackupArchiveLayout.SecretKeyEntryName;

                    return new BackupValidationResult(
                        BackupValidationFailure.MissingEntry,
                        "The archive is missing '" + missing + "'. A complete backup contains both " +
                        BackupArchiveLayout.DatabaseEntryName + " and " +
                        BackupArchiveLayout.SecretKeyEntryName +
                        "; restoring one without the other would leave a working configuration with a " +
                        "broken release-GUID history.",
                        null);
                }

                // DECLARED size first, so an archive that ADMITS to being enormous costs nothing to
                // refuse -- no bytes are written at all.
                foreach (var entry in new[] { databaseEntry, secretEntry })
                {
                    if (entry.Length > MaxEntryBytes)
                    {
                        return TooLarge(entry.FullName);
                    }
                }

                // Then the real stream, bounded. The declared length lives in the central directory
                // and is attacker-controlled: a bomb declares a few kilobytes and then decompresses
                // without end, so the check above is a cheap filter and never the guarantee.
                foreach (var pair in
                    new[] { (Entry: databaseEntry, Destination: stagedDatabase), (Entry: secretEntry, Destination: stagedSecretKey) })
                {
                    if (!TryExtractBounded(pair.Entry, pair.Destination))
                    {
                        return TooLarge(pair.Entry.FullName);
                    }
                }
            }

            if (!IsReadableSqliteDatabase(stagedDatabase, out var integrity))
            {
                return new BackupValidationResult(
                    BackupValidationFailure.CorruptDatabase,
                    "The database inside the archive could not be read as a SQLite file (" + integrity +
                    "). The archive is damaged; restore was not attempted.",
                    null);
            }

            var archiveMigrationId = BackupService.ReadAppliedMigrationId(stagedDatabase);
            var latestKnown = knownMigrationIds.Count == 0
                ? null
                : knownMigrationIds.Max(StringComparer.Ordinal);

            // An archive with no history table is not "newer" - it is older than every build that
            // has one, so it passes here and EF migrates it forward on the next start.
            if (archiveMigrationId is not null &&
                latestKnown is not null &&
                string.CompareOrdinal(archiveMigrationId, latestKnown) > 0)
            {
                return new BackupValidationResult(
                    BackupValidationFailure.SchemaTooNew,
                    "That backup was taken from a newer version of Arbitarr. Its schema is '" +
                    archiveMigrationId + "' and this build only knows up to '" + latestKnown +
                    "'. Restoring it would leave a database this build cannot read, so it was refused. " +
                    "Upgrade Arbitarr to at least the version that produced the backup, then restore again.",
                    archiveMigrationId);
            }

            succeeded = true;
            return new BackupValidationResult(
                null,
                "The archive is a valid Arbitarr backup.",
                archiveMigrationId,
                stagedDatabase,
                stagedSecretKey);
        }
        finally
        {
            // Every failure path leaves NOTHING behind, including the partially written entry that
            // the bound cut off. Only a valid result keeps its staged files, and it owns them.
            if (!succeeded)
            {
                TryDelete(stagedDatabase);
                TryDelete(stagedSecretKey);
            }
        }
    }

    private static BackupValidationResult TooLarge(string entryName) =>
        new(
            BackupValidationFailure.EntryTooLarge,
            "The archive entry '" + entryName + "' is larger than the " +
            (MaxEntryBytes / (1024 * 1024)) + " MB limit for a single entry, or kept expanding past " +
            "it. Restore was refused without writing it to disk.",
            null);

    /// <summary>
    /// Copies one entry to <paramref name="destination"/>, aborting if it produces more than
    /// <see cref="MaxEntryBytes"/>. Returns false when the bound was hit.
    ///
    /// <para><b>THE BOUND IS ON THE STREAM, NOT ON THE DECLARED LENGTH.</b> A zip entry's
    /// uncompressed size is a number in the central directory that the uploader chose, so
    /// <c>ExtractToFile</c> - which trusts it and copies until the stream ends - will happily write
    /// tens of gigabytes from a small upload of compressible bytes and fill the very volume this
    /// feature exists to protect. Counting bytes as they arrive is the only check an archive cannot
    /// lie about, which is why the declared-length check above is a filter and this is the
    /// guarantee.</para>
    /// </summary>
    private static bool TryExtractBounded(ZipArchiveEntry entry, string destination)
    {
        try
        {
            using var source = entry.Open();
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long total = 0;

            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return true;
                }

                total += read;
                if (total > MaxEntryBytes)
                {
                    return false;
                }

                target.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            // A corrupt deflate stream. Refused here rather than thrown: a damaged archive is an
            // expected input on this route, and the caller already has a refusal path for it.
            return false;
        }
    }

    /// <summary>
    /// Opens the staged file as SQLite and runs <c>PRAGMA integrity_check</c>. Both halves matter:
    /// a truncated file fails to open, while a file with a valid header and damaged pages opens
    /// fine and only the integrity check finds it.
    /// </summary>
    private static bool IsReadableSqliteDatabase(string path, out string integrity)
    {
        integrity = "unreadable";

        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            integrity = command.ExecuteScalar() as string ?? "unreadable";

            return string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException ex)
        {
            // The SQLite error text names the failure mode (not a database, file is encrypted, disk
            // image is malformed) and carries no row content, so it is safe to surface.
            integrity = ex.SqliteErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return false;
        }
    }

    /// <summary>
    /// Best-effort delete. Internal rather than private because <see cref="BackupValidationResult"/>
    /// disposes the staged files it owns through it, and a null path is a no-op so a failed result
    /// (which has none) can be disposed like any other.
    /// </summary>
    internal static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
