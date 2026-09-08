using Arbitarr.Core.Ai;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// The AI backend configuration as served over the wire.
///
/// <para><b>THE BASE URL IS PRESENT, DELIBERATELY.</b> Every other configuration surface in this
/// namespace reports a stored address as a bare <c>Has…</c> boolean, because for a source or a
/// webhook the value is a credential. This one is not: Ollama has no authentication, the URL
/// carries no token, and the write path rejects a URL with userinfo in it
/// (<see cref="SettingsValidator.ValidateOllamaBaseUrl"/>) precisely so that stays true. Making the
/// operator retype an address they cannot see, to defend a secret that does not exist, would be
/// cargo-culting the write-only idiom rather than applying it.</para>
///
/// <para><b>THE MODEL IS PRESENT FOR THE SAME REASON (#112).</b> A model name is not a credential
/// either — it is the name of a file the operator pulled — and the whole point of #112 is that the
/// value stops being invisible. Before it, the model lived only in <c>OllamaOptions</c> and an
/// operator could get a green "Connected successfully" from an instance that had never pulled it.</para>
/// </summary>
/// <param name="BaseUrl">The Ollama base URL in force, read from the database.</param>
/// <param name="Model">The Ollama model in force, read from the database (#112).</param>
public sealed record OllamaConfigResponse(string BaseUrl, string Model);

/// <summary>
/// Request body for <c>PUT /api/admin/ai/ollama</c>.
///
/// <para><b><paramref name="Model"/> IS OPTIONAL, and that is a compatibility guarantee rather than
/// laxness.</b> #89 shipped this route taking <c>baseUrl</c> alone, so a client that still sends
/// only that must keep working and must NOT have its stored model cleared as a side effect of
/// saving an address. A null model therefore means "leave the model alone", which is why the
/// handler branches on null rather than passing an empty string through to the validator the way
/// the base URL does. An EMPTY STRING is a different thing — an explicit attempt to set a blank
/// model — and is rejected by <see cref="SettingsValidator.ValidateOllamaModel"/>.</para>
/// </summary>
public sealed record UpdateOllamaConfigRequest(string? BaseUrl, string? Model = null);

/// <summary>The outcome of <c>POST /api/admin/ai/ollama/test</c>.</summary>
/// <param name="Outcome">
/// One of <see cref="OllamaProbeOutcome"/>, as a stable string the UI switches on. Distinct values
/// rather than a boolean because unreachable / TLS / not-Ollama have entirely different fixes.
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Outcome"/> alone. Never derived from
/// the backend's response, an exception message, or the configured URL.
/// </param>
/// <param name="Models">
/// #112: the model names the instance reported, on <see cref="OllamaProbeOutcome.Ok"/> only (and
/// possibly empty even then — an instance with nothing pulled is healthy). A SEPARATE FIELD from
/// <paramref name="Message"/> on purpose: the wording stays derived from the closed enum alone,
/// while these names are data the UI renders as choices. Never interpolated into the message.
/// </param>
public sealed record OllamaTestResponse(bool Success, string Outcome, string Message, IReadOnlyList<string> Models);

/// <summary>
/// #89: the admin-gated AI backend surface — read, write and probe the Ollama base URL.
/// Every route is <c>.RequireAdminApiKey()</c>, so the gate is by path prefix and never by verb:
/// the <c>GET</c> is gated exactly like the writes, because this is admin configuration rather than
/// the lite dashboard's public read surface.
///
/// <para><b>WHY ITS OWN SURFACE AND NOT A <c>SettingsCatalog</c> ENTRY.</b> The value IS stored as
/// an ordinary <see cref="SettingKey"/> row and takes the same
/// <see cref="SettingsRepository.SetAsync"/> validation path every other setting takes — but it is
/// off <c>SettingsCatalog.Entries</c>, so <c>PUT /api/admin/settings/OllamaBaseUrl</c> is a 404 and
/// it does not appear in the catalog's GET projection. The reason differs from
/// <see cref="AdminSecurityEndpoints"/>'s: the admin key is off the catalog because publishing it
/// would leak a SECRET, whereas this is off the catalog because it needs an affordance the generic
/// catalog row cannot provide — a connectivity probe — and a catalog entry would additionally
/// render it as a second, unexplained text field beside the section that owns it. The rejected
/// alternative was a dedicated table: one non-secret scalar does not warrant an EF migration and a
/// store of its own when the Settings table already holds exactly this shape.</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Every body here is bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked inside the
/// handler, exactly as <see cref="AdminSourceEndpoints"/>, <see cref="AdminNotificationEndpoints"/>
/// and <see cref="AdminSecurityEndpoints"/> do, and for exactly the same reason: a REQUIRED body is
/// model-bound BEFORE endpoint filters run, so a missing or malformed one short-circuits to 400
/// without <see cref="AdminApiKeyFilter"/> ever executing. An unauthenticated remote caller could
/// then tell a malformed body (400) from a well-formed one (503) and enumerate which admin routes
/// exist from outside the gate. Binding optionally keeps the gate strictly first.
/// <c>AdminApiKeyRouteEnumerationTests</c> asserts this by sweeping every concrete admin-mutating
/// route sending NO body at all — that bodilessness is load-bearing and must not be "fixed". All
/// three routes here are concrete rather than templated, so that sweep covers them; the by-name
/// gating assertions in <c>AdminAiEndpointsTests</c> pin them anyway, so no route is gated only by
/// assumption.</para>
///
/// <para><b>NO RESTART IS REQUIRED, and this file is half of why.</b> The write invalidates
/// <see cref="OllamaBaseUrlCache"/>, so the next <c>IOllamaClient</c> use re-reads the row (see
/// <see cref="OllamaBaseUrlResolver"/> and the client's own per-call resolution). Dropping that
/// invalidation would silently restore the restart requirement while every test that only checks
/// the value was persisted still passed.</para>
/// </summary>
public static class AdminAiEndpoints
{
    public const string OllamaRoute = "/api/admin/ai/ollama";
    public const string OllamaTestRoute = "/api/admin/ai/ollama/test";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(OllamaRoute, GetOllamaConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapPut(OllamaRoute, UpdateOllamaConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(OllamaTestRoute, TestOllamaAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetOllamaConfigAsync(
        OllamaBaseUrlResolver resolver,
        OllamaModelResolver modelResolver,
        CancellationToken cancellationToken) =>
        Results.Ok(new OllamaConfigResponse(
            await resolver.GetAsync(cancellationToken),
            await modelResolver.GetAsync(cancellationToken)));

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> UpdateOllamaConfigAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateOllamaConfigRequest? request,
        SettingsRepository repository,
        OllamaBaseUrlCache cache,
        OllamaModelCache modelCache,
        OllamaBaseUrlResolver resolver,
        OllamaModelResolver modelResolver,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with a 'baseUrl' property is required." });
        }

        // #112: both values are validated BEFORE either is written, so a rejected model cannot leave
        // a saved address behind it. The alternative — write, then validate the next — would make
        // one PUT half-succeed, and the operator would have no way to tell which half from a single
        // error message. Reject-never-clamp is also reject-never-partially-apply.
        try
        {
            // Validation lives in SettingsValidator.ValidateOllamaBaseUrl, reached through the
            // repository's own switch — the same AC24 reject-never-clamp path every other setting
            // takes. Not duplicated here: one floor, in one place, already tested. A null baseUrl is
            // passed through as an empty string so the validator produces the message, rather than
            // this layer inventing a second wording for the same rejection.
            SettingsValidator.ValidateOllamaBaseUrl(request.BaseUrl ?? string.Empty);

            // A NULL model means "not submitted" and leaves the stored value alone — #89's clients
            // send only baseUrl and must not have their model cleared by saving an address. An empty
            // string is a submitted blank and is rejected below, not treated as absent.
            if (request.Model is not null)
            {
                SettingsValidator.ValidateOllamaModel(request.Model);
            }
        }
        catch (SettingsValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        // The repository re-runs the same validation on its own switch; that is not redundancy to be
        // tidied away, it is the single floor every write path goes through. The checks above only
        // decide WHETHER to start writing.
        await repository.SetAsync(SettingKey.OllamaBaseUrl, request.BaseUrl ?? string.Empty, cancellationToken);
        if (request.Model is not null)
        {
            await repository.SetAsync(SettingKey.OllamaModel, request.Model, cancellationToken);
        }

        // THE "NO RESTART" STEP. Only after a SUCCESSFUL write: invalidating on a rejected write
        // would force a pointless re-read, and invalidating before it would open a window where a
        // concurrent read repopulated the cache from the old row and cleared the stale flag, pinning
        // the previous address until the next write. Both caches, for the same reason — leaving the
        // model cache stale would pin the previous model until the next restart while the row itself
        // was already correct.
        cache.Invalidate();
        if (request.Model is not null)
        {
            modelCache.Invalidate();
        }

        return Results.Ok(new OllamaConfigResponse(
            await resolver.GetAsync(cancellationToken),
            await modelResolver.GetAsync(cancellationToken)));
    }

    /// <summary>
    /// The connectivity probe. Takes no body at all — the address under test is the STORED one, read
    /// server-side, so there is nothing for a caller to submit. That is also why this route needs no
    /// optional-body treatment: with no body parameter there is no model binding to run ahead of the
    /// admin filter.
    ///
    /// <para>Probing the stored value rather than a submitted one is deliberate: the question an
    /// operator is asking is "does the address I saved work", and a probe against a body-supplied
    /// URL would answer a different question and turn this route into an authenticated
    /// request-forwarder pointed at any address a caller names.</para>
    ///
    /// <para><see cref="OllamaProbeOutcome"/> is a closed enum and <see cref="DescribeOutcome"/>
    /// maps it to fixed wording, so no branch can interpolate the configured URL, the upstream body,
    /// or an exception message into what the operator sees.</para>
    ///
    /// <para><b>#112: the model names ride alongside, never inside.</b> The probe now returns the
    /// names it read from <c>/api/tags</c>, and they are copied straight into their own response
    /// field. <see cref="DescribeOutcome"/> is still passed nothing but the enum, so the sentence
    /// the operator reads cannot mention a model, a count, or anything else the backend said. That
    /// separation is the reason no fifth outcome and no message field were needed.</para>
    /// </summary>
    private static async Task<IResult> TestOllamaAsync(
        OllamaBaseUrlResolver resolver,
        OllamaConnectivityProber prober,
        CancellationToken cancellationToken)
    {
        var baseUrl = await resolver.GetAsync(cancellationToken);
        var result = await prober.ProbeAsync(baseUrl, cancellationToken);

        return Results.Ok(new OllamaTestResponse(
            Success: result.Outcome == OllamaProbeOutcome.Ok,
            Outcome: result.Outcome.ToString(),
            Message: DescribeOutcome(result.Outcome),
            Models: result.Models));
    }

    /// <summary>
    /// Fixed operator-facing wording per outcome. Each says what to check next, which is the whole
    /// reason the probe reports distinct outcomes instead of one red "failed". Sourced only from the
    /// enum — never from the backend's response or the configured address.
    /// </summary>
    private static string DescribeOutcome(OllamaProbeOutcome outcome) => outcome switch
    {
        OllamaProbeOutcome.Ok =>
            "Connected successfully and Ollama answered with its model list.",
        OllamaProbeOutcome.Unreachable =>
            "Could not reach Ollama: no response from that address before the timeout. Check the base URL, the port, and that Ollama is running.",
        OllamaProbeOutcome.TlsFailure =>
            "Reached the address but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if Ollama is not serving TLS on that port.",
        OllamaProbeOutcome.UnexpectedResponse =>
            "Something answered, but it was not Ollama's API. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than at Ollama itself.",
        _ => "The connectivity test did not complete.",
    };
}
