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
    /// <see cref="ArbitarrDbContext"/>. Disposing the resulting context disposes that connection,
    /// returning it to the pool — see <c>contextOwnsConnection</c> below, which is what makes that
    /// true.
    /// </summary>
    /// <remarks>
    /// <para><b><c>contextOwnsConnection: true</c> IS LOAD-BEARING, AND ITS DEFAULT IS THE OPPOSITE
    /// OF WHAT THIS COMMENT USED TO CLAIM (arb-dhua).</b> The overload taking an already-open
    /// <c>DbConnection</c> leaves ownership with the CALLER by default: disposing the context then
    /// does NOT dispose the connection. Because every connection here is opened eagerly, one per
    /// scope, that meant every disposed <see cref="ArbitarrDbContext"/> in the process left its
    /// connection open forever — never disposed, and therefore never RETURNED to the pool.</para>
    ///
    /// <para><b>Why that was invisible, and why no pool clear could fix it.</b>
    /// <c>SqliteConnection.ClearPool</c> closes the connections a pool HOLDS; a connection that was
    /// never returned is not among them. So the handle survived every scope, every clear, and host
    /// shutdown itself — which is what made the integration factories unable to delete their config
    /// directories (~22,000 leaked under <c>%TEMP%</c>) and what sent three previous attempts
    /// looking for a wider set of connection STRINGS. The inventory was never the problem.</para>
    ///
    /// <para><b>Measured, on the real shape.</b> With the connection handed over as it is now, the
    /// directory deletes; with the previous <c>UseSqlite(connection)</c> it does not, even when the
    /// exact same pool clear runs immediately afterwards. Note that ownership alone is not
    /// sufficient: disposal only RETURNS the handle to the pool, which still holds a share lock on
    /// Windows, so the caller's pool clear is the other half and both are required.</para>
    ///
    /// <para>This is a PRODUCTION defect, not a test-harness one — a long-running host leaked a
    /// connection per scope for the same reason. Do not "simplify" this argument away.</para>
    /// </remarks>
    public static DbContextOptions<ArbitarrDbContext> Create(SqliteConnectionFactory connectionFactory)
    {
        var connection = connectionFactory.OpenConnection();

        return new DbContextOptionsBuilder<ArbitarrDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .Options;
    }
}
