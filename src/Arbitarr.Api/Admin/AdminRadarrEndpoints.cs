using Arbitarr.Core.Media;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// The Radarr instance configuration as served over the wire.
///
/// <para><b>THE TWO FIELDS ARE SHAPED DIFFERENTLY ON PURPOSE</b>, exactly as
/// <see cref="ArrConfigResponse"/>'s are. The base URL is PRESENT because it is not a credential —
/// <see cref="RadarrInstanceRepository.ValidateBaseUrl"/> rejects a URL carrying userinfo precisely
/// so that stays true. The API key is a BOOL because it IS a credential and the write-only contract
/// means there is no read path for it anywhere. Rendering either one the other way would be
/// cargo-culting an idiom rather than applying it.</para>
///
/// <para>A separate record from <see cref="ArrConfigResponse"/> rather than a shared one, matching
/// the duplicate-don't-generalise decision recorded on <see cref="RadarrInstanceRepository"/>
/// (arb-arrq D3): the two wire contracts are free to diverge, and sharing the type would couple
/// them for no gain beyond four saved lines.</para>
/// </summary>
/// <param name="BaseUrl">The Radarr base URL in force, read from the database. Null when unconfigured.</param>
/// <param name="HasApiKey">Whether a key is stored — never the key itself.</param>
public sealed record RadarrConfigResponse(string? BaseUrl, bool HasApiKey);

/// <summary>
/// Request body for <c>PUT /api/admin/arr/radarr</c>.
///
/// <para><b><paramref name="ApiKey"/> IS OPTIONAL, and null means "leave the stored key alone".</b>
/// This is the source-API-key contract exactly (see <c>SourceRepository.UpdateAsync</c> and
/// <see cref="UpdateArrConfigRequest"/>): the client never had the stored value, so it cannot
/// read-and-reapply it, and an edit that changes only the address must not clear the key as a side
/// effect. An EMPTY STRING is a different thing — an explicit attempt to store a blank key — and is
/// rejected by <see cref="RadarrInstanceRepository.ValidateApiKey"/>. There is deliberately NO
/// key-only clear: under ADR 0010 a secret is cleared only by deleting the thing that owns it, so
/// <c>DELETE /api/admin/arr/radarr</c> unconfigures the whole instance and nothing clears the key on
/// its own.</para>
/// </summary>
public sealed record UpdateRadarrConfigRequest(string? BaseUrl, string? ApiKey = null);

/// <summary>The outcome of <c>POST /api/admin/arr/radarr/test</c>.</summary>
/// <param name="Outcome">
/// One of <see cref="SourceProbeOutcome"/>, as a stable string the UI switches on. Distinct values
/// rather than a boolean because unreachable / TLS / wrong-key / not-Radarr have different fixes.
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Outcome"/> alone. Never derived from
/// Radarr's response, an exception message, the configured URL, or the key.
/// </param>
public sealed record RadarrTestResponse(bool Success, string Outcome, string Message);

/// <summary>
/// arb-6l9b.1: the admin-gated Radarr surface — read, write, clear and probe the base URL and API key
/// the movie library and queue readers (arb-arrq beads 3 and 4) will authenticate with. Every route
/// is <c>.RequireAdminApiKey()</c>, so the gate is by PATH PREFIX and never by verb: the <c>GET</c>
/// is gated exactly like the writes, because this is admin configuration rather than the lite
/// dashboard's public read surface.
///
/// <para><b>THIS MIRRORS <see cref="AdminArrEndpoints"/> RATHER THAN SHARING WITH IT</b>, per
/// arb-arrq D3 — the rejected alternative (one generic *arr surface parameterised by instance kind)
/// and the grounds for rejecting it are recorded on <see cref="RadarrInstanceRepository"/>. Radarr is
/// deliberately OUT of identity resolution (D4): <c>ArrApiProvider</c> stays Sonarr-only, and a
/// Radarr write does not bump <c>IArrInstanceEpoch</c>.</para>
///
/// <para><b>WHY ITS OWN SURFACE AND NOT A <c>SettingsCatalog</c> ENTRY.</b> Both reasons the codebase
/// already has apply here at once. The KEY is off the catalog because publishing it would leak a
/// secret, and its colon-namespaced row name is what makes that structurally impossible rather than
/// merely unintended (CLAUDE.md §1) — NEVER add either row to <c>SettingsCatalog</c>. The BASE URL is
/// off it because it needs an affordance the generic catalog row cannot provide, a connectivity
/// probe, and a catalog entry would render it a second time as an unexplained text field beside the
/// section that already owns it. The rejected alternative was a dedicated table: two rows do not
/// warrant an EF migration and a store of their own when the Settings table already holds exactly
/// this shape, and a new table would need its own retention wired into <c>MaintenanceJob</c> (see
/// <see cref="RadarrInstanceRepository"/>'s type doc).</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Every body here is bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked inside the
/// handler, exactly as <see cref="AdminArrEndpoints"/>, <see cref="AdminSourceEndpoints"/>,
/// <see cref="AdminNotificationEndpoints"/>, <see cref="AdminSecurityEndpoints"/> and
/// <see cref="AdminAiEndpoints"/> do, and for exactly the same reason: a REQUIRED body is model-bound
/// BEFORE endpoint filters run, so a missing or malformed one short-circuits to 400 without
/// <c>AdminApiKeyFilter</c> ever executing. An unauthenticated remote caller could then tell a
/// malformed body (400) from a well-formed one (503) and enumerate which admin routes exist from
/// outside the gate. Binding optionally keeps the gate strictly first.
/// <c>AdminApiKeyRouteEnumerationTests</c> asserts this by sweeping every concrete admin-mutating
/// route sending NO body at all — that bodilessness is load-bearing and must not be "fixed". All four
/// routes here are concrete rather than templated, so that sweep covers them; the by-name gating
/// assertions in <c>AdminRadarrEndpointsTests</c> pin them anyway, so no route is gated only by
/// assumption.</para>
/// </summary>
public static class AdminRadarrEndpoints
{
    public const string RadarrRoute = "/api/admin/arr/radarr";

    /// <summary>The connectivity test affordance, mirroring <see cref="AdminArrEndpoints.SonarrTestRoute"/>.</summary>
    public const string RadarrTestRoute = $"{RadarrRoute}/test";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(RadarrRoute, GetConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapPut(RadarrRoute, UpdateConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete(RadarrRoute, ClearAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(RadarrTestRoute, TestAsync)
            .RequireAdminApiKey();
    }

    /// <summary>
    /// Reads the configuration. The key is reported as a BOOL and read through
    /// <see cref="RadarrInstanceRepository.HasApiKeyAsync"/> — the only key-adjacent read a
    /// projection to the wire is permitted to make.
    /// </summary>
    private static async Task<IResult> GetConfigAsync(
        RadarrInstanceRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(new RadarrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> UpdateConfigAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateRadarrConfigRequest? request,
        RadarrInstanceRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with a 'baseUrl' property is required." });
        }

        try
        {
            // Validation lives in the repository, reached through its own SetAsync — the same
            // reject-never-clamp floor every other write path takes, and it validates BOTH values
            // before writing EITHER, so a rejected key cannot leave a saved address behind it.
            // A null baseUrl is passed through as an empty string so the validator produces the
            // message, rather than this layer inventing a second wording for the same rejection.
            await repository.SetAsync(request.BaseUrl ?? string.Empty, request.ApiKey, cancellationToken);
        }
        catch (ArrInstanceValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        return Results.Ok(new RadarrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));
    }

    /// <summary>
    /// Unconfigures the Radarr instance: forgets BOTH the address and the key. Takes no body — the
    /// route names the whole action — so there is no model binding to run ahead of the admin filter.
    ///
    /// <para><b>THE WHOLE INSTANCE, NOT THE KEY ALONE</b> (ADR 0010; bead arb-c26, ratified
    /// 2026-09-08). The shared rule is "secrets are never readable, and omission never clears", and a
    /// secret is cleared only by deleting the thing that OWNS it —
    /// <see cref="AdminSourceEndpoints"/>'s source delete removes that source's key with it, and
    /// <see cref="AdminNotificationEndpoints"/>'s bodiless webhook DELETE is the rule's completion for
    /// a singleton with no owning row. This route is that same shape for this singleton, and
    /// <see cref="AdminArrEndpoints"/>'s Sonarr delete is its twin.</para>
    ///
    /// <para>A key-only clear is deliberately absent: it leaves an address with no credential, which
    /// is precisely the half-configured state the Sources surface never offers, and adding one here
    /// would pre-empt a decision that belongs to every secret class at once rather than to whichever
    /// feature happened to land next.</para>
    /// </summary>
    private static async Task<IResult> ClearAsync(
        RadarrInstanceRepository repository,
        CancellationToken cancellationToken)
    {
        await repository.ClearAsync(cancellationToken);

        // Read back rather than assumed: the response states what is actually stored now, so a
        // caller never has to infer the post-delete shape from the fact that the call succeeded.
        return Results.Ok(new RadarrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));
    }

    /// <summary>
    /// The connectivity probe. Takes no body at all — the address and key under test are the STORED
    /// ones, read server-side, so there is nothing for a caller to submit. That is also why this route
    /// needs no optional-body treatment: with no body parameter there is no model binding to run ahead
    /// of the admin filter.
    ///
    /// <para>Probing the stored values rather than submitted ones is deliberate: the question an
    /// operator is asking is "does what I saved work", and a probe against a body-supplied URL would
    /// answer a different question and turn this route into an authenticated request-forwarder.</para>
    ///
    /// <para><b>THE KEY IS NOT READ HERE.</b> This route obtains the credential from
    /// <see cref="RadarrCredentialProvider"/>, which is the single production caller of
    /// <see cref="RadarrInstanceRepository.ReadApiKeyForUpstreamRequestAsync"/> — the same call-site
    /// count Sonarr's reader is held to (CLAUDE.md §1), and the reason the library and queue readers
    /// that follow take a credential from the provider rather than reaching for the repository. The
    /// value goes straight into the outbound probe and is never returned: the response carries a
    /// closed enum and wording derived from that enum alone.</para>
    ///
    /// <para>A null credential covers both "no address" and "address with no key". They are reported
    /// apart because the operator's next action differs: nothing is configured at all versus a
    /// half-configured instance whose probe would be answered 401 by a Radarr that is not actually
    /// broken.</para>
    /// </summary>
    private static async Task<IResult> TestAsync(
        RadarrInstanceRepository repository,
        RadarrCredentialProvider credentials,
        RadarrConnectivityProber prober,
        CancellationToken cancellationToken)
    {
        var baseUrl = await repository.GetBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Results.BadRequest(new { error = "No Radarr base URL is configured." });
        }

        var credential = await credentials.GetAsync(cancellationToken);
        if (credential is null)
        {
            return Results.BadRequest(new { error = "No Radarr API key is configured." });
        }

        var outcome = await prober.ProbeAsync(credential.BaseUrl.ToString(), credential.ApiKey, cancellationToken);

        return Results.Ok(new RadarrTestResponse(
            Success: outcome == SourceProbeOutcome.Ok,
            Outcome: outcome.ToString(),
            Message: DescribeOutcome(outcome)));
    }

    /// <summary>
    /// Fixed wording per outcome. Derived from the closed enum alone and never from Radarr's
    /// response, an exception, the configured URL, or the key — which is what keeps the five outcomes
    /// reportable without any of them being able to carry credential-derived text.
    /// </summary>
    private static string DescribeOutcome(SourceProbeOutcome outcome) => outcome switch
    {
        SourceProbeOutcome.Ok =>
            "Connected successfully and the API key was accepted.",
        SourceProbeOutcome.Unreachable =>
            "Could not reach Radarr: no response from that address before the timeout. Check the base URL, the port, and that Radarr is running.",
        SourceProbeOutcome.TlsFailure =>
            "Reached Radarr but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if Radarr is not serving TLS on that port.",
        SourceProbeOutcome.AuthenticationFailed =>
            "Radarr is reachable but rejected the API key. Check the key in Radarr under Settings > General.",
        SourceProbeOutcome.UnexpectedResponse =>
            "Something answered but it was not Radarr. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than Radarr itself.",
        _ => "The connectivity test did not complete.",
    };
}
