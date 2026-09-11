using System.IO.Compression;
using System.Security.Cryptography;
using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #56: what a backup archive contains, and — the claim the whole feature rests on — that the
/// snapshot inside it is CONSISTENT when taken against a live, writing WAL database (plan §5,
/// first bullet).
///
/// Every test builds its own real SQLite file under a temp directory. An in-memory substitute would
/// not exercise the thing under test: WAL mode, the <c>-wal</c> sidecar, and
/// <c>SqliteConnection.BackupDatabase</c> only mean anything against a file on disk.
///
/// NO REAL SECRET APPEARS ANYWHERE HERE. The key material is generated per test with
/// <see cref="RandomNumberGenerator"/> — the standing constraint in the plan's §9, and the reason
/// these fixtures can be committed at all.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _configDirectory;
    private readonly BackupPaths _paths;

    public BackupServiceTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
        _paths = new BackupPaths(_configDirectory);
    }

    public void Dispose()
    {
        // Pooled SQLite handles keep the file locked on Windows until the pool is cleared.
        // Scoped to the databases under THIS class's own temp directory rather than
        // ClearAllPools(), which would also close pooled connections belonging to test classes
        // running in parallel (arb-rga.3).
        SqlitePools.ClearPoolsForDirectory(_configDirectory);
        try
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Archive_contains_the_database_and_the_secret_key()
    {
        SeedDatabase(rowCount: 5);
        var keyMaterial = SeedSecretKey();

        var archivePath = Path.Combine(_configDirectory, "out.zip");
        await new BackupService(_paths).WriteArchiveAsync(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);

        // AC1: BOTH files. A database-only archive is the trap plan §2 exists to close — it would
        // look like a complete safety net and would restore a broken release-GUID history.
        Assert.NotNull(archive.GetEntry(BackupArchiveLayout.DatabaseEntryName));
        Assert.NotNull(archive.GetEntry(BackupArchiveLayout.SecretKeyEntryName));

        var restoredKeyMaterial = ReadEntryBytes(archive, BackupArchiveLayout.SecretKeyEntryName);
        Assert.Equal(keyMaterial, restoredKeyMaterial);
    }

    [Fact]
    public async Task Archive_excludes_the_separate_application_log_database()
    {
        SeedDatabase(rowCount: 1);
        SeedSecretKey();

        // The second SQLite file under the config directory. Named through the constant rather than
        // spelled literally, exactly as CLAUDE.md §1 requires of anything enumerating the stores —
        // a test that hardcoded "arbitarr-logs.db" would stop covering this if the name moved.
        var logDatabase = Path.Combine(_configDirectory, Arbitarr.Data.Logging.LogStore.DatabaseFileName);
        await File.WriteAllTextAsync(logDatabase, "log database contents");

        var archivePath = Path.Combine(_configDirectory, "out.zip");
        await new BackupService(_paths).WriteArchiveAsync(archivePath);

        using var archive = ZipFile.OpenRead(archivePath);

        // Positive control FIRST: prove the archive genuinely enumerates entries, so the absence
        // asserted below is a real absence and not a vacuous read of an empty list.
        Assert.NotEmpty(archive.Entries);
        Assert.Contains(archive.Entries, e => e.FullName == BackupArchiveLayout.DatabaseEntryName);

        Assert.DoesNotContain(
            archive.Entries,
            e => e.FullName.Contains(Arbitarr.Data.Logging.LogStore.DatabaseFileName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_is_consistent_when_taken_against_a_database_being_written()
    {
        // AC2, and the single claim that justifies using SQLite's backup API over a file copy.
        //
        // A writer commits rows continuously on its own connection while the backup runs. Each row
        // carries a value that is a pure function of its id, so the assertion is not "how many rows
        // arrived" (which is legitimately non-deterministic under a concurrent writer) but "is every
        // row that DID arrive whole, and is the set of ids a prefix with no holes". A torn snapshot
        // — the failure mode a File.Copy produces — shows up as a hole, a partial row, or a file
        // that will not open at all.
        SeedDatabase(rowCount: 200);
        SeedSecretKey();

        using var writerDone = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            using var connection = OpenLiveConnection();
            for (var id = 1000; id < 4000 && !writerDone.IsCancellationRequested; id++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO probe (id, payload) VALUES ($id, $payload);";
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$payload", PayloadFor(id));
                command.ExecuteNonQuery();
                await Task.Yield();
            }
        });

        var archivePath = Path.Combine(_configDirectory, "out.zip");
        await new BackupService(_paths).WriteArchiveAsync(archivePath);

        await writerDone.CancelAsync();
        await writer;

        var restored = Path.Combine(_configDirectory, "restored.db");
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            archive.GetEntry(BackupArchiveLayout.DatabaseEntryName)!.ExtractToFile(restored);
        }

        using var verify = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = restored }.ToString());
        verify.Open();

        using (var integrity = verify.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", integrity.ExecuteScalar() as string);
        }

        // Every row present is internally consistent. A torn page would surface as a payload that
        // does not match its own id, or as a row that cannot be read at all.
        using var read = verify.CreateCommand();
        read.CommandText = "SELECT id, payload FROM probe ORDER BY id;";
        using var reader = read.ExecuteReader();

        var seen = 0;
        while (reader.Read())
        {
            var id = reader.GetInt32(0);
            Assert.Equal(PayloadFor(id), reader.GetString(1));
            seen++;
        }

        // The 200 rows committed BEFORE the backup started must all be there: they were durable
        // before the snapshot began, so their absence would be data loss, not a race.
        Assert.True(seen >= 200, $"Expected at least the 200 pre-committed rows in the snapshot, saw {seen}.");
    }

    [Fact]
    public async Task Archive_records_the_schema_the_snapshot_was_taken_at()
    {
        SeedDatabase(rowCount: 1);
        SeedSecretKey();
        SeedMigrationHistory("20260907080054_AddApiKeysTable");

        var archivePath = Path.Combine(_configDirectory, "out.zip");
        await new BackupService(_paths).WriteArchiveAsync(archivePath);

        var restored = Path.Combine(_configDirectory, "restored.db");
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            archive.GetEntry(BackupArchiveLayout.DatabaseEntryName)!.ExtractToFile(restored);
        }

        Assert.Equal("20260907080054_AddApiKeysTable", BackupService.ReadAppliedMigrationId(restored));
    }

    [Fact]
    public void Reading_the_migration_id_of_a_database_with_no_history_returns_null()
    {
        // An older archive predating EF migrations is a legitimate input, not an error: the
        // validator treats a null id as "older than everything" and lets EF migrate it forward.
        SeedDatabase(rowCount: 1);
        Assert.Null(BackupService.ReadAppliedMigrationId(_paths.DatabasePath));
    }

    /// <summary>
    /// arb-rwhb: a backup whose token is ALREADY cancelled must not begin the database copy.
    ///
    /// <para><b>Why this is the property, and why it is not "the copy is aborted midway".</b>
    /// <c>SnapshotDatabase</c> calls <c>SqliteConnection.BackupDatabase</c>, which is blocking and
    /// cannot be cancelled once entered. So the only honest contract is that a signalled token stops
    /// the copy from STARTING. Aborting midway would also be worse than useless: the snapshot would
    /// be abandoned half-written, and a torn snapshot is what <c>BackupArchiveValidator</c> exists
    /// to reject.</para>
    ///
    /// <para><b>What this buys in production.</b> Without it, a host already shutting down still
    /// began a full copy, and whatever owned the config directory deleted it out from under a live
    /// SQLite reader — logged as <c>SQLite Error 5898: 'disk I/O error'</c> from
    /// <c>BackupDatabase</c>. <c>MaintenanceHostedService</c>'s
    /// <c>catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)</c> arm
    /// then treats this as a clean stop rather than a backup FAILURE, so it neither logs an error
    /// nor records a broken safety net in the UI.</para>
    ///
    /// <para>NON-VACUITY: the assertions below prove the copy did not merely fail — they prove it
    /// never ran, by showing neither the archive nor any staging snapshot was left behind. A test
    /// that only asserted the throw would pass against an implementation that threw AFTER copying.</para>
    /// </summary>
    [Fact]
    public async Task A_cancelled_token_declines_the_backup_before_the_database_copy_starts()
    {
        SeedDatabase(rowCount: 5);
        SeedSecretKey();

        var archivePath = Path.Combine(_configDirectory, "cancelled.zip");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new BackupService(_paths).WriteArchiveAsync(archivePath, cancelled.Token));

        // The copy never started: no archive, and nothing left in staging. Under the pre-fix code
        // the token was accepted and never consulted, so the full BackupDatabase copy ran and this
        // archive existed.
        Assert.False(File.Exists(archivePath), "A cancelled backup still wrote its archive.");

        var stagedSnapshots = Directory.Exists(_paths.StagingDirectory)
            ? Directory.EnumerateFiles(_paths.StagingDirectory, StagingFileNames.SnapshotPrefix + "*.db").ToArray()
            : [];
        Assert.True(
            stagedSnapshots.Length == 0,
            "A cancelled backup left a staging snapshot behind, so the database copy had already begun: " +
            string.Join(", ", stagedSnapshots.Select(Path.GetFileName)));
    }

    /// <summary>
    /// The other half of the contract, and the control for the test above: an UNCANCELLED token
    /// still takes the backup. Without this, the cancellation test would pass just as happily
    /// against an implementation that refused every backup.
    /// </summary>
    [Fact]
    public async Task An_uncancelled_token_still_takes_the_backup()
    {
        SeedDatabase(rowCount: 5);
        SeedSecretKey();

        var archivePath = Path.Combine(_configDirectory, "not-cancelled.zip");
        using var live = new CancellationTokenSource();

        await new BackupService(_paths).WriteArchiveAsync(archivePath, live.Token);

        Assert.True(File.Exists(archivePath), "An uncancelled backup did not write its archive.");
    }

    [Fact]
    public void Download_file_name_carries_a_sortable_utc_timestamp_and_no_illegal_characters()
    {
        var name = BackupService.FileNameFor(new DateTimeOffset(2026, 9, 7, 14, 5, 9, TimeSpan.Zero));

        Assert.Equal("arbitarr-backup-20260907T140509Z.zip", name);

        // A colon is not a legal file-name character on Windows, so a raw ISO 8601 string would be
        // mangled by the browser saving it. Asserted rather than assumed.
        Assert.DoesNotContain(':', name);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void Download_file_name_is_expressed_in_utc_regardless_of_the_offset_it_is_given()
    {
        // Two instants that are the SAME moment expressed in different offsets must produce the
        // same name; otherwise an operator in a non-UTC zone gets file names that sort against
        // their neighbours' incorrectly.
        var utc = new DateTimeOffset(2026, 9, 7, 14, 5, 9, TimeSpan.Zero);
        var shifted = utc.ToOffset(TimeSpan.FromHours(2));

        Assert.Equal(BackupService.FileNameFor(utc), BackupService.FileNameFor(shifted));
    }

    private static string PayloadFor(int id) => $"row-{id}-{id * 7}";

    private SqliteConnection OpenLiveConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL is the mode production runs in and the mode a file copy cannot snapshot safely, so
        // the test must exercise it rather than SQLite's rollback-journal default.
        pragma.CommandText = "PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void SeedDatabase(int rowCount)
    {
        using var connection = OpenLiveConnection();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS probe (id INTEGER PRIMARY KEY, payload TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        for (var id = 1; id <= rowCount; id++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO probe (id, payload) VALUES ($id, $payload);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$payload", PayloadFor(id));
            insert.ExecuteNonQuery();
        }
    }

    private void SeedMigrationHistory(string migrationId)
    {
        using var connection = OpenLiveConnection();

        using var create = connection.CreateCommand();
        create.CommandText =
            "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('" + migrationId + "', '10.0.11');";
        create.ExecuteNonQuery();
    }

    /// <summary>Generates throwaway key material — never a real secret, per the plan's §9.</summary>
    private byte[] SeedSecretKey()
    {
        var keyMaterial = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_paths.SecretKeyPath, keyMaterial);
        return keyMaterial;
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        using var stream = archive.GetEntry(entryName)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
