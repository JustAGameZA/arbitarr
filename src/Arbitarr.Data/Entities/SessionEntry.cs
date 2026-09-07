namespace Arbitarr.Data.Entities;

/// <summary>
/// #44: one server-side session. The row exists so that logout and expiry are REAL rather than a
/// discarded cookie (plan §3.4) — a self-contained signed token would leave the server unable to
/// end a session it had already issued, which is exactly the property the issue asks for ("no
/// sign-out, no session expiry" is listed as a defect of the status quo).
///
/// <para><b>THE SECRET RULE.</b> No column holds a token value; only <see cref="TokenHash"/>, a
/// SHA-256 digest (see <c>Arbitarr.Core.Security.SessionToken</c>). A leak of this table, or of a
/// #56 backup, yields hashes rather than replayable cookies.</para>
///
/// <para><b>TWO EXPIRIES, NOT ONE.</b> <see cref="AbsoluteExpiresAt"/> is fixed at issue time and
/// never extended, so a stolen cookie has a hard ceiling on its usefulness no matter how actively
/// it is used. <see cref="LastSeenAt"/> drives the separate IDLE expiry, which is evaluated against
/// the configured idle timeout rather than stored as a second timestamp — storing a rolling
/// "idle expires at" would mean a write on every request, which is the cost
/// <c>IApiKeyLastUsedRecorder</c> exists to avoid on the key path. Either bound alone is
/// insufficient: idle-only lets a session live forever under a script that touches it hourly, and
/// absolute-only leaves an abandoned browser authenticated for the whole window.</para>
/// </summary>
public sealed class SessionEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>The account this session authenticates as.</summary>
    public long UserId { get; set; }

    /// <summary>
    /// SHA-256 of the session token, lowercase hex — never the token itself. Unique and indexed: it
    /// is the lookup column, because verification hashes the presented cookie and finds the row.
    /// </summary>
    public required string TokenHash { get; set; }

    /// <summary>When the session was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When a request last presented this session. Compared against the configured idle timeout on
    /// every authentication; written back through a throttle
    /// (<c>ThrottledSessionActivityRecorder</c>) rather than synchronously, for the reason
    /// <see cref="Arbitarr.Core.Security.IApiKeyLastUsedRecorder"/> documents at length — this is
    /// the hot path of every cookie-authenticated request on a single-writer SQLite database.
    /// </summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// The hard ceiling, fixed when the session was issued and never extended. A session is dead
    /// once the clock passes this, however recently it was used.
    /// </summary>
    public DateTimeOffset AbsoluteExpiresAt { get; set; }

    /// <summary>
    /// When the session was revoked (by an explicit logout), or null while it is live. A tombstone
    /// rather than a DELETE, matching <see cref="ApiKeyEntry.RevokedAt"/>: it makes "was this
    /// session ended deliberately, or did it simply expire?" answerable, and it means a
    /// re-presented token can never match a freshly inserted row that happened to reuse the id.
    /// Pruned by the maintenance job rather than kept forever.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
