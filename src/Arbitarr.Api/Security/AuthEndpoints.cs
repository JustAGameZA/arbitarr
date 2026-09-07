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
/// </list>
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
/// database from a #56 backup, or deletes the rows from the users table through the config bind
/// mount. That is stated in README.md and in the login surface's own help text, because an
/// undocumented "none" reads as an oversight rather than a decision.</para>
/// </summary>
public static class AuthEndpoints
{
    public const string SetupRoute = "/api/auth/setup";
    public const string LoginRoute = "/api/auth/login";
    public const string LogoutRoute = "/api/auth/logout";
    public const string SessionRoute = "/api/auth/session";

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
        CancellationToken cancellationToken)
    {
        var setupRequired = !await users.AnyUserAsync(cancellationToken);

        if (!httpContext.Request.Cookies.TryGetValue(ISessionAuthenticator.CookieName, out var token)
            || string.IsNullOrEmpty(token))
        {
            return Results.Ok(new SessionResponse(Authenticated: false, Username: null, setupRequired));
        }

        var resolution = await authenticator.AuthenticateAsync(token, ApiKeyScope.Admin, cancellationToken);
        if (resolution.Outcome is not AdminKeyResolutionOutcome.Authorized)
        {
            return Results.Ok(new SessionResponse(Authenticated: false, Username: null, setupRequired));
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
}
