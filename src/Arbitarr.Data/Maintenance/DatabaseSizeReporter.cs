using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Maintenance;

/// <summary>
/// Size of the SQLite database: total on-disk bytes plus a row count per mapped table (M7-8 / M7
/// step 8: the settings surface shows the cost of a retention choice beside the setting that governs
/// it). Row counts rather than per-table bytes because the bundled SQLite build ships without the
/// <c>dbstat</c> virtual table, and a count is what a retention ceiling is actually expressed in.
/// </summary>
/// <param name="TotalBytes"><c>page_count * page_size</c> — the file size SQLite accounts for, including free pages.</param>
/// <param name="TableRows">Rows per table, keyed by SQLite table name, for every entity type in the EF model.</param>
public sealed record DatabaseSizeReport(long TotalBytes, IReadOnlyDictionary<string, long> TableRows)
{
    /// <summary>Rows in <paramref name="table"/>, or null when it is not a mapped table.</summary>
    public long? RowsIn(string table) => TableRows.TryGetValue(table, out var rows) ? rows : null;
}

/// <summary>Reads <see cref="DatabaseSizeReport"/> from the live <see cref="ArbitarrDbContext"/> connection.</summary>
public sealed class DatabaseSizeReporter(ArbitarrDbContext db)
{
    private readonly ArbitarrDbContext _db = db;

    /// <summary>SQLite table name for the entity type <typeparamref name="TEntity"/>, as mapped by the EF model.</summary>
    public string TableNameOf<TEntity>() =>
        _db.Model.FindEntityType(typeof(TEntity))?.GetTableName()
        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} is not mapped to a table.");

    public async Task<DatabaseSizeReport> ReadAsync(CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var total = await ScalarAsync(connection, "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size();", cancellationToken);

            // Table names come from the EF model, never from input; quoting guards only against reserved words.
            var rows = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var table in _db.Model.GetEntityTypes().Select(e => e.GetTableName()).Where(t => t is not null).Distinct(StringComparer.Ordinal))
            {
                rows[table!] = await ScalarAsync(connection, $"SELECT COUNT(*) FROM \"{table!.Replace("\"", "\"\"", StringComparison.Ordinal)}\";", cancellationToken);
            }

            return new DatabaseSizeReport(total, rows);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }
}
