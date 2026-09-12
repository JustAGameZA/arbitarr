using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data;

/// <summary>
/// Builds <see cref="DbContextOptions{TContext}"/> for <see cref="ArbitarrDbContext"/> backed
/// by a connection built through <see cref="SqliteConnectionFactory"/>, so EF Core and any raw
/// ADO.NET access (e.g. the maintenance job's VACUUM) share the exact same WAL/busy_timeout
/// configuration instead of two independently-configured connections drifting apart. The
/// connection is handed over CLOSED and configures itself when EF opens it — that is what keeps
/// the shared configuration while letting EF own the handle from the start (see the remarks on
/// <see cref="Create"/>).
/// </summary>
public static class ArbitarrDbContextOptionsFactory
{
    /// <summary>
    /// Builds an UNOPENED connection via <paramref name="connectionFactory"/> — one that applies
    /// busy_timeout and verifies WAL when EF opens it — and wraps it in
    /// <see cref="DbContextOptions{TContext}"/> for <see cref="ArbitarrDbContext"/>. EF opens it on
    /// first use and disposes it with the context, returning it to the pool; see
    /// <c>contextOwnsConnection</c> below, which is what makes that true, and the lazy-adoption
    /// paragraph, which is why the connection must not arrive already open.
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
    ///
    /// <para><b>OWNERSHIP WAS NECESSARY BUT NOT SUFFICIENT: THE CONNECTION MUST ALSO ARRIVE CLOSED
    /// (arb-auam).</b> The flag above says who disposes the connection; it does not say when EF
    /// takes charge of it. <c>RelationalConnection</c> ADOPTS the connection lazily, on the
    /// context's first actual use. So while <c>Create</c> opened the connection eagerly, a context
    /// that was resolved and then disposed WITHOUT being used never adopted the handle at all: the
    /// ownership flag had nothing to act on, the connection was never closed and never RETURNED to
    /// the pool, and — for the same reason set out above — no <c>ClearPool</c> could reach it. The
    /// handle then lived for the process. Options built via <c>Create</c> with no context at all
    /// leaked identically.</para>
    ///
    /// <para><b>Measured, 3/3 each way.</b> On the identical path, an eagerly-opened connection
    /// leaked the directory when the context went unused and did not when the context was touched
    /// once; a connection handed over CLOSED leaked in neither case. That asymmetry — a leak that
    /// disappears the moment anything reads from the context — is why this survived arb-dhua's fix
    /// and why the residue looked like it belonged to particular test classes rather than to every
    /// early-return path in the host (Program.cs, <c>MaintenanceHostedService</c>,
    /// <c>DownloadRefusalRehydrationService</c> all resolve a context they may not use).</para>
    ///
    /// <para><b>So the connection is created but NOT opened here</b>, and it configures itself —
    /// busy_timeout, WAL verification — when EF opens it; see
    /// <see cref="SqliteConnectionFactory.CreateUnopenedConnection"/>, which also records why
    /// passing a bare connection STRING (the obvious smaller fix) was rejected: it would hand EF a
    /// connection this factory never configures. Do not "simplify" this back to
    /// <c>OpenConnection()</c>: both halves are load-bearing and each hides the other's absence —
    /// the flag without the closed connection is arb-auam, and the closed connection without the
    /// flag is arb-dhua.</para>
    /// </remarks>
    public static DbContextOptions<ArbitarrDbContext> Create(SqliteConnectionFactory connectionFactory)
    {
        var connection = connectionFactory.CreateUnopenedConnection();

        return new DbContextOptionsBuilder<ArbitarrDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .Options;
    }
}
