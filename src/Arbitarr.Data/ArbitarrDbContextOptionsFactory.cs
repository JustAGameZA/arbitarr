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
    /// <para><b>Both halves of this hand-over are load-bearing, and the reasoning — the history, the
    /// alternatives beaten and the measurements — is in
    /// <c>docs/adr/0017-sqlite-connection-lifetime-for-ef-contexts.md</c> rather than repeated
    /// here.</b></para>
    ///
    /// <para><b><c>contextOwnsConnection: true</c> (arb-dhua).</b> The overload taking a
    /// <c>DbConnection</c> leaves ownership with the CALLER by default, so a disposed context does
    /// NOT dispose its connection — and a connection never disposed is never RETURNED to the pool,
    /// where <c>SqliteConnection.ClearPool</c> closes only what a pool HOLDS. Ownership alone is
    /// still not sufficient: disposal only returns the handle to the pool, which keeps a share lock
    /// on Windows, so a caller needing the file deleted must clear the pool too.</para>
    ///
    /// <para><b>The connection must also arrive CLOSED (arb-auam).</b> The flag says who disposes
    /// the connection, not when EF takes charge of it: <c>RelationalConnection</c> ADOPTS the
    /// connection lazily, on the context's first actual use, so a context resolved and disposed
    /// WITHOUT being used would never adopt an eagerly-opened handle and the flag would have nothing
    /// to act on. This is a PRODUCTION defect, not a test-harness one — a long-running host leaks a
    /// connection per early-return scope (Program.cs, <c>MaintenanceHostedService</c>,
    /// <c>DownloadRefusalRehydrationService</c> all resolve a context they may not use).</para>
    ///
    /// <para><b>So the connection is created but NOT opened here</b>, and it configures itself —
    /// busy_timeout, WAL verification — when EF opens it; see
    /// <see cref="SqliteConnectionFactory.CreateUnopenedConnection"/>. Do not "simplify" this back
    /// to <c>OpenConnection()</c>, and do not drop the flag: each hides the other's absence — the
    /// flag without the closed connection is arb-auam, and the closed connection without the flag is
    /// arb-dhua.</para>
    /// </remarks>
    public static DbContextOptions<ArbitarrDbContext> Create(SqliteConnectionFactory connectionFactory)
    {
        var connection = connectionFactory.CreateUnopenedConnection();

        return new DbContextOptionsBuilder<ArbitarrDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true)
            .Options;
    }
}
