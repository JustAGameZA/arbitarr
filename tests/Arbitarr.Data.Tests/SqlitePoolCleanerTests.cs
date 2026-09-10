using Arbitarr.Data.Backup;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-n21: <see cref="SqlitePoolCleaner"/> must drop EVERY pooled handle on the database it is
/// given, and no handle on any other database.
///
/// <para><b>Why the assertion is an inode observation and not a file lock.</b> The obvious check —
/// "the file can now be deleted or replaced" — is vacuous on Linux, which is where CI runs. On
/// Windows a pooled handle holds a share lock so the replace throws; on Linux the replace is simply
/// PERMITTED while handles remain open, and the damage is that those handles keep serving the
/// REPLACED INODE. A lock-based test would therefore pass on ubuntu-latest precisely when the bug is
/// present, which is worse than no test. Measured directly rather than assumed: on Ubuntu, an
/// already-open descriptor reads the old content after a rename over its path while a fresh open
/// reads the new content.</para>
///
/// <para>So these tests write marker A, populate the pools, replace the file with one holding marker
/// B, and then assert PER CONNECTION STRING that a freshly-opened connection reads B. A surviving
/// pooled handle hands back its cached open file — marker A — on Linux, and makes the replace throw
/// on Windows. Both platforms fail if a pool was missed.</para>
///
/// <para><b>Why this class may clear pools under parallel execution.</b> Every string it clears
/// embeds one of its own fixtures' GUID-distinct paths (including the <c>.incoming</c> one), and
/// pools are keyed by the full string, so no neighbouring test's pool is reachable from here. That
/// isolation is a property of the fixture paths: pointing any of these strings at a fixed path
/// would reintroduce the cross-test interference this bead exists to close.</para>
/// </summary>
public sealed class SqlitePoolCleanerTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arb-n21-cleaner");
    private readonly SqliteTestDatabase _logStore = new("arb-n21-logstore");

    public SqlitePoolCleanerTests()
    {
        // This class reaches each file through strings the fixture does not build itself, and pools
        // are keyed by the FULL string, so the fixture's own clear would leave those pools open and
        // its delete would race them. Registering them is what makes cleanup cover this class.
        foreach (var connectionString in DatabaseConnectionStrings.ForDatabase(_database.Path))
        {
            _database.AlsoClearPoolFor(connectionString);
        }

        _database.AlsoClearPoolFor(DatabaseConnectionStrings.Maintenance(_database.Path + ".incoming"));
        _logStore.AlsoClearPoolFor(LogStoreConnectionString(_logStore.Path));
        _logStore.AlsoClearPoolFor(DatabaseConnectionStrings.Maintenance(_logStore.Path));
    }

    public void Dispose()
    {
        _database.Dispose();
        _logStore.Dispose();
    }

    /// <summary>
    /// The property the restore depends on: after the clear, EVERY string that names the database
    /// reads the replaced file.
    ///
    /// <para>The non-vacuity of this is carried by
    /// <see cref="Clearing_only_the_application_string_leaves_the_maintenance_pool_serving_the_old_file"/>,
    /// which drives the same sequence with the incomplete clear this test exists to forbid and
    /// requires it to FAIL. Without that companion, this test would pass just as happily if pools
    /// were never populated at all.</para>
    /// </summary>
    [Fact]
    public void Clearing_the_database_pools_lets_every_connection_string_see_the_replaced_file()
    {
        SeedMarker(_database.Path, "A");
        PopulateEveryPool(_database.Path);

        SqlitePoolCleaner.ClearPoolsFor(_database.Path);

        ReplaceWithMarker(_database.Path, "B");

        // Per string, not "some connection sees B": a single assertion over one of them would pass
        // while the other pool still served the old inode, which is the exact bug (arb-n21).
        foreach (var connectionString in DatabaseConnectionStrings.ForDatabase(_database.Path))
        {
            Assert.Equal("B", ReadMarker(connectionString));
        }
    }

    /// <summary>
    /// POSITIVE CONTROL (CLAUDE.md §4). Clears ONLY the application string — the incomplete fix a
    /// naive implementation ships — and requires the maintenance pool to still be serving the OLD
    /// file afterwards.
    ///
    /// <para>This is what proves the test above is not vacuous: it demonstrates that a missed pool
    /// IS detectable by this measurement. Without it, "every string reads B" could pass because
    /// nothing was ever pooled, and CLAUDE.md §4 records three separate occasions when exactly that
    /// shape shipped.</para>
    ///
    /// <para>On Windows the surviving handle makes the replace throw instead, so the incomplete
    /// clear is caught there too — either outcome is a detection, and the test accepts both rather
    /// than asserting a platform-specific mechanism.</para>
    /// </summary>
    [Fact]
    public void Clearing_only_the_application_string_leaves_the_maintenance_pool_serving_the_old_file()
    {
        SeedMarker(_database.Path, "A");
        PopulateEveryPool(_database.Path);

        // The incomplete clear: one of the two strings DatabaseConnectionStrings produces.
        using (var applicationOnly = new SqliteConnection(DatabaseConnectionStrings.Application(_database.Path)))
        {
            SqliteConnection.ClearPool(applicationOnly);
        }

        string? maintenanceMarker = null;
        var replaceThrew = false;
        try
        {
            ReplaceWithMarker(_database.Path, "B");
            maintenanceMarker = ReadMarker(DatabaseConnectionStrings.Maintenance(_database.Path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows: the surviving pooled handle holds a share lock and the replace is refused.
            // BOTH types are caught deliberately — File.Move over a share-locked file surfaces as
            // UnauthorizedAccessException ("Access to the path is denied"), not the IOException the
            // sharing-violation wording suggests, and catching only IOException made this control
            // fail for the wrong reason while the detection itself was working.
            replaceThrew = true;
        }

        Assert.True(
            replaceThrew || maintenanceMarker == "A",
            "Clearing only the application string should have left the maintenance pool holding the " +
            $"old file, but the replace succeeded and that pool read '{maintenanceMarker}'. If this " +
            "fails, the measurement in Clearing_the_database_pools_lets_every_connection_string_see_" +
            "the_replaced_file is vacuous: it can no longer tell a complete clear from an incomplete " +
            "one.");

        // Leave no poisoned pool behind for the next test.
        SqlitePoolCleaner.ClearPoolsFor(_database.Path);
    }

    /// <summary>
    /// NEGATIVE CONTROL: the clear must not reach another database. The log store owns a SEPARATE
    /// FILE (CLAUDE.md §1's two-databases rule) that a restore never replaces, so its pool must
    /// survive — a clear that took it too would be the over-broad behaviour of
    /// <c>ClearAllPools</c> reintroduced under a new name, which is the whole point of arb-n21.
    ///
    /// <para>Asserted by observation rather than by inspecting the pool: the log-store handle is
    /// pooled BEFORE the clear and must still be serving its own file's original content after it,
    /// having never been asked to reopen.</para>
    /// </summary>
    [Fact]
    public void Clearing_the_database_pools_leaves_another_databases_pool_untouched()
    {
        SeedMarker(_database.Path, "A");
        SeedMarker(_logStore.Path, "L");

        PopulateEveryPool(_database.Path);
        var logStoreConnectionString = LogStoreConnectionString(_logStore.Path);
        Assert.Equal("L", ReadMarker(logStoreConnectionString));

        SqlitePoolCleaner.ClearPoolsFor(_database.Path);

        // Still readable, still its own content: the clear did not reach across.
        Assert.Equal("L", ReadMarker(logStoreConnectionString));

        // And the log store's file is untouched by a database restore.
        Assert.True(File.Exists(_logStore.Path));
    }

    /// <summary>
    /// The set the cleaner walks is the set the assembly can open. Spelled out here so that adding a
    /// shape to <see cref="DatabaseConnectionStrings"/> without extending this test is a visible
    /// choice rather than an accident.
    /// </summary>
    [Fact]
    public void Every_connection_string_shape_names_the_database_and_they_are_distinct()
    {
        var strings = DatabaseConnectionStrings.ForDatabase(_database.Path).ToArray();

        Assert.Equal(2, strings.Length);
        Assert.All(strings, s => Assert.Contains(_database.Path, s, StringComparison.Ordinal));

        // Distinctness is the reason the cleaner needs more than one: identical strings would be
        // one pool, and this whole type would be unnecessary.
        Assert.Equal(strings.Length, strings.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Rebuilds LogStore's own connection string exactly; a plainer one names a different pool.</summary>
    private static string LogStoreConnectionString(string path) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();

    private static void SeedMarker(string path, string marker)
    {
        using var connection = new SqliteConnection(DatabaseConnectionStrings.Maintenance(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS Marker (V TEXT NOT NULL); DELETE FROM Marker; INSERT INTO Marker (V) VALUES ($v);";
        command.Parameters.AddWithValue("$v", marker);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Opens and returns to the pool one connection per string, so every pool the cleaner is
    /// supposed to clear actually holds a handle when it runs.
    /// </summary>
    private static void PopulateEveryPool(string path)
    {
        foreach (var connectionString in DatabaseConnectionStrings.ForDatabase(path))
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
        }
    }

    /// <summary>
    /// Replaces the database with a different file carrying <paramref name="marker"/>, the way the
    /// restore's File.Move does. The replacement is built elsewhere and moved over the path, so a
    /// surviving handle keeps the old inode rather than seeing an edit.
    ///
    /// <para>The finally mirrors <c>RestoreService.ApplyValidatedFiles</c>': the move is EXPECTED to
    /// throw here on Windows — that is how the positive control detects an incomplete clear — and
    /// without this the staged <c>{db}.incoming</c> survives every such run, so a later call's
    /// <see cref="File.Delete(string)"/> is the only thing clearing it and a failure at that point
    /// would surface as a confusing leftover rather than as the detection it is.</para>
    /// </summary>
    private static void ReplaceWithMarker(string path, string marker)
    {
        var incoming = path + ".incoming";
        File.Delete(incoming);

        try
        {
            SeedMarker(incoming, marker);

            // The incoming file's own pool must go, or it holds the handle instead.
            using (var incomingConnection = new SqliteConnection(DatabaseConnectionStrings.Maintenance(incoming)))
            {
                SqliteConnection.ClearPool(incomingConnection);
            }

            File.Move(incoming, path, overwrite: true);
        }
        finally
        {
            // Best-effort: a successful move already consumed the file, and a failure to tidy up
            // must not replace the exception the caller is measuring.
            try
            {
                if (File.Exists(incoming))
                {
                    File.Delete(incoming);
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

    private static string ReadMarker(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT V FROM Marker LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }
}
