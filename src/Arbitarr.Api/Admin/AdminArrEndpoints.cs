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
/// The Sonarr instance configuration as served over the wire.
///
/// <para><b>THE TWO FIELDS ARE SHAPED DIFFERENTLY ON PURPOSE.</b> The base URL is PRESENT, like
/// <see cref="OllamaConfigResponse"/>'s and unlike a source's, because it is not a credential —
/// <c>ArrInstanceRepository.ValidateBaseUrl</c> rejects a URL carrying userinfo precisely so that
/// stays true. The API key is a BOOL, like a source's <c>hasApiKey</c> indicator and unlike the
/// Ollama section (which has no key at all), because it IS a credential and the write-only contract
/// means there is no read path for it anywhere. Rendering either one the other way would be
/// cargo-culting an idiom rather than applying it.</para>
/// </summary>
/// <param name="BaseUrl">The Sonarr base URL in force, read from the database. Null when unconfigured.</param>
/// <param name="HasApiKey">Whether a key is stored — never the key itself.</param>
public sealed record ArrConfigResponse(string? BaseUrl, bool HasApiKey);

/// <summary>
/// Request body for <c>PUT /api/admin/arr/sonarr</c>.
///
/// <para><b><paramref name="ApiKey"/> IS OPTIONAL, and null means "leave the stored key alone".</b>
/// This is the source-API-key contract exactly (see <c>SourceRepository.UpdateAsync</c> and
/// <c>NotificationRepository.SetSettingsAsync</c>): the client never had the stored value, so it
/// cannot read-and-reapply it, and an edit that changes only the address must not clear the key as
/// a side effect. An EMPTY STRING is a different thing — an explicit attempt to store a blank key —
/// and is rejected by <c>ArrInstanceRepository.ValidateApiKey</c>. There is deliberately NO
/// key-only clear: under ADR 0010 a secret is cleared only by deleting the thing that owns it, so
/// <c>DELETE /api/admin/arr/sonarr</c> unconfigures the whole instance and nothing clears the key
/// on its own.</para>
/// </summary>
public sealed record UpdateArrConfigRequest(string? BaseUrl, string? ApiKey = null);

/// <summary>The outcome of <c>POST /api/admin/arr/sonarr/test</c>.</summary>
/// <param name="Outcome">
/// One of <see cref="SourceProbeOutcome"/>, as a stable string the UI switches on. Distinct values
/// rather than a boolean because unreachable / TLS / wrong-key / not-Sonarr have different fixes.
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Outcome"/> alone. Never derived from
/// Sonarr's response, an exception message, the configured URL, or the key.
/// </param>
public sealed record ArrTestResponse(bool Success, string Outcome, string Message);

/// <summary>
/// arb-u1c: the admin-gated Sonarr surface — read, write, clear and probe the base URL and API key
/// that <c>Arbitarr.Media.Providers.ArrApiProvider</c> resolves series identities through.
/// Every route is <c>.RequireAdminApiKey()</c>, so the gate is by path prefix and never by verb:
/// the <c>GET</c> is gated exactly like the writes, because this is admin configuration rather than
/// the lite dashboard's public read surface.
///
/// <para><b>WHY ITS OWN SURFACE AND NOT A <c>SettingsCatalog</c> ENTRY.</b> Both reasons the
/// codebase already has apply here at once. The KEY is off the catalog for
/// <see cref="AdminSecurityEndpoints"/>'s reason — publishing it would leak a secret, and its
/// colon-namespaced row name is what makes that structurally impossible rather than merely
/// unintended (CLAUDE.md §1). The BASE URL is off it for <see cref="AdminAiEndpoints"/>'s reason —
/// it needs an affordance the generic catalog row cannot provide, a connectivity probe, and a
/// catalog entry would render it a second time as an unexplained text field beside the section that
/// already owns it. The rejected alternative was a dedicated table: two rows do not warrant an EF
/// migration and a store of their own when the Settings table already holds exactly this shape, and
/// a new table would need its own retention wired into <c>MaintenanceJob</c> (see
/// <see cref="ArrInstanceRepository"/>'s type doc).</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Every body here is bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked inside the
/// handler, exactly as <see cref="AdminSourceEndpoints"/>, <see cref="AdminNotificationEndpoints"/>,
/// <see cref="AdminSecurityEndpoints"/> and <see cref="AdminAiEndpoints"/> do, and for exactly the
/// same reason: a REQUIRED body is model-bound BEFORE endpoint filters run, so a missing or
/// malformed one short-circuits to 400 without <c>AdminApiKeyFilter</c> ever executing. An
/// unauthenticated remote caller could then tell a malformed body (400) from a well-formed one
/// (503) and enumerate which admin routes exist from outside the gate. Binding optionally keeps the
/// gate strictly first. <c>AdminApiKeyRouteEnumerationTests</c> asserts this by sweeping every
/// concrete admin-mutating route sending NO body at all — that bodilessness is load-bearing and must
/// not be "fixed". All four routes here are concrete rather than templated, so that sweep covers
/// them; the by-name gating assertions in <c>AdminArrEndpointsTests</c> pin them anyway, so no route
/// is gated only by assumption.</para>
/// </summary>
public static class AdminArrEndpoints
{
    public const string SonarrRoute = "/api/admin/arr/sonarr";

    /// <summary>§3.3's test affordance.</summary>
    public const string SonarrTestRoute = $"{SonarrRoute}/test";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SonarrRoute, GetConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapPut(SonarrRoute, UpdateConfigAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete(SonarrRoute, ClearAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(SonarrTestRoute, TestAsync)
            .RequireAdminApiKey();
    }

    /// <summary>
    /// Reads the configuration. The key is reported as a BOOL and read through
    /// <see cref="ArrInstanceRepository.HasApiKeyAsync"/> — the only key-adjacent read a projection
    /// to the wire is permitted to make.
    /// </summary>
    private static async Task<IResult> GetConfigAsync(
        ArrInstanceRepository repository,
        CancellationToken cancellationToken) =>
        Results.Ok(new ArrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> UpdateConfigAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateArrConfigRequest? request,
        ArrInstanceRepository repository,
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

        return Results.Ok(new ArrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));
    }

    /// <summary>
    /// Unconfigures the Sonarr instance: forgets BOTH the address and the key. Takes no body — the
    /// route names the whole action — so there is no model binding to run ahead of the admin filter.
    ///
    /// <para><b>THE WHOLE INSTANCE, NOT THE KEY ALONE</b> (ADR 0010; bead arb-c26, ratified
    /// 2026-09-08). The shared rule is "secrets are never readable, and omission never clears", and
    /// a secret is cleared only by deleting the thing that OWNS it —
    /// <see cref="AdminSourceEndpoints"/>'s source delete removes that source's key with it, and
    /// <see cref="AdminNotificationEndpoints"/>'s bodiless webhook DELETE is the rule's completion
    /// for a singleton with no owning row. This route is that same shape for this singleton.</para>
    ///
    /// <para>A key-only clear was written first and removed: it leaves an address with no
    /// credential, which is precisely the half-configured state the Sources surface never offers,
    /// and adding one here would have pre-empted a decision that belongs to every secret class at
    /// once rather than to whichever feature happened to land next.</para>
    /// </summary>
    private static async Task<IResult> ClearAsync(
        ArrInstanceRepository repository,
        CancellationToken cancellationToken)
    {
        await repository.ClearAsync(cancellationToken);

        // Read back rather than assumed: the response states what is actually stored now, so a
        // caller never has to infer the post-delete shape from the fact that the call succeeded.
        return Results.Ok(new ArrConfigResponse(
            await repository.GetBaseUrlAsync(cancellationToken),
            await repository.HasApiKeyAsync(cancellationToken)));
    }

    /// <summary>
    /// The connectivity probe. Takes no body at all — the address and key under test are the STORED
    /// ones, read server-side, so there is nothing for a caller to submit. That is also why this
    /// route needs no optional-body treatment: with no body parameter there is no model binding to
    /// run ahead of the admin filter.
    ///
    /// <para>Probing the stored values rather than submitted ones is deliberate, exactly as
    /// <see cref="AdminAiEndpoints"/>'s probe is: the question an operator is asking is "does what I
    /// saved work", and a probe against a body-supplied URL would answer a different question and
    /// turn this route into an authenticated request-forwarder.</para>
    ///
    /// <para>This is the one place <c>ReadApiKeyForUpstreamRequestAsync</c> is called from, and the
    /// value goes straight into the outbound probe. It is never returned: the response carries a
    /// closed enum and wording derived from that enum alone.</para>
    /// </summary>
    private static async Task<IResult> TestAsync(
        ArrInstanceRepository repository,
        SonarrConnectivityProber prober,
        CancellationToken cancellationToken)
    {
        var baseUrl = await repository.GetBaseUrlAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Results.BadRequest(new { error = "No Sonarr base URL is configured." });
        }

        var apiKey = await repository.ReadApiKeyForUpstreamRequestAsync(cancellationToken);
        var outcome = await prober.ProbeAsync(baseUrl, apiKey, cancellationToken);

        return Results.Ok(new ArrTestResponse(
            Success: outcome == SourceProbeOutcome.Ok,
            Outcome: outcome.ToString(),
            Message: DescribeOutcome(outcome)));
    }

    /// <summary>
    /// Fixed wording per outcome. Derived from the closed enum alone and never from Sonarr's
    /// response, an exception, the configured URL, or the key — which is what keeps the five
    /// outcomes reportable without any of them being able to carry credential-derived text.
    /// </summary>
    private static string DescribeOutcome(SourceProbeOutcome outcome) => outcome switch
    {
        SourceProbeOutcome.Ok =>
            "Connected successfully and the API key was accepted.",
        SourceProbeOutcome.Unreachable =>
            "Could not reach Sonarr: no response from that address before the timeout. Check the base URL, the port, and that Sonarr is running.",
        SourceProbeOutcome.TlsFailure =>
            "Reached Sonarr but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if Sonarr is not serving TLS on that port.",
        SourceProbeOutcome.AuthenticationFailed =>
            "Sonarr is reachable but rejected the API key. Check the key in Sonarr under Settings > General.",
        SourceProbeOutcome.UnexpectedResponse =>
            "Something answered but it was not Sonarr. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than Sonarr itself.",
        _ => "The connectivity test did not complete.",
    };
}
