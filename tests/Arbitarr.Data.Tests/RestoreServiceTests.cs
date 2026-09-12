using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #56: validation refuses each malformed archive with its OWN message before touching anything,
/// and a valid restore takes a pre-restore safety copy first (plan §5, AC4/AC5).
///
/// The four rejection cases are asserted as four DISTINCT failure values, not merely as "some
/// failure": plan §5 requires four distinct messages, and a single opaque "invalid backup" would
/// leave an operator unable to tell a truncated download from a version mismatch.
///
/// Key material is generated per test; no real secret is committed (plan §9).
/// </summary>
public sealed class RestoreServiceTests : IDisposable
{
    private const string OldMigration = "20260830012500_AddBaseBackoffSecondsToSourceHealthRecord";
    private const string CurrentMigration = "20260907080054_AddApiKeysTable";
    private const string FutureMigration = "29991231235959_AddSomethingFromTheFuture";

    private static readonly string[] KnownMigrations = [OldMigration, CurrentMigration];

    private readonly string _configDirectory;
    private readonly BackupPaths _paths;

    public RestoreServiceTests()
    {
        _configDirectory = Path.Combine(
            Path.GetTempPath(), "arbitarr-restore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDirectory);
        _paths = new BackupPaths(_configDirectory);
    }

    public void Dispose()
    {
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
    public void Validation_rejects_a_file_that_is_not_an_archive()
    {
        var path = Path.Combine(_configDirectory, "not-a-zip.zip");
        File.WriteAllText(path, "this is plainly not a zip archive");

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.NotAnArchive, result.Failure);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validation_rejects_an_archive_with_no_secret_key()
    {
        // The half-backup case, and the one plan §2 calls a trap: it restores a working
        // configuration and a broken release-GUID history, so it must be refused rather than
        // applied with a warning.
        var path = BuildArchive(includeDatabase: true, includeSecret: false, migrationId: CurrentMigration);

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.MissingEntry, result.Failure);
        Assert.Contains(BackupArchiveLayout.SecretKeyEntryName, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_rejects_an_archive_with_no_database()
    {
        var path = BuildArchive(includeDatabase: false, includeSecret: true, migrationId: null);

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.MissingEntry, result.Failure);
        Assert.Contains(BackupArchiveLayout.DatabaseEntryName, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_rejects_an_archive_whose_database_is_corrupt()
    {
        var path = Path.Combine(_configDirectory, "corrupt.zip");
        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, BackupArchiveLayout.DatabaseEntryName, "not a SQLite file at all");
            WriteBytesEntry(archive, BackupArchiveLayout.SecretKeyEntryName, RandomNumberGenerator.GetBytes(32));
        }

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.CorruptDatabase, result.Failure);
    }

    [Fact]
    public void Validation_refuses_a_newer_schema_and_names_both_versions()
    {
        // AC5. Silently accepting this produces a database the running build cannot read, and the
        // failure surfaces later and somewhere else.
        var path = BuildArchive(includeDatabase: true, includeSecret: true, migrationId: FutureMigration);

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.SchemaTooNew, result.Failure);
        Assert.Contains(FutureMigration, result.Message, StringComparison.Ordinal);
        Assert.Contains(CurrentMigration, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_four_rejection_cases_produce_four_distinct_messages()
    {
        // Plan §5 asks for four distinct messages, which is a property of the SET and not of any
        // one case — four tests that each pass individually would still permit four identical
        // strings. This asserts the thing actually required.
        var messages = new[]
        {
            BackupArchiveValidator.Validate(WriteText("junk.zip", "junk"), KnownMigrations, _paths.StagingDirectory).Message,
            BackupArchiveValidator.Validate(
                BuildArchive(includeDatabase: true, includeSecret: false, migrationId: CurrentMigration), KnownMigrations, _paths.StagingDirectory).Message,
            BackupArchiveValidator.Validate(BuildCorruptArchive(), KnownMigrations, _paths.StagingDirectory).Message,
            BackupArchiveValidator.Validate(
                BuildArchive(includeDatabase: true, includeSecret: true, migrationId: FutureMigration), KnownMigrations, _paths.StagingDirectory).Message,
        };

        Assert.Equal(4, messages.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_archive_older_than_this_build_is_accepted()
    {
        // The other direction of AC5: refusing NEWER must not have become refusing DIFFERENT. An
        // older archive is the ordinary case — it is what a backup taken before an upgrade is — and
        // EF migrates it forward on the next start.
        var path = BuildArchive(includeDatabase: true, includeSecret: true, migrationId: OldMigration);

        var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.True(result.IsValid, result.Message);
    }

    /// <summary>
    /// A DECOMPRESSION BOMB is refused, and refused without writing what it expands to.
    ///
    /// <para>The upload cap bounds only the COMPRESSED bytes. A zip of highly compressible data
    /// expands by orders of magnitude, so a bounded upload can still write enough to fill the config
    /// volume - the very disk this feature exists to protect - and the validator extracts before the
    /// SQLite check, so a rejected archive would still have paid that cost.</para>
    ///
    /// <para>This is the case where the header is HONEST: the entry declares a size over the limit,
    /// so it is refused from the central directory alone, with nothing written at all.</para>
    /// </summary>
    [Fact]
    public void An_entry_declaring_a_size_over_the_limit_is_refused_before_anything_is_written()
    {
        var path = BuildArchiveWithOversizedDatabaseEntry();

        var stagedBefore = CountStagedValidationFiles();
        using var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        Assert.Equal(BackupValidationFailure.EntryTooLarge, result.Failure);
        Assert.Null(result.StagedDatabasePath);

        // Nothing was extracted: the refusal came from the declared length, before any write.
        Assert.Equal(stagedBefore, CountStagedValidationFiles());
    }

    /// <summary>
    /// arb-zupt: the CORRUPT-DATABASE refusal also leaves nothing staged — the case the test above
    /// cannot reach.
    ///
    /// <para><b>Why this is a distinct case and not a second spelling of the same one.</b> The bomb
    /// above is refused from the central directory, before a byte is written and long before
    /// anything is opened as SQLite. A corrupt database is the ONLY refusal that gets as far as
    /// <c>BackupArchiveValidator.IsReadableSqliteDatabase</c>, which is the only place a staged file
    /// is handed to SQLite at all — so this is the only path where the cleanup has a live handle to
    /// contend with, and it was the one that leaked: the read-only connection was pooled, its
    /// <c>Dispose</c> returned the handle rather than closing the file, and the <c>finally</c>'s
    /// <c>TryDelete</c> swallowed the resulting IOException. The refusal reported success and
    /// <c>arbitarr-restore-validate-&lt;guid&gt;.db</c> stayed on disk.</para>
    ///
    /// <para><b>This class's <c>Dispose</c> does not mask it.</b> The
    /// <c>SqlitePools.ClearPoolsForDirectory</c> there runs at TEARDOWN, after this assertion has
    /// already been evaluated, so the pooled handle is still held at the moment that matters. What
    /// the teardown clear does hide is the DIRECTORY residue, which is why the leak surfaced in
    /// <c>Arbitarr.Integration.Tests</c> (whose factory has no such clear) and not here.</para>
    /// </summary>
    [Fact]
    public void A_corrupt_database_refusal_leaves_nothing_staged()
    {
        var path = BuildArchiveWithCorruptDatabase();

        var stagedBefore = CountStagedValidationFiles();
        using var result = BackupArchiveValidator.Validate(path, KnownMigrations, _paths.StagingDirectory);

        // The refusal must be the corrupt-database one: refused any earlier and the SQLite open this
        // test exists to check never happened.
        Assert.Equal(BackupValidationFailure.CorruptDatabase, result.Failure);
        Assert.Null(result.StagedDatabasePath);

        // The validator always CALLS File.Delete on what it staged, so a surviving file means that
        // delete threw and was swallowed — and the only thing that makes it throw is an open handle.
        Assert.Equal(stagedBefore, CountStagedValidationFiles());
    }

    /// <summary>
    /// arb-3gd POSITIVE CONTROL for the "nothing was staged" assertion above. It proves two things,
    /// each with its own half:
    ///
    /// <list type="number">
    ///   <item>A file with the validator's own prefix, planted in THIS instance's staging directory,
    ///   DOES change <see cref="CountStagedValidationFiles"/> — so the equality assertion above is a
    ///   real check on this instance and not a comparison of two calls that could never differ.</item>
    ///   <item>The same file, planted in the MACHINE-WIDE system temp directory instead, does NOT
    ///   change it — proving the count is genuinely scoped to this instance's directory and not
    ///   still reading the shared one arb-3gd moved it off of.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void The_staged_file_count_detects_a_planted_file_in_this_instance_and_ignores_a_bystander_elsewhere()
    {
        Directory.CreateDirectory(_paths.StagingDirectory);
        var before = CountStagedValidationFiles();

        var plantedInInstance = Path.Combine(
            _paths.StagingDirectory, "arbitarr-restore-validate-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(plantedInInstance, "planted by the positive control");
        try
        {
            // Half 1: a file placed where the validator actually stages IS detected.
            Assert.NotEqual(before, CountStagedValidationFiles());
        }
        finally
        {
            File.Delete(plantedInInstance);
        }

        Assert.Equal(before, CountStagedValidationFiles());

        var bystanderPath = Path.Combine(
            Path.GetTempPath(), "arbitarr-restore-validate-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(bystanderPath, "a bystander process's file in the shared system temp dir");
        try
        {
            // Half 2: the SAME prefix, in the shared system temp dir a bystander process would use,
            // is NOT detected — the count reads this instance's directory only.
            Assert.Equal(before, CountStagedValidationFiles());
        }
        finally
        {
            File.Delete(bystanderPath);
        }
    }

    /// <summary>
    /// The bomb is refused, and the refusal leaves nothing on disk - with a legitimate archive of
    /// the same shape still validating, so this is not a validator that simply refuses everything.
    ///
    /// <para><b>ON THE STREAM BOUND, WHICH THIS TEST DELIBERATELY DOES NOT CLAIM TO COVER.</b>
    /// <c>TryExtractBounded</c> counts bytes as it reads, because a zip's uncompressed size is
    /// attacker-controlled metadata. Measured against .NET 10 outside this repo, that branch turns
    /// out to be UNREACHABLE through a crafted archive: <c>ZipArchiveEntry.Open()</c> clamps reads
    /// to the declared size, so patching the central directory down to 4096 yields a stream that
    /// stops at 4096 rather than one that runs on - tried with an accurate directory, a patched
    /// directory, and a streamed entry with a data descriptor, and all three clamped.
    ///
    /// So the DECLARED-length check is what refuses a bomb on this runtime. The two guards are
    /// REDUNDANT WITH EACH OTHER by design, and the mutation results say so exactly: removing either
    /// one alone leaves these tests green, because the survivor catches the archive; removing BOTH
    /// fails them. That is what defence in depth looks like under mutation, and it is the reason
    /// there is no separate test asserting the stream bound - such a test could not be made to fail
    /// while the declared-length check stands, so it would have read as coverage while proving
    /// nothing. Recorded rather than quietly dropped.</para>
    /// </summary>
    [Fact]
    public void A_legitimate_archive_still_validates_after_the_bomb_guard()
    {
        // POSITIVE CONTROL for the refusal above: the same shape, honestly sized, must pass.
        var legitimate = BuildArchive(includeDatabase: true, includeSecret: true, migrationId: CurrentMigration);

        using var result = BackupArchiveValidator.Validate(legitimate, KnownMigrations, _paths.StagingDirectory);

        Assert.True(
            result.IsValid,
            "Positive control failed: a legitimate archive did not validate, so refusing the " +
            "oversized one proves nothing about the bound. Message: " + result.Message);
        Assert.NotNull(result.StagedDatabasePath);
        Assert.True(File.Exists(result.StagedDatabasePath));
    }

    [Fact]
    public async Task A_refused_restore_changes_nothing_on_disk()
    {
        SeedLiveState(out var originalDatabaseBytes, out var originalKeyMaterial);

        var path = BuildArchive(includeDatabase: true, includeSecret: true, migrationId: FutureMigration);
        var result = await NewRestoreService().RestoreAsync(path, KnownMigrations);

        Assert.False(result.Succeeded);
        Assert.False(result.PreRestoreBackupTaken);

        // The live files are byte-for-byte what they were. A restore that half-applies is worse
        // than one that refuses, so "refused" must mean untouched and not merely "reported an error".
        Assert.Equal(originalDatabaseBytes, await File.ReadAllBytesAsync(_paths.DatabasePath));
        Assert.Equal(originalKeyMaterial, await File.ReadAllBytesAsync(_paths.SecretKeyPath));
        Assert.False(File.Exists(_paths.PreRestorePath));
    }

    [Fact]
    public async Task A_valid_restore_replaces_both_files_and_takes_a_pre_restore_backup_first()
    {
        SeedLiveState(out _, out var originalKeyMaterial);

        var replacementKeyMaterial = RandomNumberGenerator.GetBytes(32);
        var path = BuildArchive(
            includeDatabase: true, includeSecret: true, migrationId: CurrentMigration, keyMaterial: replacementKeyMaterial);

        var result = await NewRestoreService().RestoreAsync(path, KnownMigrations);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.PreRestoreBackupTaken);

        // AC4: the safety copy exists, and it holds the state from BEFORE the restore — which is
        // the only property that makes a mistaken restore recoverable. Asserting merely that a file
        // appeared would pass just as well for a copy of the post-restore state.
        Assert.True(File.Exists(_paths.PreRestorePath));
        using (var preRestore = ZipFile.OpenRead(_paths.PreRestorePath))
        {
            var savedKeyMaterial = ReadEntryBytes(preRestore, BackupArchiveLayout.SecretKeyEntryName);
            Assert.Equal(originalKeyMaterial, savedKeyMaterial);
            Assert.NotEqual(replacementKeyMaterial, savedKeyMaterial);
        }

        // And the live key really was replaced — the GUID-invalidating half of a restore.
        Assert.Equal(replacementKeyMaterial, await File.ReadAllBytesAsync(_paths.SecretKeyPath));
    }

    /// <summary>
    /// Many restores issued at once through the gated entry point leave exactly ONE pre-restore
    /// safety copy, and every call either succeeds or is refused outright - never a partial apply
    /// and never an IOException from two callers writing the same file.
    ///
    /// <para><b>Why this asserts an invariant rather than "exactly one succeeded and one was
    /// refused".</b> Whether two calls actually overlap is not something a test can force here:
    /// there is no point in the restore path that blocks rather than throws, so a holder cannot be
    /// parked inside the gate without adding a seam to production code that exists only for this
    /// test. An earlier version of this test raced two calls and asserted one refusal; it passed in
    /// isolation and FAILED in the full sequential run, because under load the first restore
    /// finished before the second began and both legitimately succeeded. That is a test asserting
    /// the scheduler, not the gate. What is true regardless of interleaving is asserted instead: the
    /// safety copy is written once, and no caller ever fails with a file-sharing error.</para>
    ///
    /// <para><b>What the gate prevents.</b> RestoreService is a singleton and the safety copy has a
    /// FIXED filename. Without the gate two overlapping calls write that same path with
    /// FileShare.None, so one takes an IOException - and worse, if the second copy is taken after
    /// the first has begun replacing the live files it captures a HALF-APPLIED state. The one
    /// artefact that makes a mistaken restore recoverable becomes a snapshot of a broken instance,
    /// and nobody finds out until they need it. Both of those are what the assertions below catch:
    /// with the gate removed this test throws IOException from the losing caller.</para>
    ///
    /// <para>POSITIVE CONTROL: at least one call succeeds, so the invariant is not satisfied by a
    /// gate that refuses everything, and a further restore succeeds afterwards, so the gate
    /// reopens.</para>
    /// </summary>
    [Fact]
    public async Task Concurrent_restores_never_collide_and_leave_exactly_one_safety_copy()
    {
        SeedLiveState(out _, out _);

        var service = NewRestoreService();

        const int callers = 8;
        var archives = Enumerable.Range(0, callers)
            .Select(_ => BuildArchive(
                includeDatabase: true, includeSecret: true, migrationId: CurrentMigration))
            .ToArray();

        Directory.CreateDirectory(_paths.BackupDirectory);
        if (File.Exists(_paths.PreRestorePath))
        {
            File.Delete(_paths.PreRestorePath);
        }

        var start = new TaskCompletionSource();

        var callTasks = archives.Select(archive => Task.Run(async () =>
        {
            // Every caller waits on the same signal, so they are released together rather than
            // trickling in as each Task.Run is scheduled.
            await start.Task;
            return await service.TryRestoreAsync(archive, KnownMigrations);
        })).ToArray();

        start.SetResult();

        var results = await Task.WhenAll(callTasks);

        // No caller collided on the fixed safety-copy path. Without the gate the losing caller
        // throws IOException out of Task.WhenAll above and this test never reaches here.
        var ran = results.Where(r => r is not null).ToArray();

        // POSITIVE CONTROL: the gate admits work; it does not simply refuse everything.
        Assert.NotEmpty(ran);
        Assert.All(ran, r => Assert.True(r!.Succeeded, r.Message));

        // Every refusal is an explicit null - never a half-applied restore reported as success.
        Assert.Equal(callers, ran.Length + results.Count(r => r is null));

        // Exactly one safety copy, never one overwritten by a pass that ran against partly-replaced
        // files.
        Assert.True(File.Exists(_paths.PreRestorePath));
        Assert.Single(Directory.EnumerateFiles(
            _paths.BackupDirectory, BackupPaths.PreRestoreFileName));

        // POSITIVE CONTROL: the gate reopened after the burst.
        SeedLiveState(out _, out _);
        var after = await service.TryRestoreAsync(
            BuildArchive(includeDatabase: true, includeSecret: true, migrationId: CurrentMigration),
            KnownMigrations);
        Assert.NotNull(after);
        Assert.True(after!.Succeeded, after.Message);
    }

    [Fact]
    public async Task The_pre_restore_backup_is_itself_restorable()
    {
        // Plan §5: "that backup is itself restorable". A safety copy that cannot be restored is not
        // a safety net, so this drives the real restore path over it rather than inspecting it.
        SeedLiveState(out _, out var originalKeyMaterial);

        var service = NewRestoreService();
        var incoming = BuildArchive(
            includeDatabase: true,
            includeSecret: true,
            migrationId: CurrentMigration,
            keyMaterial: RandomNumberGenerator.GetBytes(32));

        Assert.True((await service.RestoreAsync(incoming, KnownMigrations)).Succeeded);

        // Now roll back, using the safety copy the restore above left behind.
        var rollbackSource = Path.Combine(_configDirectory, "rollback.zip");
        File.Copy(_paths.PreRestorePath, rollbackSource);

        var rollback = await service.RestoreAsync(rollbackSource, KnownMigrations);

        Assert.True(rollback.Succeeded, rollback.Message);
        Assert.Equal(originalKeyMaterial, await File.ReadAllBytesAsync(_paths.SecretKeyPath));
    }

    private RestoreService NewRestoreService() =>
        new(_paths, new BackupService(_paths));

    private void SeedLiveState(out byte[] databaseBytes, out byte[] keyMaterial)
    {
        CreateSqliteFile(_paths.DatabasePath, CurrentMigration);
        databaseBytes = File.ReadAllBytes(_paths.DatabasePath);

        keyMaterial = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(_paths.SecretKeyPath, keyMaterial);
    }

    private string WriteText(string name, string content)
    {
        var path = Path.Combine(_configDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string BuildCorruptArchive()
    {
        var path = Path.Combine(_configDirectory, "corrupt-" + Guid.NewGuid().ToString("N") + ".zip");
        using var stream = new FileStream(path, FileMode.Create);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteTextEntry(archive, BackupArchiveLayout.DatabaseEntryName, "not a SQLite file at all");
        WriteBytesEntry(archive, BackupArchiveLayout.SecretKeyEntryName, RandomNumberGenerator.GetBytes(32));
        return path;
    }

    /// <summary>
    /// How many validator staging files currently exist under THIS TEST'S OWN instance directory
    /// (<c>_paths.StagingDirectory</c>), not the machine-wide system temp directory (arb-3gd) — the
    /// validator now stages there, so this is instance-scoped rather than process-global. Counting
    /// rather than asserting zero, and compared as a delta by the caller, tolerates other tests in
    /// this same class running in the same process; it does not depend on other processes at all
    /// now, which is the property arb-3gd exists to establish.
    /// </summary>
    private int CountStagedValidationFiles() =>
        Directory.Exists(_paths.StagingDirectory)
            ? Directory.EnumerateFiles(_paths.StagingDirectory, "arbitarr-restore-validate-*").Count()
            : 0;

    /// <summary>
    /// An archive that is structurally a complete backup — both entries present and correctly named
    /// — whose database entry is not a SQLite file. Refused by the integrity check rather than by
    /// any earlier structural one, which is what makes it the archive that reaches the SQLite open.
    /// </summary>
    private string BuildArchiveWithCorruptDatabase()
    {
        var path = Path.Combine(_configDirectory, "corrupt-" + Guid.NewGuid().ToString("N") + ".zip");

        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteTextEntry(archive, BackupArchiveLayout.DatabaseEntryName, "not a SQLite file at all");
            WriteBytesEntry(archive, BackupArchiveLayout.SecretKeyEntryName, RandomNumberGenerator.GetBytes(32));
        }

        return path;
    }

    /// <summary>
    /// An archive whose database entry HONESTLY declares more than
    /// <see cref="BackupArchiveValidator.MaxEntryBytes"/>. Built by writing that many bytes of zeros
    /// through the deflate stream, which compresses to a trivially small file - which is the whole
    /// point of the attack.
    /// </summary>
    private string BuildArchiveWithOversizedDatabaseEntry()
    {
        var path = Path.Combine(_configDirectory, "bomb-" + Guid.NewGuid().ToString("N") + ".zip");

        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entry = archive.CreateEntry(BackupArchiveLayout.DatabaseEntryName).Open())
            {
                WriteZeros(entry, BackupArchiveValidator.MaxEntryBytes + (1L * 1024 * 1024));
            }

            WriteBytesEntry(archive, BackupArchiveLayout.SecretKeyEntryName, RandomNumberGenerator.GetBytes(32));
        }

        return path;
    }

    private static void WriteZeros(Stream destination, long count)
    {
        var buffer = new byte[81920];
        var remaining = count;

        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            destination.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
    }

    private string BuildArchive(
        bool includeDatabase,
        bool includeSecret,
        string? migrationId,
        byte[]? keyMaterial = null)
    {
        var path = Path.Combine(_configDirectory, "archive-" + Guid.NewGuid().ToString("N") + ".zip");
        var databaseSource = Path.Combine(_configDirectory, "source-" + Guid.NewGuid().ToString("N") + ".db");

        if (includeDatabase)
        {
            CreateSqliteFile(databaseSource, migrationId);
        }

        using (var stream = new FileStream(path, FileMode.Create))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            if (includeDatabase)
            {
                archive.CreateEntryFromFile(databaseSource, BackupArchiveLayout.DatabaseEntryName);
            }

            if (includeSecret)
            {
                WriteBytesEntry(
                    archive,
                    BackupArchiveLayout.SecretKeyEntryName,
                    keyMaterial ?? RandomNumberGenerator.GetBytes(32));
            }
        }

        SqlitePools.ClearPoolForFile(databaseSource);
        return path;
    }

    /// <summary>
    /// Builds a small real SQLite file, then clears the connection pool.
    ///
    /// The pool clear is not tidiness: Microsoft.Data.Sqlite keeps the handle alive after the
    /// connection is disposed, so on Windows the file stays locked and the very next
    /// <c>ZipFile.CreateEntryFromFile</c> or <c>File.ReadAllBytes</c> over it fails with a sharing
    /// violation. Every helper that produces a file another step immediately reads must clear it.
    /// </summary>
    private static void CreateSqliteFile(string path, string? migrationId)
    {
        CreateSqliteFileCore(path, migrationId);
        SqlitePools.ClearPoolForFile(path);
    }

    private static void CreateSqliteFileCore(string path, string? migrationId)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS Settings (Name TEXT PRIMARY KEY, Value TEXT NOT NULL);" +
            "INSERT OR REPLACE INTO Settings (Name, Value) VALUES ('marker', 'seeded');";
        command.ExecuteNonQuery();

        if (migrationId is not null)
        {
            using var history = connection.CreateCommand();
            history.CommandText =
                "CREATE TABLE IF NOT EXISTS __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
                "INSERT OR REPLACE INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '10.0.11');";
            history.Parameters.AddWithValue("$id", migrationId);
            history.ExecuteNonQuery();
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content) =>
        WriteBytesEntry(archive, name, Encoding.UTF8.GetBytes(content));

    private static void WriteBytesEntry(ZipArchive archive, string name, byte[] content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(content);
    }

    private static byte[] ReadEntryBytes(ZipArchive archive, string entryName)
    {
        using var stream = archive.GetEntry(entryName)!.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
