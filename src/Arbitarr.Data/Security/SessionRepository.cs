using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Security;

/// <summary>
/// The plaintext of a newly issued session token, returned from
/// <see cref="SessionRepository.IssueAsync"/> and from nowhere else, ever again.
///
/// <para>The same one-shot posture <see cref="CreatedApiKey"/> takes, for the same reason: this is
/// the only shape in which a live session credential can reach a caller, and it exists solely so
/// the login handler can put it in a <c>Set-Cookie</c> header. It is not stored, not logged, and
/// not reachable from any read method — <see cref="SessionEntry"/> has no column to read it back
/// from.</para>
/// </summary>
/// <param name="Entry">The persisted row, carrying no secret.</param>
/// <param name="PlaintextToken">The generated token value. Written to one cookie, then gone.</param>
public sealed record IssuedSession(SessionEntry Entry, string PlaintextToken);

/// <summary>
/// #44: issuance, verification, and revocation of server-side sessions.
///
/// <para><b>WHY THE ROWS EXIST AT ALL.</b> A self-contained signed token would need no table and no
/// lookup — and would make logout a lie, because the server could not refuse a token it had already
/// signed. The issue lists "no sign-out, no session expiry" as defects to fix, so revocation has to
/// be real. That costs one indexed lookup per cookie-authenticated request, which is the price of
/// the feature rather than an oversight.</para>
/// </summary>
public sealed class SessionRepository
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public SessionRepository(ArbitarrDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Issues a session for <paramref name="userId"/>, returning the plaintext token once.
    /// <paramref name="absoluteLifetime"/> fixes <see cref="SessionEntry.AbsoluteExpiresAt"/> now
    /// and it is never extended afterwards.
    /// </summary>
    public async Task<IssuedSession> IssueAsync(
        long userId,
        TimeSpan absoluteLifetime,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var token = SessionToken.Generate();

        var entry = new SessionEntry
        {
            UserId = userId,
            TokenHash = SessionToken.Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            AbsoluteExpiresAt = now.Add(absoluteLifetime),
        };

        _dbContext.Sessions.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new IssuedSession(entry, token);
    }

    /// <summary>
    /// The verification lookup: hashes <paramref name="presentedToken"/> and returns the matching
    /// row when it is live — not revoked, not past its absolute expiry, and not idle beyond
    /// <paramref name="idleTimeout"/>. Returns null otherwise, with no distinction between the
    /// reasons: a caller holding a dead cookie gets one answer, and telling it which flavour of
    /// dead is information it has no use for.
    ///
    /// <para>Expiry is evaluated HERE rather than trusted from a stored flag, so a session cannot
    /// outlive its window merely because no cleanup job has run yet. The maintenance prune is a
    /// storage concern, never the security boundary.</para>
    /// </summary>
    public async Task<SessionEntry?> FindLiveByPresentedTokenAsync(
        string presentedToken,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return null;
        }

        var hash = SessionToken.Hash(presentedToken);
        var now = _timeProvider.GetUtcNow();

        var entry = await _dbContext.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, cancellationToken);

        if (entry is null)
        {
            return null;
        }

        if (entry.AbsoluteExpiresAt <= now || entry.LastSeenAt.Add(idleTimeout) <= now)
        {
            return null;
        }

        return entry;
    }

    /// <summary>
    /// Revokes one session by its presented token — what logout does. Idempotent, and silent about
    /// an unknown token: a caller logging out with a cookie the server has never seen has already
    /// achieved what it asked for.
    ///
    /// <para>Revokes by TOKEN rather than by id so the logout route needs no session id in its body
    /// and cannot be aimed at somebody else's session. The only session a caller can end is the one
    /// it can already present.</para>
    /// </summary>
    public async Task RevokeByPresentedTokenAsync(string presentedToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return;
        }

        var hash = SessionToken.Hash(presentedToken);

        var entry = await _dbContext.Sessions
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.RevokedAt == null, cancellationToken);

        if (entry is null)
        {
            return;
        }

        entry.RevokedAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// #96: revokes every live session of <paramref name="userId"/> EXCEPT
    /// <paramref name="exceptSessionId"/>, returning the number revoked. What a password change does,
    /// and the only caller.
    ///
    /// <para><b><paramref name="exceptSessionId"/> IS REQUIRED, NOT A NULLABLE CONVENIENCE.</b> An
    /// overload that revoked everything would be one call site away from signing the operator out of
    /// the tab they just used — which reads as the change having failed, and leaves them re-typing a
    /// password they set five seconds ago on a machine they are already sitting at. Nothing in the
    /// codebase needs revoke-all, so the parameter that prevents it is mandatory.</para>
    ///
    /// <para><b>ROWS ARE TOMBSTONED, NOT DELETED</b>, exactly as
    /// <see cref="RevokeByPresentedTokenAsync"/> and <see cref="ApiKeyRepository"/> do it. #95's
    /// maintenance prune is what removes them, and it is a storage concern, never the security
    /// boundary — see this type's note on why the rows exist at all.</para>
    ///
    /// <para><b>EXPIRY IS NOT RE-EVALUATED HERE.</b> An already-expired row is revoked too, which is
    /// harmless. The liveness rule lives in <see cref="FindLiveByPresentedTokenAsync"/>; duplicating
    /// it here would be a second place for it to drift.</para>
    /// </summary>
    public async Task<int> RevokeAllForUserExceptAsync(
        long userId,
        long exceptSessionId,
        CancellationToken cancellationToken)
    {
        var doomed = await _dbContext.Sessions
            .Where(s => s.UserId == userId && s.Id != exceptSessionId && s.RevokedAt == null)
            .ToListAsync(cancellationToken);

        if (doomed.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var session in doomed)
        {
            session.RevokedAt = now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return doomed.Count;
    }

    /// <summary>
    /// Stamps <see cref="SessionEntry.LastSeenAt"/>, which is what keeps an active session from
    /// hitting its idle expiry. Called only from <c>ThrottledSessionActivityRecorder</c>, which
    /// coalesces it — see <see cref="IApiKeyLastUsedRecorder"/> for why this must not become a
    /// synchronous write on every authenticated request.
    ///
    /// <para>A session revoked between verification and this write is left alone, matching
    /// <see cref="ApiKeyRepository.RecordLastUsedAsync"/>: the stamp is not worth resurrecting a
    /// logged-out session's activity for.</para>
    /// </summary>
    public async Task RecordSeenAsync(long id, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.Sessions.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (entry is null || entry.RevokedAt is not null)
        {
            return;
        }

        entry.LastSeenAt = seenAt;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
