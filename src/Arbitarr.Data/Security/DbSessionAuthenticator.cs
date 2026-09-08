using Arbitarr.Core.Security;
using Arbitarr.Data.Settings;

namespace Arbitarr.Data.Security;

/// <summary>
/// #44: the single resolution point the admin gate consults for a SESSION cookie, mirroring
/// <see cref="DbAdminKeyResolver"/>'s role for a key. It answers the same question — "may this
/// presented value do this?" — and returns the same <see cref="AdminKeyResolution"/> type, so the
/// filter contains no session logic and makes one scope decision over whichever credential answered.
///
/// <para><b>ONE MODEL, NOT TWO.</b> This class exists precisely so that adopting #58's primitive is
/// a structural fact rather than a claim: there is no session-specific outcome enum here, no second
/// scope vocabulary, and no permission table. A session that verifies is authorized through the
/// same <see cref="ApiKeyScope"/> comparison an API key goes through, written once, below.</para>
///
/// <para><b>WHY A SESSION NEVER RETURNS <see cref="AdminKeyResolutionOutcome.NotConfigured"/>.</b>
/// That outcome means something specific and load-bearing: no credential of ANY kind exists on this
/// deployment, which is what #43's local-network bootstrap bypass keys off. If a session could
/// produce it, presenting any junk cookie would re-enter the bypass and "anyone on the LAN" would
/// again be admitted — the exact thing constraint 1 of the owner ruling forbids. Every failure here
/// is therefore <see cref="AdminKeyResolutionOutcome.Rejected"/>, and the filter additionally
/// refuses to let a Rejected session overwrite a key's NotConfigured outcome.</para>
/// </summary>
public sealed class DbSessionAuthenticator : ISessionAuthenticator
{
    /// <summary>
    /// The label a session-authenticated request is attributed by in the gate's logs. Deliberately
    /// not the username: the log line's job is to say which KIND of credential opened the gate, and
    /// carrying an account name into every gated request's log would put an identifier in the
    /// persistent log store (#65) for every request an operator makes. The session's user is
    /// recoverable from the sessions table when an investigation actually needs it.
    /// </summary>
    public const string SessionLabel = "operator session";

    private readonly SessionRepository _sessions;
    private readonly SettingsRepository _settings;
    private readonly ISessionActivityRecorder _activityRecorder;

    public DbSessionAuthenticator(
        SessionRepository sessions,
        SettingsRepository settings,
        ISessionActivityRecorder activityRecorder)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _activityRecorder = activityRecorder ?? throw new ArgumentNullException(nameof(activityRecorder));
    }

    public async Task<AdminKeyResolution> AuthenticateAsync(
        string? presentedToken,
        ApiKeyScope requiredScope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return AdminKeyResolution.Rejected;
        }

        var (idleTimeout, _) = await _settings.GetSessionLifetimesAsync(cancellationToken);

        var session = await _sessions.FindLiveByPresentedTokenAsync(
            presentedToken,
            idleTimeout,
            cancellationToken);

        if (session is null)
        {
            // Unknown, revoked (logged out), idle-expired, or past its absolute expiry — one answer
            // for all of them, matching ApiKeyRepository.FindLiveByPresentedKeyAsync's posture.
            return AdminKeyResolution.Rejected;
        }

        // Keeps an actively used session from hitting its idle expiry. Off the request path and
        // throttled — see ISessionActivityRecorder.
        _activityRecorder.RecordSeen(session.Id);

        // THE SINGLE SCOPE DECISION, in the same shape DbAdminKeyResolver.Authorize uses: a
        // comparison over the enum's declared order, so a scope added between the two would inherit
        // the ordering rather than fall through a missing case.
        //
        // A session carries Admin scope because Arbitarr has one operator role (see
        // ISessionAuthenticator). This is written as a comparison rather than an unconditional
        // "authorized" so that when a narrower role does exist, it arrives as a different value in
        // THIS vocabulary and this line already handles it correctly.
        const ApiKeyScope sessionScope = ApiKeyScope.Admin;

        return sessionScope >= requiredScope
            ? new AdminKeyResolution(AdminKeyResolutionOutcome.Authorized, null, SessionLabel, sessionScope)
            : new AdminKeyResolution(AdminKeyResolutionOutcome.InsufficientScope, null, SessionLabel, sessionScope);
    }
}
