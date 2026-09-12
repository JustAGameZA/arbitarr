using Microsoft.Data.Sqlite;

namespace Arbitarr.Data.Logging;

/// <summary>
/// Persistence for application log rows, in a SQLite database SEPARATE from <c>arbitarr.db</c>
/// (#65 plan §2). Two reasons, both load-bearing:
///
///   1. WRITE CONTENTION. Log writes are frequent and bursty in a way the settings/sources/events
///      tables are not. Putting them in the main database would make every log line compete for the
///      same writer lock the D1-critical search path uses.
///   2. #56's BACKUP. The backup archive is meant to carry configuration — sources, settings, the
///      release-GUID secret. If logs lived in <c>arbitarr.db</c>, every backup would drag a log
///      store around with it, and restoring a backup would restore somebody's old logs over the
///      current ones. A second file keeps the backup about configuration and lets retention vacuum
///      the log store independently.
///
/// Deliberately raw ADO.NET rather than an <see cref="ArbitarrDbContext"/>: this file has exactly
/// one table and no relationships, EF's model/migration machinery would buy nothing, and — the real
/// reason — <see cref="SqliteLoggerProvider"/> must be able to write log rows from inside the
/// logging infrastructure itself, which is constructed before and lives outside the scoped DbContext
/// lifetime. A DbContext here would mean a logger that depends on DI scopes that logging is itself
/// used to construct.
///
/// The connection string is opened per operation rather than held: SQLite connections are cheap,
/// the pooled handle underneath is reused, and a long-lived open handle on a WAL database keeps the
/// -wal file from checkpointing.
/// </summary>
public sealed class LogStore
{
    private readonly string _connectionString;
    private readonly string _databasePath;

    /// <summary>The file name of the log database, under the same config directory as the main one.</summary>
    public const string DatabaseFileName = "arbitarr-logs.db";

    public LogStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString();
    }

    /// <summary>The path of the SQLite file this store writes to.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Creates the table if it does not exist. Called once at startup, before the provider is
    /// registered, so a log write never races schema creation.
    ///
    /// No EF migration and no <c>auto_vacuum = INCREMENTAL</c> here, unlike
    /// <see cref="SqliteConnectionFactory"/>: this store reclaims space with a FULL
    /// <c>VACUUM</c> in <see cref="TrimAsync"/> (see that method), which works regardless of the
    /// auto_vacuum mode the file was created with, and does not depend on having been configured
    /// before the first table existed — a fragile precondition for a file this class creates itself.
    /// </summary>
    public void EnsureCreated()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Logs (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                Time          TEXT    NOT NULL,
                Level         TEXT    NOT NULL,
                Logger        TEXT    NOT NULL,
                Message       TEXT    NOT NULL,
                Exception     TEXT    NULL,
                ExceptionType TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Logs_Time  ON Logs (Time);
            CREATE INDEX IF NOT EXISTS IX_Logs_Level ON Logs (Level);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Writes a batch of entries in ONE transaction. Batching is not a micro-optimisation here: an
    /// unbatched insert per log line means an fsync per log line, which is what makes a naive
    /// database log sink a latency bug on the request path. <see cref="SqliteLoggerProvider"/> is
    /// what accumulates the batch; this method just commits one.
    /// </summary>
    public Task WriteAsync(IReadOnlyList<PendingLogEntry> entries, CancellationToken cancellationToken = default) =>
        WriteAsync(entries, cleanse: null, cancellationToken);

    /// <summary>
    /// arb-qafw: test-only overload. <paramref name="cleanse"/>, when supplied, replaces
    /// <see cref="LogMessageCleanser.Cleanse(string?)"/> for this call — the same test-seam shape as
    /// <see cref="LogMessageCleanser.Cleanse(string?, Func{string, string}?)"/>'s <c>timeoutProbe</c>,
    /// used here to prove that one row's cleanse throwing does not abort the other rows' commit
    /// without relying on a real 250ms regex timeout inside a unit test. <c>null</c> (the default
    /// overload above) on every production call site.
    ///
    /// <para><c>public</c>, not <c>internal</c>: there is no <c>InternalsVisibleTo</c> from this
    /// project to the test assembly.</para>
    /// </summary>
    public async Task WriteAsync(
        IReadOnlyList<PendingLogEntry> entries,
        Func<string?, string?>? cleanse,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        cleanse ??= LogMessageCleanser.Cleanse;

        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Logs (Time, Level, Logger, Message, Exception, ExceptionType)
            VALUES ($time, $level, $logger, $message, $exception, $exceptionType);
            """;

        var time = command.Parameters.Add("$time", SqliteType.Text);
        var level = command.Parameters.Add("$level", SqliteType.Text);
        var logger = command.Parameters.Add("$logger", SqliteType.Text);
        var message = command.Parameters.Add("$message", SqliteType.Text);
        var exception = command.Parameters.Add("$exception", SqliteType.Text);
        var exceptionType = command.Parameters.Add("$exceptionType", SqliteType.Text);

        foreach (var entry in entries)
        {
            time.Value = entry.Time.UtcDateTime.ToString("O");
            level.Value = entry.Level;
            logger.Value = entry.Logger;
            // Cleansing happens HERE, at the single choke point every row passes through, rather
            // than at each call site — a call site that forgets is exactly the leak this is for.
            message.Value = cleanse(entry.Message) ?? string.Empty;
            exception.Value = (object?)cleanse(entry.Exception) ?? DBNull.Value;
            exceptionType.Value = (object?)entry.ExceptionType ?? DBNull.Value;

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one page of rows, newest first, optionally filtered by level and/or logger.
    ///
    /// <para>The level filter is a MINIMUM SEVERITY (arb-pw7r): asking for Warning returns Warning,
    /// Error and Critical. An exact match hid Error and Critical from an operator who selected the
    /// level that "usually needs attention", which is the opposite of what the filter is for.</para>
    /// </summary>
    /// <param name="level">
    /// Minimum level name, matched case-insensitively; null for all levels. A name that is not one
    /// of <see cref="SeverityOrder"/> falls back to an exact match, so an unrecognised value keeps
    /// today's behaviour instead of silently widening the filter to everything — the failure mode
    /// that a "no rows at that level" answer and a "the filter was ignored" answer must not share.
    /// </param>
    /// <param name="logger">Substring of the logger category to match; null for all loggers.</param>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Rows per page.</param>
    public async Task<LogPage> ReadAsync(
        string? level,
        string? logger,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // Resolved ONCE and handed to both commands below, so the WHERE text and the parameters
        // bound into it cannot drift between the COUNT and the page query — a mismatch there shows
        // the operator a total that disagrees with the rows beside it.
        var levelNames = ResolveLevelsAtOrAbove(level);

        var filters = new List<string>();
        if (levelNames.Count > 0)
        {
            // An IN-list of the names at or above the requested severity, each bound as its own
            // parameter. Deliberately NOT a string comparison (`Level >= $level`): the stored value
            // is a LogLevel NAME, and alphabetically "Error" < "Information" < "Warning", so string
            // ordering would make "Warning and above" return nothing but Warning.
            // COLLATE goes on the LEFT operand: for `x IN (...)` SQLite takes the comparison
            // collation from the left-hand expression, so a trailing COLLATE after the list would
            // not apply to the membership test and the case-insensitive match would quietly be lost.
            filters.Add("Level COLLATE NOCASE IN (" + string.Join(", ", levelNames.Select((_, i) => $"$level{i}")) + ")");
        }

        if (!string.IsNullOrWhiteSpace(logger))
        {
            filters.Add("Logger LIKE $logger ESCAPE '\\'");
        }

        var where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);

        await using var connection = OpenConnection();

        int total;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM Logs {where};";
            BindFilters(countCommand, levelNames, logger);
            var scalar = await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            total = Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }

        var entries = new List<LogEntry>();
        await using (var command = connection.CreateCommand())
        {
            // Ordered by Id, not Time: Id is monotonic and unique, so two rows written inside the
            // same batch (identical timestamps to the tick are entirely possible) still have a
            // stable order. Ordering by a non-unique key makes paging able to skip or repeat a row.
            command.CommandText = $"""
                SELECT Id, Time, Level, Logger, Message, Exception, ExceptionType
                FROM Logs
                {where}
                ORDER BY Id DESC
                LIMIT $limit OFFSET $offset;
                """;
            BindFilters(command, levelNames, logger);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new LogEntry(
                    Id: reader.GetInt64(0),
                    Time: DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    Level: reader.GetString(2),
                    Logger: reader.GetString(3),
                    Message: reader.GetString(4),
                    Exception: reader.IsDBNull(5) ? null : reader.GetString(5),
                    ExceptionType: reader.IsDBNull(6) ? null : reader.GetString(6)));
            }
        }

        return new LogPage(entries, total);
    }

    /// <summary>The distinct logger categories present, for the UI's logger filter.</summary>
    public async Task<IReadOnlyList<string>> GetLoggersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT Logger FROM Logs ORDER BY Logger;";

        var loggers = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            loggers.Add(reader.GetString(0));
        }

        return loggers;
    }

    /// <summary>
    /// Deletes rows older than <see cref="LogRetentionPolicy.Retention"/> and then VACUUMs.
    ///
    /// THE VACUUM IS NOT OPTIONAL — see <see cref="LogRetentionPolicy"/> for the full reasoning.
    /// Short version: SQLite's <c>DELETE</c> returns pages to its own freelist, not to the
    /// filesystem, so trimming alone leaves the file at its high-water mark permanently and the
    /// disk fills anyway while the trim appears to work.
    ///
    /// A full <c>VACUUM</c> (not <c>PRAGMA incremental_vacuum</c>, which
    /// <see cref="Arbitarr.Data.Maintenance.MaintenanceJob"/> uses on the main database) because
    /// that pragma is a silent no-op unless <c>auto_vacuum = INCREMENTAL</c> was set before the
    /// file's first table was created. This store creates its own file, so depending on that
    /// ordering would be a trap; a full VACUUM has no such precondition. It rewrites the file, but
    /// this runs on the maintenance interval against a small database, never on the request path.
    ///
    /// VACUUM cannot run inside a transaction, hence the separate command after the delete commits.
    /// </summary>
    /// <returns>How many rows were deleted.</returns>
    public async Task<int> TrimAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var cutoff = now - LogRetentionPolicy.Retention;

        await using var connection = OpenConnection();

        int deleted;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM Logs WHERE Time < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", cutoff.UtcDateTime.ToString("O"));
            deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var vacuum = connection.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // TRUNCATE checkpoint, and it is required for the vacuum to mean anything here. In WAL mode
        // the rewritten pages land in the -wal file, and the MAIN database file keeps its old size
        // until a checkpoint folds them back in — measured, not assumed: without this the file stays
        // at its high-water mark for the entire life of the process and only shrinks when the last
        // connection closes, which for a container that runs for months is never. That is precisely
        // the disk-fill failure the trim exists to prevent, so the checkpoint is part of the
        // retention mechanism rather than a tidy-up after it. TRUNCATE (not PASSIVE) additionally
        // resets the -wal file itself, which is the other file growing on that config volume.
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return deleted;
    }

    /// <summary>The largest page size <see cref="ReadAsync"/> will serve, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <summary>The page size used when the caller does not specify one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// The level names this store can hold, least severe first.
    ///
    /// <para>These are <c>Microsoft.Extensions.Logging.LogLevel.ToString()</c> values, because that
    /// is literally what <see cref="SqliteLoggerProvider"/> writes into the Level column — so the
    /// list is the spelling of the data, not a parallel vocabulary that could drift from it.
    /// Serilog's own names for the two ends (Verbose, Fatal) are accepted as aliases by
    /// <see cref="ResolveLevelsAtOrAbove"/> so a row written through a Serilog-shaped sink still
    /// sorts, but they are not offered here as canonical.</para>
    ///
    /// <para><c>None</c> is absent deliberately: it is a "log nothing" sentinel, never a severity a
    /// row can carry, and including it would make "None and above" mean "everything".</para>
    /// </summary>
    public static readonly IReadOnlyList<string> SeverityOrder = new[]
    {
        "Trace", "Debug", "Information", "Warning", "Error", "Critical",
    };

    /// <summary>Serilog's spellings for the levels whose MEL name differs, mapped onto the MEL one.</summary>
    private static readonly Dictionary<string, string> SeverityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Verbose"] = "Trace",
        ["Fatal"] = "Critical",
    };

    /// <summary>
    /// Expands a requested level into every level name at or above it in severity.
    ///
    /// <para>Returns an empty list for a null/blank request (no level filter at all), and a
    /// single-element list holding the request verbatim when the name is not recognised — an
    /// unknown name then behaves exactly as it did before this became a minimum-severity filter
    /// (exact match, almost certainly zero rows) rather than widening to the whole store, which is
    /// the one wrong answer a security-relevant surface must not give silently.</para>
    ///
    /// <para><c>public</c> rather than <c>internal</c>: the test assembly has no
    /// <c>InternalsVisibleTo</c> here (same reason as <see cref="WriteAsync(IReadOnlyList{PendingLogEntry}, Func{string?, string?}?, CancellationToken)"/>'s
    /// test seam), and <c>LogsEndpoint</c>'s documentation refers to it across the project boundary.</para>
    /// </summary>
    public static IReadOnlyList<string> ResolveLevelsAtOrAbove(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return Array.Empty<string>();
        }

        var name = level.Trim();
        if (SeverityAliases.TryGetValue(name, out var canonical))
        {
            name = canonical;
        }

        var index = -1;
        for (var i = 0; i < SeverityOrder.Count; i++)
        {
            if (string.Equals(SeverityOrder[i], name, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return new[] { level };
        }

        // Every canonical name at or above the match, PLUS the aliases of those names, so a store
        // that happens to hold "Fatal" rows is still covered by a Warning request.
        var names = new List<string>();
        for (var i = index; i < SeverityOrder.Count; i++)
        {
            names.Add(SeverityOrder[i]);
            foreach (var alias in SeverityAliases.Where(pair => pair.Value == SeverityOrder[i]))
            {
                names.Add(alias.Key);
            }
        }

        return names;
    }

    private static void BindFilters(SqliteCommand command, IReadOnlyList<string> levelNames, string? logger)
    {
        for (var i = 0; i < levelNames.Count; i++)
        {
            command.Parameters.AddWithValue($"$level{i}", levelNames[i]);
        }

        if (!string.IsNullOrWhiteSpace(logger))
        {
            // Escape the LIKE wildcards so an operator typing "%" in the logger filter searches for
            // a literal percent sign rather than matching every row.
            var escaped = logger
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);
            command.Parameters.AddWithValue("$logger", $"%{escaped}%");
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        try
        {
            using var pragma = connection.CreateCommand();
            // WAL so a reader serving GET /api/admin/logs never blocks the batch writer, and a
            // busy_timeout so a concurrent trim/vacuum makes a writer wait rather than throw
            // SQLITE_BUSY. Unlike SqliteConnectionFactory this does NOT verify the mode took
            // effect and refuse to proceed: that factory guards the D1-critical search path, where
            // silently losing WAL is a correctness problem worth failing startup over. Here the
            // worst case is a slower log write, and throwing from inside the logging sink would
            // turn a performance nuisance into a failure of the thing meant to record failures.
            pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 3000;";
            pragma.ExecuteNonQuery();
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }
}

/// <summary>
/// A log entry queued for writing, before it has a row id. Separate from <see cref="LogEntry"/>
/// because the id is assigned by SQLite on insert, and a nullable/sentinel id on the read model
/// would make every consumer handle a state that only exists inside the sink's queue.
/// </summary>
public sealed record PendingLogEntry(
    DateTimeOffset Time,
    string Level,
    string Logger,
    string Message,
    string? Exception,
    string? ExceptionType);
