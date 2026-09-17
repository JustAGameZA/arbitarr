using Arbitarr.Core.Diagnostics;
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
/// reported by <paramref name="HasApiKey"/> and nothing else (§3.1/AC2). The arb-x7w8.1 per-indexer
/// columns added below are all non-secret tuning values; none is a string that could carry a key,
/// and <paramref name="ApiPath"/> is validated at the repository boundary to reject one.
/// </summary>
/// <param name="QueryLimit">
/// Null means UNLIMITED and is a different state from 0 — see <see cref="Arbitarr.Data.Entities.Source.QueryLimit"/>.
/// It is projected as a nullable so the wire preserves that distinction rather than collapsing it.
/// </param>
/// <param name="GrabLimit">Same nullable semantics as <paramref name="QueryLimit"/>: null is unlimited, 0 is a cap of zero.</param>
/// <param name="RuntimeState">
/// arb-x7w8.11: the derived <see cref="Arbitarr.Data.Sources.SourceRuntimeState"/> as its enum NAME —
/// healthy, budgeted, backing off, or permanently disabled.
///
/// <para><b>This is per-source RUNTIME intelligence and it lives on this ADMIN-GATED route rather
/// than on <c>/api/status</c>.</b> That route is <c>RouteClassification.PublicRead</c> and un-gated,
/// so "indexer X has spent 47 of its 50 daily queries" published there would be unratified
/// disclosure about a private deployment. <c>/api/status</c> carries exactly ONE thing from this
/// feature — the blocking health item a PERMANENTLY DISABLED indexer raises, whose summary is built
/// from the configured source name alone — and nothing else. See
/// <c>StatusEndpoint.SourcePermanentlyDisabledKey</c>.</para>
///
/// <para>An enum name and never free text, so no branch can interpolate an upstream body or a
/// credential into it — the same closure <paramref name="LastOutcome"/> relies on and the same one
/// <see cref="SourceTestResponse"/> already has.</para>
/// </param>
/// <param name="DisabledUntil">
/// When a transient hold-off expires, or null. <b>A non-null value in the PAST is normal</b> and does
/// NOT mean the source is backing off — the row retains it because the level it was reached at is
/// still live information. <paramref name="RuntimeState"/> is what says whether anything is held off.
/// </param>
/// <param name="DisabledLevel">How far transient escalation has climbed; zero means not escalated.</param>
/// <param name="LastOutcome">
/// The last observed call outcome as a <c>SourceCallOutcome</c> enum name, or null before any outcome
/// was recorded. Deliberately NOT a free-text <c>lastError</c>: this record's no-secret property is
/// structural — no field here is capable of carrying a key or any upstream text — and adding a string
/// field sourced from an upstream would trade that structure for care.
/// </param>
/// <param name="QueriesUsed">
/// Query hits spent inside the current rolling <paramref name="LimitsUnit"/> window, summing
/// RepeatCount. Read against <paramref name="QueryLimit"/>, whose null means UNLIMITED — "3 used"
/// with a null limit is unlimited, and rendering that as "3 of 0" or as a percentage is exactly the
/// collapse <see cref="Arbitarr.Data.Entities.Source.QueryLimit"/> warns about.
/// </param>
/// <param name="GrabsUsed">Grab hits spent inside the same window, read against <paramref name="GrabLimit"/> on the same terms.</param>
public sealed record SourceResponse(
    long Id,
    string Kind,
    string DisplayName,
    string BaseUrl,
    bool Enabled,
    bool HasApiKey,
    string ApiPath,
    int Priority,
    int? TimeoutSeconds,
    int? QueryLimit,
    int? GrabLimit,
    string LimitsUnit,
    string NzbAccessMode,
    string RuntimeState,
    DateTimeOffset? DisabledUntil,
    int DisabledLevel,
    string? LastOutcome,
    int QueriesUsed,
    int GrabsUsed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Request body for <c>POST /api/admin/sources</c>. <paramref name="ApiKey"/> is write-only and optional.
/// Every arb-x7w8.1 tuning field is optional; omitting one takes the entity default rather than zero.
/// </summary>
/// <param name="QueryLimit">
/// Omitted leaves the default (null, unlimited). Explicitly sending <c>null</c> also means unlimited,
/// which is NOT the same as sending <c>0</c> — see <see cref="Arbitarr.Data.Entities.Source.QueryLimit"/>.
/// </param>
public sealed record CreateSourceRequest(
    string? Kind,
    string? DisplayName,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled,
    string? ApiPath = null,
    int? Priority = null,
    int? TimeoutSeconds = null,
    int? QueryLimit = null,
    int? GrabLimit = null,
    string? LimitsUnit = null,
    string? NzbAccessMode = null)
{
    /// <summary>
    /// Names the source being created and redacts the submitted key (arb-1ox9).
    /// </summary>
    /// <remarks>
    /// <para>The synthesised <c>ToString</c> on a positional record prints every member by name and
    /// value, so the default here rendered the operator-supplied <c>ApiKey</c> verbatim. This is a
    /// REQUEST BODY on the admin write path, and neither existing scrubbing layer covers it: both the
    /// <c>IHttpClientFactory</c> URI redaction and <c>LogMessageCleanser</c> are scoped to query
    /// strings, while a bare <c>ApiKey = value</c> in a record's string form is not a URI at all
    /// (CLAUDE.md §1) — it would land verbatim in the store served at <c>/api/admin/logs</c>.</para>
    ///
    /// <para><b>Only the identifying fields are rendered, not every member.</b> The tuning fields are
    /// not secrets, but reproducing a dozen of them would make this override a second copy of the
    /// parameter list that a future field is added to twice or once. The identity of the source plus
    /// the key's redacted presence is what a diagnostic line needs; anything more belongs in a
    /// structured log argument naming the field it cares about.</para>
    /// </remarks>
    // The null arm is deliberate and must not be unified with the unconditional overrides on
    // SonarrCredential / NamedClientApiKey — see UpdateArrConfigRequest.ToString for why.
    public override string ToString() =>
        $"{nameof(CreateSourceRequest)} {{ {nameof(Kind)} = {Kind}, {nameof(DisplayName)} = {DisplayName}, "
        + $"{nameof(BaseUrl)} = {BaseUrl}, "
        + $"{nameof(ApiKey)} = {(ApiKey is null ? "null" : CredentialPatterns.Replacement)} }}";
}

/// <summary>
/// Request body for <c>PUT /api/admin/sources/{id}</c>. A null <paramref name="ApiKey"/> leaves the
/// stored key untouched; a non-empty one REPLACES it. There is deliberately no way to express
/// "give me back what is stored" — the write-only contract means the client never had it.
///
/// <para>An omitted tuning field likewise leaves the stored value alone. The three NULLABLE columns
/// cannot use that convention, because for them <c>null</c> is a real stored value rather than an
/// absence — unlimited for the two limits, "fall back to the global default" for the timeout.
/// Clearing one is therefore expressed by <paramref name="ClearQueryLimit"/>,
/// <paramref name="ClearGrabLimit"/> or <paramref name="ClearTimeoutSeconds"/>, which keeps "set
/// back to the null state" distinct from "do not touch". <paramref name="Priority"/> needs no such
/// flag: it is non-nullable and <c>0</c> is an ordinary weight, so it has no unset state to
/// express.</para>
/// </summary>
public sealed record UpdateSourceRequest(
    string? Kind,
    string? DisplayName,
    string? BaseUrl,
    string? ApiKey,
    bool? Enabled,
    string? ApiPath = null,
    int? Priority = null,
    int? TimeoutSeconds = null,
    int? QueryLimit = null,
    int? GrabLimit = null,
    string? LimitsUnit = null,
    string? NzbAccessMode = null,
    bool ClearQueryLimit = false,
    bool ClearGrabLimit = false,
    bool ClearTimeoutSeconds = false)
{
    /// <summary>
    /// Names the source being updated and redacts the submitted key (arb-1ox9). Mirrors
    /// <see cref="CreateSourceRequest.ToString"/>, whose remarks carry the full reasoning — including
    /// why only the identifying fields are rendered rather than every member.
    /// </summary>
    // The null arm is deliberate and must not be unified with the unconditional overrides on
    // SonarrCredential / NamedClientApiKey — see UpdateArrConfigRequest.ToString for why.
    public override string ToString() =>
        $"{nameof(UpdateSourceRequest)} {{ {nameof(Kind)} = {Kind}, {nameof(DisplayName)} = {DisplayName}, "
        + $"{nameof(BaseUrl)} = {BaseUrl}, "
        + $"{nameof(ApiKey)} = {(ApiKey is null ? "null" : CredentialPatterns.Replacement)} }}";
}

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
/// arb-x7w8.5: one protocol family's share of a caps refresh. Like <see cref="SourceTestResponse"/>
/// this carries no upstream-derived text — see <see cref="CapsRefreshOutcome"/> for why a failed
/// fetch is reported as a flag and never as a message.
/// </summary>
/// <param name="Protocol">The protocol family whose caps endpoint was asked, as its enum name.</param>
/// <param name="Refreshed">
/// True when the upstream answered and the stored entry was replaced. False leaves whatever was
/// stored before untouched — a failed refresh can never blank a working entry.
/// </param>
public sealed record SourceCapsRefreshEntry(string Protocol, bool Refreshed);

/// <summary>
/// arb-x7w8.5: the outcome of a caps refresh — both the explicit
/// <c>POST /api/admin/sources/{id}/caps/refresh</c> action and the fetch the create/update flow
/// performs.
/// </summary>
/// <param name="Refreshed">
/// True when at least ONE protocol family was refreshed, not an AND across the two. The families are
/// independent endpoints, and an indexer serving only one of them — a torrent-only tracker answering
/// torznab and 404ing newznab — has genuinely refreshed everything it has; reporting that as a
/// failure would train an operator to ignore the indicator. Per-family detail is in
/// <paramref name="Protocols"/> for whoever needs to see which half answered.
/// </param>
public sealed record SourceCapsRefreshResponse(bool Refreshed, IReadOnlyList<SourceCapsRefreshEntry> Protocols);

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
/// The arb-x7w8.1 columns are validated there too, by the same exact-ordinal rule: <c>limitsUnit</c>,
/// <c>nzbAccessMode</c> and <c>apiPath</c>. This layer only translates
/// <see cref="SourceValidationException"/> into a 400 — one validation floor, in one place, already
/// tested.</para>
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

        // arb-x7w8.5. Gated exactly like its siblings, by RequireAdminApiKey (which attaches
        // RouteClassification.AdminMutating and the scope alongside the filter) and never by verb.
        //
        // TEMPLATED, SO THE ENUMERATION SWEEP DOES NOT COVER IT.
        // AdminApiKeyRouteEnumerationTests skips every route containing '{' — a templated route
        // needs a real value to resolve — so this route's gating is asserted BY NAME in
        // AdminSourceEndpointsTests. The sweep passing is not evidence for this route (CLAUDE.md §2).
        endpoints.MapPost($"{SourcesRoute}/{{id:long}}/caps/refresh", RefreshSourceCapsAsync)
            .RequireAdminApiKey();
    }

    /// <summary>
    /// The list read, and since arb-x7w8.11 the only path that carries per-source RUNTIME state.
    ///
    /// <para><b>The runtime read is batched, once, before the projection loop.</b>
    /// <see cref="SourceRuntimeStateReader.GetAllAsync"/> loads every backoff row in one query and
    /// counts each source's window hits; doing it inside the loop instead would issue one backoff
    /// query per source on top of the counting that genuinely is per source.</para>
    ///
    /// <para><b>ON DEMAND, not on a poll.</b> <c>SourceApiHitCounter.CountAsync</c> filters its
    /// window in memory because EF Core cannot translate the <c>DateTimeOffset</c> comparison on
    /// SQLite, so this costs 2N event scans — bounded per source by arb-15u3's
    /// <c>(Kind, SourceDisplayName)</c> index, but real work nonetheless. It rides the sources list
    /// the operator already fetches when they open the surface, and is deliberately given no polling
    /// interval of its own.</para>
    /// </summary>
    private static async Task<IResult> GetSourcesAsync(
        SourceRepository repository,
        SourceRuntimeStateReader runtimeStates,
        CancellationToken cancellationToken)
    {
        var sources = await repository.GetAllAsync(cancellationToken);

        var runtime = await runtimeStates.GetAllAsync(sources, cancellationToken);

        var responses = new List<SourceResponse>(sources.Count);
        foreach (var source in sources)
        {
            runtime.TryGetValue(source.DisplayName, out var status);
            responses.Add(await ToResponseAsync(source, repository, cancellationToken, status));
        }

        return Results.Ok(responses);
    }

    // Body bound optionally — see the type doc's REQUIRED-BODY TRAP note. Do not make it required.
    private static async Task<IResult> CreateSourceAsync(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateSourceRequest? request,
        SourceRepository repository,
        ISourceRegistry registry,
        CapsRefresher refresher,
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
                cancellationToken,
                new SourceOptions
                {
                    ApiPath = request.ApiPath,
                    Priority = request.Priority,
                    TimeoutSeconds = request.TimeoutSeconds,
                    SetTimeoutSeconds = request.TimeoutSeconds is not null,
                    // On create, an omitted limit and an explicit null both mean "unlimited", which
                    // is the entity default anyway — so writing the value unconditionally is safe
                    // here in a way it is not on update, where an omission must not clear a stored cap.
                    QueryLimit = request.QueryLimit,
                    SetQueryLimit = true,
                    GrabLimit = request.GrabLimit,
                    SetGrabLimit = true,
                    LimitsUnit = request.LimitsUnit,
                    NzbAccessMode = request.NzbAccessMode,
                });

            // Explicitly no runtime status: a source created in this request has no backoff row and
            // no hits in the window, so the Healthy arm is the correct reading rather than an
            // omission. See ToResponseAsync.
            var response = await ToResponseAsync(source, repository, cancellationToken, runtime: null);

            // arb-x7w8.5: the add-indexer flow fetches caps in the same round trip that saved the
            // row, so a newly added indexer is searchable on its real capabilities immediately
            // rather than on the first background pass — both NZBHydra2 and Prowlarr do this.
            //
            // AFTER the write, necessarily: the registry resolves once per scope, so resolving it
            // earlier in this request would memoise a source set without the row just committed and
            // this would silently refresh nothing.
            //
            // THE RESULT IS DISCARDED HERE ON PURPOSE, and that is a scope boundary rather than an
            // oversight. The fetch's product is the STORED entry, which is what the search and caps
            // paths read; returning it would change this route's 201 body, and the add-indexer FORM
            // that would consume it is arb-x7w8.16's bead. The operator-facing per-family detail is
            // already available, unchanged, from the explicit refresh route below.
            _ = await RefreshCapsForAsync(source, registry, refresher, cancellationToken);

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
        ISourceRegistry registry,
        CapsRefresher refresher,
        ICapsCacheStore capsCache,
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
                cancellationToken,
                new SourceOptions
                {
                    ApiPath = request.ApiPath,
                    Priority = request.Priority,
                    // A value sets the column; the explicit Clear flag sets it back to null. Omitting
                    // both leaves the stored value alone — the three cases stay distinct because for
                    // each of these columns null is a real state, not an absence (see Source.cs).
                    // The timeout follows the same rule as the limits: null there means "fall back
                    // to the global default", which an operator must be able to return to.
                    TimeoutSeconds = request.ClearTimeoutSeconds ? null : request.TimeoutSeconds,
                    SetTimeoutSeconds = request.ClearTimeoutSeconds || request.TimeoutSeconds is not null,
                    QueryLimit = request.ClearQueryLimit ? null : request.QueryLimit,
                    SetQueryLimit = request.ClearQueryLimit || request.QueryLimit is not null,
                    GrabLimit = request.ClearGrabLimit ? null : request.GrabLimit,
                    SetGrabLimit = request.ClearGrabLimit || request.GrabLimit is not null,
                    LimitsUnit = request.LimitsUnit,
                    NzbAccessMode = request.NzbAccessMode,
                });

            // Explicitly no runtime status, as on create: the edit itself reports nothing about the
            // source's runtime condition, and reading it here would cost two more event scans on a
            // write path. See ToResponseAsync.
            var response = await ToResponseAsync(source, repository, cancellationToken, runtime: null);

            // A RENAME ORPHANS THE OLD NAME'S CAPS ENTRIES (architect M1, ADR 0016). The cache key is
            // the display name, so after a rename the old name's entries describe a source that no
            // longer exists under it — and the next source created or renamed to that freed name
            // would silently adopt them as its own last-known-good. Removed BEFORE the refresh
            // below, so that refresh is what re-establishes the entries, under the new name.
            //
            // Ordinal, matching the key's own comparison: the store is keyed by the exact string, so
            // a case-only rename really does move the entries to a different key and really does
            // need the old ones removed.
            if (!string.Equals(existing.DisplayName, source.DisplayName, StringComparison.Ordinal))
            {
                await capsCache.DeleteAsync(existing.DisplayName, cancellationToken);
            }

            // arb-x7w8.5, same reasoning as the create path above, and needed for the same reason it
            // is there: an edit can change the base URL, the API path or the key, any of which makes
            // the stored caps describe an endpoint this row no longer points at. Discarded here for
            // the same scope reason — the stored entry is the product.
            _ = await RefreshCapsForAsync(source, registry, refresher, cancellationToken);

            return Results.Ok(response);
        }
        catch (SourceValidationException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Deletes the row and, with it, the stored caps entries that existed only to describe it
    /// (architect M1, ADR 0016's set-membership rule).
    ///
    /// <para><b>The row is read BEFORE the delete, and only for its display name.</b> That name is
    /// the cache key, so after the delete there is nothing left to derive it from — reading it
    /// afterwards would find nothing and silently orphan both entries. The lookup is not a second
    /// existence check: the repository's delete still answers that, and is still what decides
    /// between 204 and 404.</para>
    ///
    /// <para>The caps delete runs only once the row is actually gone. Removing the entries first and
    /// then failing to delete the row would leave a live source with no last-known-good to fall back
    /// on — a working configuration made worse by a failed delete.</para>
    /// </summary>
    private static async Task<IResult> DeleteSourceAsync(
        long id,
        SourceRepository repository,
        ICapsCacheStore capsCache,
        CancellationToken cancellationToken)
    {
        var existing = await repository.GetAsync(id, cancellationToken);

        var deleted = await repository.DeleteAsync(id, cancellationToken);

        if (deleted && existing is not null)
        {
            await capsCache.DeleteAsync(existing.DisplayName, cancellationToken);
        }

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
        // The address comes from the row that was just loaded for the 404 check; the credential is
        // consulted only for the key, so there is no implied invariant that its BaseUrl and this
        // source's agree.
        var credential = await credentials.GetAsync(id, cancellationToken);
        var outcome = await prober.ProbeAsync(source.BaseUrl, credential?.ApiKey, cancellationToken);

        return Results.Ok(new SourceTestResponse(
            Success: outcome == SourceProbeOutcome.Ok,
            Outcome: outcome.ToString(),
            Message: DescribeOutcome(outcome)));
    }

    /// <summary>
    /// arb-x7w8.5's explicit per-indexer "Refresh caps" action. Takes no body — like the test route
    /// the source is identified by the route id, so there is nothing to submit and therefore no
    /// model binding that could run ahead of the admin filter.
    ///
    /// <para><b>The 404 is answered from the ROW, not from the registry.</b> A source that exists but
    /// is DISABLED resolves to no adapter, and reporting that as "source 7 does not exist" would tell
    /// an operator their indexer had been deleted. The row lookup answers existence; the registry
    /// answers reachability, and a disabled or unresolvable source refreshes nothing — reported as
    /// <c>refreshed: false</c> on both families, which is the honest answer and the same shape a
    /// reachable-but-down indexer produces.</para>
    /// </summary>
    private static async Task<IResult> RefreshSourceCapsAsync(
        long id,
        SourceRepository repository,
        ISourceRegistry registry,
        CapsRefresher refresher,
        CancellationToken cancellationToken)
    {
        var source = await repository.GetAsync(id, cancellationToken);
        if (source is null)
        {
            return Results.NotFound(new { error = $"Source {id} does not exist." });
        }

        return Results.Ok(await RefreshCapsForAsync(source, registry, refresher, cancellationToken));
    }

    /// <summary>
    /// Refreshes the stored caps for one just-written or just-identified source row, shared by the
    /// create, update and explicit-refresh paths so all three produce the same shape and the same
    /// failure posture.
    ///
    /// <para><b>Matched out of the registry by <see cref="IUpstreamSource.Name"/>, which is the row's
    /// display name.</b> The registry is the only thing that knows how to build an adapter from a row
    /// (its kind mapping, its per-source HttpClient and its off-origin guard all live there), and
    /// duplicating that construction here to get one adapter would mean a second place for those
    /// guards to be forgotten.</para>
    ///
    /// <para><b>What makes the match SAFE is within-scope stability, not uniqueness.</b> Uniqueness
    /// alone would be the wrong argument: it is a property of one instant, and the risk here is the
    /// name changing between the row being read and the registry being resolved. It cannot. Both
    /// happen inside a single request scope, the registry resolves once per scope, and the only
    /// writers of a display name are these very handlers — so within one call there is no window in
    /// which the row this refreshes could be renamed out from under the lookup.
    ///
    /// The failure mode if that ever stopped holding is closed, which is why a name match is
    /// tolerable at all: a stale or missing match finds no adapter, refreshes nothing, and reports
    /// <c>refreshed: false</c>. It cannot refresh the WRONG source's caps, because the name it
    /// searches for is the one just written for this row. The same closure covers the deliberate
    /// comparison mismatch with <c>EnsureDisplayNameIsUniqueAsync</c>, which rejects collisions
    /// case-INSENSITIVELY while this matches Ordinal: the stricter comparison can only fail to find
    /// an adapter that a looser one would have found, never find a different one.</para>
    ///
    /// <para>Re-keying this to the source id — which would remove the question rather than argue it —
    /// is tracked as <b>arb-kfe9</b>. It is not done here because the id is not currently on
    /// <see cref="IUpstreamSource"/>, so it would change the registry contract and every adapter with
    /// it, well beyond this bead.</para>
    ///
    /// <para><b>A source that resolves to no adapter refreshes nothing and does not fail.</b> That
    /// covers a disabled row, an unknown kind, and a row the registry skipped for an off-origin API
    /// path. Each is reported as not-refreshed per family rather than as an error, because the caller
    /// here is an operator saving a form: failing the save because the new indexer was unreachable
    /// would discard a correct configuration over a transient upstream.</para>
    /// </summary>
    private static async Task<SourceCapsRefreshResponse> RefreshCapsForAsync(
        Source source,
        ISourceRegistry registry,
        CapsRefresher refresher,
        CancellationToken cancellationToken)
    {
        var resolved = await registry.ResolveAsync(cancellationToken);
        var upstream = resolved.FirstOrDefault(s =>
            string.Equals(s.Name, source.DisplayName, StringComparison.Ordinal));

        var outcomes = upstream is null
            ? Array.Empty<CapsRefreshOutcome>()
            : await refresher.RefreshAsync(upstream, cancellationToken);

        return new SourceCapsRefreshResponse(
            Refreshed: outcomes.Any(o => o.Refreshed),
            Protocols: outcomes
                .Select(o => new SourceCapsRefreshEntry(o.Protocol.ToString(), o.Refreshed))
                .ToArray());
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
    ///
    /// <para>arb-x7w8.11's runtime fields are added HERE for the same reason everything else is: it
    /// is the single chokepoint, so the list, the create and the update all report the same shape
    /// without three places to keep in step. <paramref name="runtime"/> is passed IN rather than read
    /// here because the list path reads every source's state in one batch (see
    /// <see cref="SourceRuntimeStateReader.GetAllAsync"/>) and a per-source read inside this method
    /// would turn that back into N round trips.</para>
    ///
    /// <para><b>A missing status projects as HEALTHY, not as an error.</b> That is the honest reading
    /// of a source with no recorded backoff row and no hits in the window — a source that has never
    /// been called is not in trouble. The write paths take this arm deliberately: a source that was
    /// just created or edited has, by construction, nothing to report yet, and a second batch read
    /// there would cost two more event scans to produce the same answer.</para>
    ///
    /// <para><b>Required rather than defaulted</b>, so taking that Healthy arm is
    /// something a call site STATES by passing <c>null</c>. With a default a new read path picks the
    /// arm up by omission and silently reports Healthy for a source whose state it never read, which
    /// is the one failure this projection cannot detect from the inside.</para>
    /// </summary>
    private static async Task<SourceResponse> ToResponseAsync(
        Source source,
        SourceRepository repository,
        CancellationToken cancellationToken,
        SourceRuntimeStatus? runtime) =>
        new(
            Id: source.Id,
            Kind: source.Kind,
            DisplayName: source.DisplayName,
            BaseUrl: source.BaseUrl,
            Enabled: source.Enabled,
            HasApiKey: await repository.HasApiKeyAsync(source.Id, cancellationToken),
            ApiPath: source.ApiPath,
            Priority: source.Priority,
            TimeoutSeconds: source.TimeoutSeconds,
            // Copied straight through as nullables: null (unlimited) must stay distinguishable from
            // 0 (a cap of zero) on the wire, exactly as it is in the column (see Source.cs).
            QueryLimit: source.QueryLimit,
            GrabLimit: source.GrabLimit,
            LimitsUnit: source.LimitsUnit,
            NzbAccessMode: source.NzbAccessMode,
            // The enum NAME, matching how SourceTestResponse carries SourceProbeOutcome: a closed set
            // the UI switches on, with no string field anywhere in the path that could carry
            // upstream text.
            RuntimeState: (runtime?.State ?? SourceRuntimeState.Healthy).ToString(),
            DisabledUntil: runtime?.DisabledUntil,
            DisabledLevel: runtime?.DisabledLevel ?? 0,
            LastOutcome: runtime?.LastOutcome,
            // Zero used, never "zero limit": these are the tallies, and the CAPS above stay nullable
            // beside them so null-is-unlimited survives onto the wire intact.
            QueriesUsed: runtime?.QueriesUsed ?? 0,
            GrabsUsed: runtime?.GrabsUsed ?? 0,
            CreatedAt: source.CreatedAt,
            UpdatedAt: source.UpdatedAt);
}
