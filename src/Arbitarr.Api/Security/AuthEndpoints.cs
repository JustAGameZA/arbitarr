using Arbitarr.Api.Admin;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Security;

/// <summary>Request body for <c>POST /api/auth/setup</c> and <c>POST /api/auth/login</c>.</summary>
public sealed record CredentialsRequest(string? Username, string? Password);

/// <summary>
/// #96: request body for <c>POST /api/auth/password</c>.
///
/// <para>No username and no user id: the account is whichever one the session resolves to, so there
/// is no field a caller could aim at somebody else's account. That is the same posture
/// <c>LogoutAsync</c> takes and it is what keeps this route from needing an authorization check
/// beyond "is this a live session".</para>
///
/// <para><b>NO <c>ToString</c> OVERRIDE, AND DO NOT ADD ONE.</b> A positional record's generated
/// <c>ToString</c> prints every property, so any log line that interpolated one of these would put
/// both passwords into the persistent log store (#65). Nothing on this path logs it today —
/// <c>PasswordLogInjectionTests</c> is what fails if that changes — and the record staying
/// unremarkable is half of why.</para>
/// </summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>
/// The answer to <c>GET /api/auth/session</c> — who, if anyone, this request is.
///
/// <para>Note what is absent: no token, no hash, and deliberately no nullable field a future edit
/// could start populating with either — the same structural posture <c>ApiKeyResponse</c> takes.
/// The session token is <c>HttpOnly</c> and the frontend never needs to see it; a field here would
/// be the one path by which it could reach script.</para>
/// </summary>
/// <param name="Authenticated">Whether this request carried a live session.</param>
/// <param name="Username">The signed-in operator, or null when not authenticated.</param>
/// <param name="SetupRequired">
/// True when the instance has NO accounts at all, which is what tells the SPA to route to first-run
/// setup rather than to the login form. Answered here rather than by a separate endpoint so the
/// route guard makes ONE call and cannot render a login page to an instance that has no account to
/// log into — the loop the plan's redirect concern is about.
/// </param>
public sealed record SessionResponse(bool Authenticated, string? Username, bool SetupRequired);

/// <summary>
/// #44: the human authentication surface — first-run setup, login, logout, and whoami.
///
/// <para><b>WHY EVERY ROUTE HERE IS CLASSIFIED <see cref="RouteClassification.PublicRead"/>.</b>
/// This is the one classification decision in the issue that looks wrong at first glance and is
/// not, so it is recorded here rather than left to be re-derived.</para>
///
/// <para><see cref="RouteClassification"/> answers exactly one question in this codebase: is this
/// route wrapped by <c>AdminApiKeyFilter</c>? <see cref="RouteClassification.AdminMutating"/> means
/// "gated by an admin credential", and <c>AdminApiKeyRouteEnumerationTests</c> enforces both
/// directions of that — every AdminMutating route must 401/503 without a key, and every PublicRead
/// route must never do so. Login and setup MUST be reachable without a credential; that is what
/// they exist to establish. Classifying them AdminMutating would fail that sweep, and would be
/// false: they are not gated by the admin key and must not be.</para>
///
/// <para>They are nonetheless mutating, and the classification does NOT capture that — the name is
/// about the GATE, not about side effects. So each route carries its own guard instead, and those
/// guards are the actual security boundary here:</para>
/// <list type="bullet">
/// <item><b>setup</b> — refused unless the instance has zero accounts AND the caller is on a
/// trusted network (<see cref="TrustedNetwork"/>). Both, not either.</item>
/// <item><b>login</b> — rate-limited per username and per address
/// (<see cref="LoginRateLimiter"/>), and it verifies a credential by definition.</item>
/// <item><b>logout</b> — acts only on the session the caller can already present; it cannot be
/// aimed at anyone else's.</item>
/// <item><b>session</b> — returns only what the caller already knows about itself.</item>
/// <item><b>password</b> (#96) — requires a LIVE SESSION, resolved by the handler itself, and
/// deliberately refuses the admin key. See <see cref="ChangePasswordAsync"/>.</item>
/// </list>
///
/// <para><b>WHY THE PASSWORD ROUTE IS PublicRead EVEN THOUGH IT 401s.</b> This looks like a
/// contradiction and is not, because of the paragraph above: the classification says only "not
/// wrapped by <c>AdminApiKeyFilter</c>", which is exactly true here and is the whole point.
/// Classifying it <see cref="RouteClassification.AdminMutating"/> would attach that filter, and the
/// filter accepts EITHER credential — so an admin key would then be able to rotate a human's
/// password, which is the one thing #96 exists to forbid. The route 401s because it requires a
/// SESSION, which is a different gate from the one the classification names. Because the sweep in
/// <c>AdminApiKeyRouteEnumerationTests</c> asserts that no PublicRead route ever answers 401, this
/// route is named there as an explicit, positively-asserted exemption — see
/// <c>Every_PublicRead_route_never_requires_the_admin_key</c>.</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Bodies are bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked in the
/// handler, exactly as <c>AdminSecurityEndpoints</c> and <c>AdminApiKeyEndpoints</c> do. The
/// original reason was to keep the admin gate strictly ahead of model binding; these routes are
/// ungated, so that specific leak does not apply — but the convention is followed anyway, because a
/// route that answers 400-on-malformed-body versus 415-on-missing-body still discriminates for a
/// prober, and because a future change that gates one of these would otherwise reintroduce the leak
/// silently. One convention, no exceptions to remember.</para>
///
/// <para><b>THE RECOVERY STORY.</b> There is none, deliberately (see <c>UserEntry</c>): no reset
/// token, no recovery e-mail, no secret question. An operator locked out restores the config
/// database from a #56 backup, or clears the Users and Sessions tables through the config bind
/// mount, which reopens first-run setup. That is stated in README.md under "Signing in, and what
/// to do when you cannot" — with the rate-limit case and the still-working admin key alongside it —
/// and in the login surface's own help text, because an undocumented "none" reads as an oversight
/// rather than a decision.</para>
/// </summary>
public static class AuthEndpoints
{
    public const string SetupRoute = "/api/auth/setup";
    public const string LoginRoute = "/api/auth/login";
    public const string LogoutRoute = "/api/auth/logout";
    public const string SessionRoute = "/api/auth/session";

    /// <summary>
    /// #96. Under <c>/api/auth/</c> rather than <c>/api/admin/</c> on purpose: the admin prefix is
    /// what <c>apiFetch</c>'s <c>needsAdminKey</c> and <c>AdminApiKeyFilter</c> both key off, and
    /// both attach or accept the machine credential this route must refuse. A human-credential route
    /// under the machine-credential prefix would invite exactly that confusion.
    /// </summary>
    public const string PasswordRoute = "/api/auth/password";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        // See the type doc for why PublicRead is the correct — and the only passing —
        // classification for these four. It means "not wrapped by AdminApiKeyFilter", which is
        // exactly true of a surface whose job is to authenticate a caller who has no credential yet.
        endpoints.MapGet(SessionRoute, GetSessionAsync)
            .WithClassification(RouteClassification.PublicRead);

        endpoints.MapPost(SetupRoute, CreateFirstAccountAsync)
            .WithClassification(RouteClassification.PublicRead);

        endpoints.MapPost(LoginRoute, LoginAsync)
            .WithClassification(RouteClassification.PublicRead);

        endpoints.MapPost(LogoutRoute, LogoutAsync)
            .WithClassification(RouteClassification.PublicRead);

        // #96. PublicRead here means "not wrapped by AdminApiKeyFilter" and nothing else — see the
        // type doc's note on why that is the correct classification for a route that nonetheless
        // requires a session, and where the enumeration sweep names it as an exemption.
        endpoints.MapPost(PasswordRoute, ChangePasswordAsync)
            .WithClassification(RouteClassification.PublicRead);
    }

    /// <summary>
    /// Whoami. Answers both "am I signed in?" and "does this instance have any account yet?" in one
    /// response, because the route guard needs both to decide between the app, the login page, and
    /// the setup page — and asking in two calls would let it observe an inconsistent pair.
    /// </summary>
    private static async Task<IResult> GetSessionAsync(
        HttpContext httpContext,
        ISessionAuthenticator authenticator,
        SessionRepository sessions,
        UserRepository users,
        SettingsRepository settings,
        LanPassthroughOptions lanPassthrough,
        CancellationToken cancellationToken)
    {
        var setupRequired = !await users.AnyUserAsync(cancellationToken);

        // arb-lan-passthrough (ADR 0012): when passthrough is on and the caller is on a trusted
        // socket peer, the SPA must not send them to the login screen, because the gate
        // (AdminApiKeyFilter) will admit their subsequent admin calls with no cookie. A REAL session
        // is still consulted first so a signed-in operator keeps seeing their username; passthrough
        // is the fallback for a local caller with no (or no valid) cookie, and reports authenticated
        // with no username, there being no account row to name. Setup, if still required, wins: an
        // unclaimed instance should still offer account creation.
        var passthroughAdmits = lanPassthrough.Enabled && !setupRequired
            && TrustedNetwork.IsTrusted(httpContext.Connection.RemoteIpAddress);

        if (!httpContext.Request.Cookies.TryGetValue(ISessionAuthenticator.CookieName, out var token)
            || string.IsNullOrEmpty(token))
        {
            return Results.Ok(new SessionResponse(Authenticated: passthroughAdmits, Username: null, setupRequired));
        }

        var resolution = await authenticator.AuthenticateAsync(token, ApiKeyScope.Admin, cancellationToken);
        if (resolution.Outcome is not CredentialResolutionOutcome.Authorized)
        {
            return Results.Ok(new SessionResponse(Authenticated: passthroughAdmits, Username: null, setupRequired));
        }

        // Re-read the row to name the operator. The authenticator deliberately does not carry a
        // username in its resolution (see DbSessionAuthenticator.SessionLabel — it would then land
        // in the gate's log line for every gated request), so this surface, which is the only one
        // that needs the name, fetches it.
        var (idleTimeout, _) = await settings.GetSessionLifetimesAsync(cancellationToken);
        var session = await sessions.FindLiveByPresentedTokenAsync(token, idleTimeout, cancellationToken);
        var user = session is null ? null : await users.FindByIdAsync(session.UserId, cancellationToken);

        return Results.Ok(new SessionResponse(
            Authenticated: user is not null,
            Username: user?.Username,
            setupRequired));
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> CreateFirstAccountAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CredentialsRequest? request,
        HttpContext httpContext,
        UserRepository users,
        SessionRepository sessions,
        SettingsRepository settings,
        CancellationToken cancellationToken)
    {
        // THE TRUSTED-NETWORK CHECK IS FIRST, AND IT IS NOT REDUNDANT WITH THE ZERO-ACCOUNTS ONE.
        // Zero-accounts alone leaves a real window: on a fresh install reachable from outside the
        // LAN, whoever posts here first owns the instance, and the operator has no way to know it
        // was not them. Requiring the caller to also be local means claiming a fresh install
        // requires access the attacker does not have. The issue names this exact scenario ("a fresh
        // install with no users must offer account creation without leaving an open window where
        // anyone on the LAN can claim the instance"); this is the narrower of the two answers.
        //
        // Trust comes from the socket peer only — never a header. See TrustedNetwork.
        if (!TrustedNetwork.IsTrusted(httpContext.Connection.RemoteIpAddress))
        {
            return Results.Problem(
                title: "First-run setup is not available from this address",
                detail: "The first account can only be created from a machine on the local network.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with 'username' and 'password' properties is required." });
        }

        try
        {
            var created = await users.CreateFirstUserAsync(
                request.Username ?? string.Empty,
                request.Password ?? string.Empty,
                cancellationToken);

            if (created is null)
            {
                // An account already exists — either it did before this call, or a concurrent
                // request won the race (see UserRepository.CreateFirstUserAsync, which cannot tell
                // those apart and deliberately does not try). 409 rather than 403: the caller was
                // permitted to ask, the instance is simply already claimed. The message says
                // nothing about who owns it.
                return Results.Problem(
                    title: "This instance already has an account",
                    detail: "First-run setup is only available before the first account exists. Sign in instead.",
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Signed in immediately, so the operator who just created the account is not asked to
            // type the password again into a form they have not seen yet.
            var (_, absoluteLifetime) = await settings.GetSessionLifetimesAsync(cancellationToken);
            var issued = await sessions.IssueAsync(created.Id, absoluteLifetime, cancellationToken);
            SessionCookie.Attach(httpContext.Response, issued.PlaintextToken);

            return Results.Created(SessionRoute, new SessionResponse(
                Authenticated: true,
                Username: created.Username,
                SetupRequired: false));
        }
        catch (UserValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> LoginAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CredentialsRequest? request,
        HttpContext httpContext,
        UserRepository users,
        SessionRepository sessions,
        SettingsRepository settings,
        LoginRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with 'username' and 'password' properties is required." });
        }

        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString();

        // Checked BEFORE verification, so a throttled attempt costs no KDF work. Doing it after
        // would bound guessing but leave the CPU-exhaustion path open, since the expensive part
        // would already have run.
        if (!rateLimiter.IsAllowed(request.Username, remoteAddress))
        {
            return Results.Problem(
                title: "Too many sign-in attempts",
                detail: $"Too many failed sign-in attempts. Try again in up to {LoginRateLimiter.Window.TotalMinutes:0} minutes.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var user = await users.VerifyCredentialsAsync(request.Username, request.Password, cancellationToken);

        if (user is null)
        {
            rateLimiter.RecordFailure(request.Username, remoteAddress);

            // ONE MESSAGE FOR BOTH FAILURES — an unknown username and a wrong password are
            // indistinguishable here, as they already are in VerifyCredentialsAsync (which also
            // equalises their timing). Naming which half was wrong turns this route into a username
            // oracle, and is worth more to an attacker than it is to an operator who mistyped.
            return Results.Problem(
                title: "Sign-in failed",
                detail: "The username or password is incorrect.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        rateLimiter.RecordSuccess(request.Username);

        var (_, absoluteLifetime) = await settings.GetSessionLifetimesAsync(cancellationToken);
        var issued = await sessions.IssueAsync(user.Id, absoluteLifetime, cancellationToken);
        SessionCookie.Attach(httpContext.Response, issued.PlaintextToken);

        return Results.Ok(new SessionResponse(Authenticated: true, Username: user.Username, SetupRequired: false));
    }

    /// <summary>
    /// Ends the caller's session — SERVER-SIDE, then the cookie.
    ///
    /// <para>Order matters and the revocation is the part that counts: clearing the cookie alone
    /// would be the "logout" the issue lists as a defect, since a caller who kept a copy of the
    /// token could keep using it. Always 204, whether or not a live session was found — a caller
    /// asking to be logged out and being told "you already were" has no use for the distinction,
    /// and answering differently would tell an unauthenticated prober whether a token is live.</para>
    /// </summary>
    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        SessionRepository sessions,
        CancellationToken cancellationToken)
    {
        if (httpContext.Request.Cookies.TryGetValue(ISessionAuthenticator.CookieName, out var token)
            && !string.IsNullOrEmpty(token))
        {
            await sessions.RevokeByPresentedTokenAsync(token, cancellationToken);
        }

        SessionCookie.Clear(httpContext.Response);

        return Results.NoContent();
    }

    /// <summary>
    /// #96: rotation by the signed-in operator. Requires a LIVE SESSION and nothing else; returns
    /// 204 on success.
    ///
    /// <para><b>THE ADMIN KEY IS NEVER READ HERE.</b> Not consulted, not fallen back to. A machine
    /// credential must not be able to rotate a human's password, and the property is structural
    /// rather than a check: <c>AdminApiKeyFilter</c> is the only thing in the codebase that reads
    /// <c>X-Admin-Api-Key</c>, and this route is not wrapped by it. Asserted by
    /// <c>Changing_a_password_with_only_an_admin_key_is_refused</c>.</para>
    ///
    /// <para><b>#43's BOOTSTRAP BYPASS CANNOT REACH THIS ROUTE EITHER</b>, which is worth stating
    /// because it is the one way "session only" could silently become "anyone on the LAN". That
    /// bypass lives entirely inside <c>AdminApiKeyFilter.HandleUnconfiguredAsync</c>; this route
    /// never calls the filter, and <c>DbSessionAuthenticator</c> cannot return
    /// <see cref="CredentialResolutionOutcome.NotConfigured"/> by construction. So on a fresh install
    /// with no admin key configured, this route still requires a live session — asserted by
    /// <c>Changing_a_password_requires_a_session_even_when_no_admin_key_is_configured</c>, which
    /// deliberately does NOT seed a key.</para>
    ///
    /// <para><b>THE ORDER OF THE CHECKS IS LOAD-BEARING.</b> Session first, so an unauthenticated
    /// caller learns nothing about the body schema. Rate limit next, BEFORE the body is inspected
    /// and before either KDF invocation, for the reason <c>LoginRateLimiter.IsAllowed</c> states —
    /// checking after verification would bound guessing but leave the CPU-exhaustion path open, and
    /// this route runs TWO hashes rather than one.</para>
    ///
    /// <para><b>NO <c>Set-Cookie</c> ON SUCCESS.</b> The current session is not re-issued: it is
    /// still valid, and re-issuing would be a second code path for issuing sessions on a route with
    /// no need to issue one. Asserted, because a future "refresh the cookie while we are here" would
    /// otherwise pass silently.</para>
    /// </summary>
    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> ChangePasswordAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ChangePasswordRequest? request,
        HttpContext httpContext,
        ISessionAuthenticator authenticator,
        UserRepository users,
        SessionRepository sessions,
        SettingsRepository settings,
        LoginRateLimiter rateLimiter,
        CancellationToken cancellationToken)
    {
        if (!httpContext.Request.Cookies.TryGetValue(ISessionAuthenticator.CookieName, out var token)
            || string.IsNullOrEmpty(token))
        {
            return NotSignedIn();
        }

        // The CSRF control from ADR 0008. A cookie is ambient — a browser attaches it to a
        // cross-site request automatically — so this header is what a forged request cannot supply.
        // Its absence is treated as NO credential, matching AdminApiKeyFilter's posture exactly:
        // this is a state-changing cookie-authenticated POST and needs the same control the gate
        // applies to every other one.
        if (!httpContext.Request.Headers.ContainsKey(AdminApiKeyFilter.SessionRequestHeaderName))
        {
            return NotSignedIn();
        }

        var resolution = await authenticator.AuthenticateAsync(token, ApiKeyScope.Admin, cancellationToken);
        if (resolution.Outcome is not CredentialResolutionOutcome.Authorized)
        {
            return NotSignedIn();
        }

        // The same two-step GetSessionAsync does: the authenticator says "yes, live session", and
        // this resolves WHICH one — yielding both the account to re-hash and the session id to spare
        // when the others are revoked.
        var (idleTimeout, _) = await settings.GetSessionLifetimesAsync(cancellationToken);
        var session = await sessions.FindLiveByPresentedTokenAsync(token, idleTimeout, cancellationToken);
        if (session is null)
        {
            return NotSignedIn();
        }

        var remoteAddress = httpContext.Connection.RemoteIpAddress?.ToString();

        // Keyed by USER ID, not username. The id is the stable identity, and the "u:"/"a:" prefixes
        // LoginRateLimiter applies mean a "pw:" sub-prefix cannot collide with a real username's
        // budget — so a login-guessing spree cannot consume the rotation budget of a signed-in
        // operator, or vice versa. Two different attacks, two budgets.
        //
        // BOTH AXES MUST BE NAMESPACED, NOT JUST THE USERNAME ONE. The address is the same string in
        // either flow, so without AddressScope these failures would spend the per-address LOGIN
        // budget: an operator fumbling their current password a few times could then be thrown a 429
        // on the sign-in page, and behind a shared NAT egress that is somebody else's sign-in page
        // too. Asserted in BOTH directions by
        // Password_change_failures_do_not_exhaust_the_login_address_budget and its converse.
        var limiterKey = RateLimiterKey(session.UserId);

        if (!rateLimiter.IsAllowed(limiterKey, remoteAddress, AddressScope))
        {
            // Delays, never disables — ADR 0009 unchanged. The wording is the login route's.
            return Results.Problem(
                title: "Too many password change attempts",
                detail: $"Too many failed attempts. Try again in up to {LoginRateLimiter.Window.TotalMinutes:0} minutes.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with 'currentPassword' and 'newPassword' properties is required." });
        }

        try
        {
            var result = await users.ChangePasswordAsync(
                session.UserId,
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);

            if (result is ChangePasswordResult.CurrentPasswordIncorrect)
            {
                // The SAME AddressScope the IsAllowed check above used — a mismatch would increment
                // one counter and read another, silently making the address budget unenforceable.
                rateLimiter.RecordFailure(limiterKey, remoteAddress, AddressScope);

                // ONE GENERIC MESSAGE, matching VerifyCredentialsAsync's posture. There is no
                // username oracle to protect here — the caller is already authenticated as a known
                // account — so naming WHICH field was wrong is fine; what it must not do is say
                // anything about the stored value.
                return Results.Problem(
                    title: "Password change failed",
                    detail: "The current password is incorrect.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            rateLimiter.RecordSuccess(limiterKey);

            // Every OTHER session of this account is revoked; the one that made the request is
            // spared. Revoking all of them would sign the operator out of the tab they just used,
            // which reads as the change having failed; revoking none would make a rotation prompted
            // by "I think someone else has my password" achieve nothing.
            await sessions.RevokeAllForUserExceptAsync(session.UserId, session.Id, cancellationToken);

            return Results.NoContent();
        }
        catch (UserValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// The one answer this route gives to every way of not being signed in — no cookie, no CSRF
    /// header, an unauthorized resolution, or a token whose row is no longer live.
    ///
    /// <para>One shape for all four so the response cannot be read as an oracle for which of them
    /// applied, and so a fifth arm added later cannot accidentally answer differently.</para>
    /// </summary>
    private static IResult NotSignedIn() =>
        Results.Problem(
            title: "Not signed in",
            detail: "Changing a password requires a signed-in session.",
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The <see cref="LoginRateLimiter"/> key for a password change. The "pw:" prefix keeps this
    /// budget separate from the login budget the same limiter holds under "u:" — see the call site.
    /// </summary>
    private static string RateLimiterKey(long userId) => $"pw:{userId}";

    /// <summary>
    /// The <see cref="LoginRateLimiter"/> ADDRESS-budget scope for a password change, which keeps
    /// these failures out of the login route's per-address budget. Login passes no scope, so its key
    /// is unchanged; see <c>LoginRateLimiter.AddressKey</c> for why one axis is not enough.
    /// </summary>
    private const string AddressScope = "pw";
}
