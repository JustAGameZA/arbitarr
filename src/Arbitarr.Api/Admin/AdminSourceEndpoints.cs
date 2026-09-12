using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// One source as served over the wire. Note what is absent: there is no field for the API key, and
/// deliberately no nullable "key" property that a future edit could start populating. Presence is
/// reported by <paramref name="HasApiKey"/> and nothing else (§3.1/AC2).
/// </summary>
public sealed record SourceResponse(
    long Id,
    string Kind,
    string DisplayName,
    string BaseUrl,
    bool Enabled,
    bool HasApiKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Request body for <c>POST /api/admin/sources</c>. <paramref name="ApiKey"/> is write-only and optional.</summary>
public sealed record CreateSourceRequest(
    string? Kind,
    string? DisplayName,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled);

/// <summary>
/// Request body for <c>PUT /api/admin/sources/{id}</c>. A null <paramref name="ApiKey"/> leaves the
/// stored key untouched; a non-empty one REPLACES it. There is deliberately no way to express
/// "give me back what is stored" — the write-only contract means the client never had it.
/// </summary>
public sealed record UpdateSourceRequest(
    string? Kind,
    string? DisplayName,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled);

/// <summary>The outcome of <c>POST /api/admin/sources/{id}/test</c>, per §3.3.</summary>
/// <param name="Outcome">
/// One of <see cref="SourceProbeOutcome"/>, as a stable string the UI switches on. Distinct values
/// rather than a boolean because unreachable / TLS / auth / unexpected-shape have entirely
/// different fixes (AC4).
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Outcome"/> alone. It is never derived
/// from the upstream's response, an exception message, or the submitted key.
/// </param>
public sealed record SourceTestResponse(bool Success, string Outcome, string Message);

/// <summary>
/// #53 stage 53c: the admin-gated CRUD surface for sources, plus the §3.3 connectivity test.
/// Every route is <c>.RequireAdminApiKey()</c> — the gate is by path prefix, never by verb, so the
/// read (<c>GET</c>) is gated exactly like the writes: this is admin configuration, not the lite
/// dashboard's public read surface.
///
/// <para><b>THE SECRET RULE, and how this file keeps it.</b> Source API keys live as write-only
/// rows in the Settings table under <c>source:{id}:api_key</c> — a colon-namespaced name that no
/// <c>SettingKey</c> enum value can produce, which is why those rows can never surface on
/// <c>GET /api/admin/settings</c> (that endpoint projects from <c>SettingsCatalog.Entries</c>, never
/// from the table). This file preserves that property structurally rather than by care:
/// <see cref="SourceResponse"/> has no field capable of carrying a key,
/// <see cref="ToResponseAsync"/> is the single projection every read path goes through, and it
/// sources its indicator from <see cref="SourceRepository.HasApiKeyAsync"/>, which returns a bool.
/// The write paths accept a key and return 200/201 carrying only that same projection. No response
/// path in this file can emit a key, which is the same trap #43 had to avoid.</para>
///
/// <para><b>THE REQUIRED-BODY TRAP.</b> Every body on these routes is bound OPTIONALLY
/// (<c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c>) and null-checked inside the
/// handler, exactly as <see cref="AdminSecurityEndpoints"/> does and for exactly the same reason: a
/// REQUIRED body is model-bound BEFORE endpoint filters run, so a missing or malformed one
/// short-circuits to 400 without <see cref="AdminApiKeyFilter"/> ever executing. An unauthenticated
/// remote caller could then tell a malformed body (400) from a well-formed one (503) and enumerate
/// which admin routes exist from outside the gate. Binding optionally keeps the gate strictly
/// first, so an unauthorised caller learns only that they are unauthorised. This is asserted by
/// <c>AdminApiKeyRouteEnumerationTests</c>, which sweeps every concrete admin-mutating route
/// sending NO body at all — that bodilessness is load-bearing and must not be "fixed".</para>
///
/// <para><b>Validation is not duplicated here.</b> <see cref="SourceRepository"/> already rejects
/// non-absolute/non-http(s) URLs, empty or colliding display names, and kinds that are empty or
/// outside <see cref="SourceRepository.KnownKinds"/> — which, because the comparison is ordinal,
/// includes a merely wrongly-cased <c>"nzbhydra"</c> — with the AC24 reject-never-clamp posture.
/// This layer only translates <see cref="SourceValidationException"/> into a 400 — one validation
/// floor, in one place, already tested.</para>
/// </summary>
public static class AdminSourceEndpoints
{
    public const string SourcesRoute = "/api/admin/sources";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SourcesRoute, GetSourcesAsync)
            .RequireAdminApiKey();

        endpoints.MapPost(SourcesRoute, CreateSourceAsync)
            .RequireAdminApiKey();

        endpoints.MapPut($"{SourcesRoute}/{{id:long}}", UpdateSourceAsync)
            .RequireAdminApiKey();

        endpoints.MapDelete($"{SourcesRoute}/{{id:long}}", DeleteSourceAsync)
            .RequireAdminApiKey();

        endpoints.MapPost($"{SourcesRoute}/{{id:long}}/test", TestSourceAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetSourcesAsync(
        SourceRepository repository,
        CancellationToken cancellationToken)
    {
        var sources = await repository.GetAllAsync(cancellationToken);

        var responses = new List<SourceResponse>(sources.Count);
        foreach (var source in sources)
        {
            responses.Add(await ToResponseAsync(source, repository, cancellationToken));
        }

        return Results.Ok(responses);
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> CreateSourceAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateSourceRequest? request,
        SourceRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with 'kind', 'displayName' and 'baseUrl' properties is required." });
        }

        try
        {
            // Nulls are passed through as empty strings rather than rejected here, so the
            // repository's own "must not be empty" rejection produces the message — one floor.
            var source = await repository.AddAsync(
                request.Kind ?? string.Empty,
                request.DisplayName ?? string.Empty,
                request.BaseUrl ?? string.Empty,
                request.ApiKey,
                request.Enabled ?? true,
                cancellationToken);

            var response = await ToResponseAsync(source, repository, cancellationToken);
            return Results.Created($"{SourcesRoute}/{source.Id}", response);
        }
        catch (SourceValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> UpdateSourceAsync(
        long id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateSourceRequest? request,
        SourceRepository repository,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "A request body with 'kind', 'displayName' and 'baseUrl' properties is required." });
        }

        var existing = await repository.GetAsync(id, cancellationToken);
        if (existing is null)
        {
            return Results.NotFound(new { error = $"Source {id} does not exist." });
        }

        try
        {
            var source = await repository.UpdateAsync(
                id,
                request.Kind ?? string.Empty,
                request.DisplayName ?? string.Empty,
                request.BaseUrl ?? string.Empty,
                // Null leaves the stored key alone; a value replaces it. There is no "clear the key"
                // verb here deliberately — deleting the source removes it, and a half-configured
                // source with a base URL but a deliberately-blanked key is not a state the UI offers.
                request.ApiKey,
                request.Enabled ?? existing.Enabled,
                cancellationToken);

            return Results.Ok(await ToResponseAsync(source, repository, cancellationToken));
        }
        catch (SourceValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteSourceAsync(
        long id,
        SourceRepository repository,
        CancellationToken cancellationToken)
    {
        var deleted = await repository.DeleteAsync(id, cancellationToken);

        return deleted
            ? Results.NoContent()
            : Results.NotFound(new { error = $"Source {id} does not exist." });
    }

    /// <summary>
    /// §3.3's connectivity test. Takes no body at all — the source under test is identified by the
    /// route id and its configuration is read from the database, so there is nothing for a caller
    /// to submit. That is also why this route needs no optional-body treatment: with no body
    /// parameter there is no model binding to run ahead of the admin filter.
    ///
    /// The stored key reaches the prober through <see cref="SourceCredentialProvider"/>, which since
    /// arb-x7w8.3 is the sole reader of <c>SourceRepository.ReadApiKeyForUpstreamRequestAsync</c>
    /// (ADR 0018) — this endpoint no longer reads it, and must not go back to doing so when the
    /// search path becomes the provider's second consumer. It is never put into the response:
    /// <see cref="SourceProbeOutcome"/> is a closed enum and <see cref="DescribeOutcome"/> maps it
    /// to fixed wording, so no branch can interpolate the key, the upstream body, or an exception
    /// message into what the operator sees. The credential is unpacked into the prober's existing
    /// <c>(baseUrl, apiKey)</c> parameters rather than handed over whole, so no probe signature
    /// gains a field that could carry key-derived text back out.
    /// </summary>
    private static async Task<IResult> TestSourceAsync(
        long id,
        SourceRepository repository,
        SourceCredentialProvider credentials,
        SourceConnectivityProber prober,
        CancellationToken cancellationToken)
    {
        var source = await repository.GetAsync(id, cancellationToken);
        if (source is null)
        {
            return Results.NotFound(new { error = $"Source {id} does not exist." });
        }

        // A source with no key stored yields no credential, and that is still worth probing: the
        // operator pressing Test on a half-configured source wants to learn whether the ADDRESS is
        // right, and an unauthenticated caps request answers that (Unreachable vs
        // AuthenticationFailed vs UnexpectedResponse) where refusing to probe would answer nothing.
        var credential = await credentials.GetAsync(id, cancellationToken);
        var outcome = await prober.ProbeAsync(
            credential?.BaseUrl ?? source.BaseUrl,
            credential?.ApiKey,
            cancellationToken);

        return Results.Ok(new SourceTestResponse(
            Success: outcome == SourceProbeOutcome.Ok,
            Outcome: outcome.ToString(),
            Message: DescribeOutcome(outcome)));
    }

    /// <summary>
    /// Fixed operator-facing wording per outcome. Each says what to check next, which is the whole
    /// reason §3.3 demands five distinct outcomes instead of one red "failed". Sourced only from
    /// the enum — never from the upstream response or the key.
    /// </summary>
    private static string DescribeOutcome(SourceProbeOutcome outcome) => outcome switch
    {
        SourceProbeOutcome.Ok =>
            "Connected successfully and the API key was accepted.",
        SourceProbeOutcome.Unreachable =>
            "Could not reach the source: no response from that address before the timeout. Check the base URL, the port, and that the service is running.",
        SourceProbeOutcome.TlsFailure =>
            "Reached the source but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if the service is not serving TLS on that port.",
        SourceProbeOutcome.AuthenticationFailed =>
            "The source is reachable but rejected the API key. Check the key and that it has permission on this source.",
        SourceProbeOutcome.UnexpectedResponse =>
            "The source answered but not with the API expected. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than the source itself.",
        _ => "The connectivity test did not complete.",
    };

    /// <summary>
    /// The SINGLE projection from entity to wire. Every read path goes through here, so the
    /// "no key ever leaves" property is enforced in one place: the key is represented only by the
    /// boolean from <see cref="SourceRepository.HasApiKeyAsync"/>, and
    /// <see cref="SourceResponse"/> has no field that could hold the value even if a future caller
    /// tried.
    /// </summary>
    private static async Task<SourceResponse> ToResponseAsync(
        Source source,
        SourceRepository repository,
        CancellationToken cancellationToken) =>
        new(
            Id: source.Id,
            Kind: source.Kind,
            DisplayName: source.DisplayName,
            BaseUrl: source.BaseUrl,
            Enabled: source.Enabled,
            HasApiKey: await repository.HasApiKeyAsync(source.Id, cancellationToken),
            CreatedAt: source.CreatedAt,
            UpdatedAt: source.UpdatedAt);
}
