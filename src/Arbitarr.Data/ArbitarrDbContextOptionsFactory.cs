using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data;

/// <summary>
/// Builds <see cref="DbContextOptions{TContext}"/> for <see cref="ArbitarrDbContext"/> backed
/// by a connection opened through <see cref="SqliteConnectionFactory"/>, so EF Core and any raw
/// ADO.NET access (e.g. the maintenance job's VACUUM) share the exact same WAL/busy_timeout
/// configuration instead of two independently-configured connections drifting apart.
/// </summary>
public static class ArbitarrDbContextOptionsFactory
{
    /// <summary>
    /// Opens a new connection via <paramref name="connectionFactory"/> (WAL-verified,
    /// busy_timeout applied) and wraps it in <see cref="DbContextOptions{TContext}"/> for
    /// <see cref="ArbitarrDbContext"/>. The caller owns disposing the resulting context
    /// (which, by default, disposes the connection it was handed since EF Core takes ownership
    /// of an externally-provided open connection unless told otherwise).
    /// </summary>
    public static DbContextOptions<ArbitarrDbContext> Create(SqliteConnectionFactory connectionFactory)
    {
        var connection = connectionFactory.OpenConnection();

        return new DbContextOptionsBuilder<ArbitarrDbContext>()
            .UseSqlite(connection)
            .Options;
    }
}
