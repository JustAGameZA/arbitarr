using Arbitarr.Core.Media;
using Arbitarr.Data.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// arb-6l9b.3: the admin-gated queue reads — <c>GET /api/admin/arr/sonarr/queue</c> and
/// <c>GET /api/admin/arr/radarr/queue</c>. Both are <c>.RequireAdminApiKey()</c>, so the gate is by
/// PATH PREFIX and never by verb: these are reads, but they are admin configuration-adjacent reads
/// that make an authenticated outbound call on the operator's behalf, exactly as
/// <see cref="AdminArrEndpoints"/>'s <c>GET</c> is gated like its writes.
///
/// <para><b>BOTH ROUTES ARE CONCRETE, NOT TEMPLATED, AND THAT IS LOAD-BEARING.</b> Paging is
/// query-string rather than path-segment specifically to keep it that way:
/// <c>AdminApiKeyRouteEnumerationTests</c>' sweep SKIPS every <c>{</c>-containing route (a templated
/// route needs a real value to resolve), so a <c>/queue/{page}</c> shape would have dropped silently
/// out of the generic gating sweep. The by-name gating assertions in <c>AdminArrEndpointsTests</c>
/// and <c>AdminRadarrEndpointsTests</c> pin them anyway, so neither route is gated only by
/// assumption — but the concrete shape is what keeps the sweep's coverage real. Do NOT add anything
/// to that sweep for these routes; it already picks them up, and its bodilessness is load-bearing.</para>
///
/// <para><b>NEITHER ROUTE TAKES A BODY</b>, so the required-body trap
/// (<see cref="AdminArrEndpoints"/>'s type doc) does not arise here at all: with no body parameter
/// there is no model binding to run ahead of <c>AdminApiKeyFilter</c>, and the gate is strictly
/// first. If either route ever gains a body it must be bound
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> and null-checked in the handler,
/// for the reason that doc sets out at length.</para>
///
/// <para><b>THE KEY IS NOT READ HERE.</b> Each handler obtains its credential from
/// <see cref="SonarrCredentialProvider"/> / <see cref="RadarrCredentialProvider"/>, which are the
/// single production callers of their repositories' <c>ReadApiKeyForUpstreamRequestAsync</c>
/// (CLAUDE.md §1). These endpoints do not touch either reader, and adding a second call site would
/// destroy precisely the guarantee those providers exist to hold —
/// <c>SecretReaderSingleCallerTests</c> fails the build if one is added. The credential goes straight
/// into the outbound request and never comes back: the response carries a closed enum, wording
/// derived from that enum alone, and a projection with no key-shaped member.</para>
///
/// <para><b>A SINGLE SURFACE FOR BOTH KINDS, unlike the configuration endpoints.</b>
/// <see cref="AdminRadarrEndpoints"/> mirrors <see cref="AdminArrEndpoints"/> rather than sharing
/// with it (arb-arrq D3) because those two wire contracts are free to diverge. This one is the other
/// case: the queue response shape is identical for both kinds because the upstream contract is, and
/// the handler body is the credential lookup and the status mapping — the part where two copies
/// would be two places for the NotConfigured-vs-AuthenticationFailed distinction to drift. The kinds
/// stay genuinely separate where they are separate: separate routes, separate typed clients with
/// their own registrations, separate credential providers.</para>
///
/// <para><b>THE WIRE SHAPE IS <see cref="ArrSectionEnvelope{T}"/>, SHARED WITH
/// <see cref="AdminArrLibraryEndpoints"/>.</b> arb-6l9b.3 served a hand-written
/// <c>ArrQueueResponse</c> record and its doc left open whether the sharing should become a generic;
/// arb-6l9b.4 arrived holding all four sections and folded this record onto the generic, so the five
/// outer members are now identical BY CONSTRUCTION rather than by two copies staying in step. The
/// change was rename-only: same JSON names, same casing, same order, same values.</para>
///
/// <para><b><see cref="DefaultPageSize"/> AND <see cref="MaxPageSize"/> ARE DEFINED HERE AND
/// REFERENCED BY THE LIBRARY ENDPOINTS</b> rather than re-typed there — two surfaces on one screen
/// disagreeing about what <c>?pageSize=200</c> means is a difference an operator would read as a bug,
/// and re-typed constants are how that happens. The CLAMPING is deliberately not shared: the library
/// endpoints page server-side over a fetched-whole list rather than passing the parameters upstream,
/// so their clamp has a different job — see <see cref="AdminArrLibraryEndpoints"/>.</para>
/// </summary>
public static class AdminArrQueueEndpoints
{
    /// <summary>Concrete, not templated — see the type doc. Paging is query-string.</summary>
    public const string SonarrQueueRoute = $"{AdminArrEndpoints.SonarrRoute}/queue";

    /// <summary>Concrete, not templated — see the type doc. Paging is query-string.</summary>
    public const string RadarrQueueRoute = $"{AdminRadarrEndpoints.RadarrRoute}/queue";

    /// <summary>
    /// The default page size when the caller names none. Modest on purpose: a queue page is rendered
    /// as rows in a browser, and the common case is a handful of active downloads.
    /// </summary>
    public const int DefaultPageSize = 25;

    /// <summary>The largest page this surface will serve, however large a value the caller asks for.</summary>
    public const int MaxPageSize = 100;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SonarrQueueRoute, GetSonarrQueueAsync)
            .RequireAdminApiKey();

        endpoints.MapGet(RadarrQueueRoute, GetRadarrQueueAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetSonarrQueueAsync(
        SonarrCredentialProvider credentials,
        SonarrQueueClient client,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);

        return Results.Ok(await ReadAsync(
            client,
            credential?.BaseUrl,
            credential?.ApiKey,
            page,
            pageSize,
            "Sonarr",
            cancellationToken));
    }

    private static async Task<IResult> GetRadarrQueueAsync(
        RadarrCredentialProvider credentials,
        RadarrQueueClient client,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);

        return Results.Ok(await ReadAsync(
            client,
            credential?.BaseUrl,
            credential?.ApiKey,
            page,
            pageSize,
            "Radarr",
            cancellationToken));
    }

    /// <summary>
    /// The shared body: clamp the paging, short-circuit an unusable credential, read, and project
    /// onto the envelope.
    ///
    /// <para>A NULL CREDENTIAL IS <see cref="ArrSectionStatus.NotConfigured"/>, and it covers BOTH
    /// "no address stored" and "an address with no key" — the half-configured state the credential
    /// providers report as null rather than as a credential with an empty key. Reading that state as
    /// "not configured" rather than attempting the call is what stops the surface reporting
    /// <see cref="ArrSectionStatus.AuthenticationFailed"/> against an *arr that is not actually
    /// broken, which is the same reasoning the providers' own docs record for the probe.</para>
    /// </summary>
    private static async Task<ArrSectionEnvelope<ArrQueueItem>> ReadAsync(
        ArrQueueReader client,
        Uri? baseUrl,
        string? apiKey,
        int? page,
        int? pageSize,
        string instanceName,
        CancellationToken cancellationToken)
    {
        var resolvedPage = ClampPage(page);
        var resolvedPageSize = ClampPageSize(pageSize);

        if (baseUrl is null || string.IsNullOrWhiteSpace(apiKey))
        {
            return Envelope(
                ArrSectionStatus.NotConfigured,
                resolvedPage,
                resolvedPageSize,
                ArrQueuePage.Failed(ArrSectionStatus.NotConfigured),
                instanceName);
        }

        var result = await client.ReadQueueAsync(
            baseUrl,
            apiKey,
            resolvedPage,
            resolvedPageSize,
            cancellationToken);

        return Envelope(result.Status, resolvedPage, resolvedPageSize, result, instanceName);
    }

    private static ArrSectionEnvelope<ArrQueueItem> Envelope(
        ArrSectionStatus status,
        int page,
        int pageSize,
        ArrQueuePage result,
        string instanceName) =>
        new(
            Status: status.ToString(),
            Message: DescribeStatus(status, instanceName),
            Page: page,
            PageSize: pageSize,
            TotalRecords: result.TotalRecords,
            Records: result.Items);

    /// <summary>
    /// <b>PAGING IS CLAMPED, NOT REJECTED, AND THAT IS A DELIBERATE DEPARTURE — read this before
    /// "fixing" it.</b> This repository's settled posture for a SETTINGS WRITE is reject-never-clamp
    /// (<c>ArrInstanceRepository.SetAsync</c>, and every other write path): silently storing a value
    /// other than the one an operator typed makes the stored configuration disagree with what they
    /// believe they saved, and the disagreement surfaces much later as behaviour nobody asked for.
    ///
    /// <para>None of that reasoning reaches here. A <c>pageSize</c> in a query string is not a stored
    /// value and nothing persists it; it is a knob on one read, its effect is visible in the same
    /// response that answers it (<see cref="ArrSectionEnvelope{T}.PageSize"/> reports what was actually
    /// served, so the clamp is not silent), and clamping is what every paged web API does because the
    /// alternative — a 400 on <c>?pageSize=1000</c> — turns a harmless over-ask into a broken page.
    /// The upper bound exists so an unbounded value cannot be used to make this process fetch and
    /// buffer an arbitrarily large upstream document. The choice is recorded here rather than left
    /// implicit so it reads as a decision rather than an oversight against the settings rule.</para>
    /// </summary>
    private static int ClampPageSize(int? pageSize) => pageSize switch
    {
        null => DefaultPageSize,
        // Below the floor rather than an exception: a zero or negative page size has no sensible
        // reading other than "the caller did not mean that".
        < 1 => 1,
        > MaxPageSize => MaxPageSize,
        _ => pageSize.Value,
    };

    /// <summary>
    /// Pages are 1-based upstream, so anything below 1 is the first page. A page BEYOND the range is
    /// not clamped: the upstream answers it with an empty <c>records</c> array and the real
    /// <c>totalRecords</c>, which is the truthful answer and lets a client detect the overshoot.
    /// </summary>
    private static int ClampPage(int? page) => page is null or < 1 ? 1 : page.Value;

    /// <summary>
    /// Fixed wording per status. Derived from the closed enum and the instance's NAME alone — never
    /// from the upstream response, an exception, the configured URL, or the key.
    ///
    /// <para>This mirrors <c>AdminArrEndpoints.DescribeOutcome</c> deliberately, and the property it
    /// preserves is the same one: with the message chosen by a <c>switch</c> over a closed enum,
    /// there is no expression anywhere on this path into which upstream-derived text could be
    /// interpolated. An implementation that appended "(upstream said: ...)" would be strictly more
    /// informative and would leak whatever a hostile or misconfigured upstream chose to put in its
    /// body onto an operator's screen and into their bug report.
    /// <c>ArrQueueFixedMessageTests</c> plants a distinctive marker in the upstream body and asserts
    /// it is absent from the WHOLE response, so that mutation fails a test rather than shipping.</para>
    ///
    /// <para><paramref name="instanceName"/> is a compile-time constant supplied by the two handlers
    /// ("Sonarr" / "Radarr") and never a value from the request or the database. It is the only
    /// variable part, and it varies over exactly two literals in this file.</para>
    /// </summary>
    private static string DescribeStatus(ArrSectionStatus status, string instanceName) => status switch
    {
        ArrSectionStatus.Ok =>
            $"Read the {instanceName} queue successfully.",
        ArrSectionStatus.NotConfigured =>
            $"{instanceName} is not configured. Add its base URL and API key in the {instanceName} section, then try again.",
        ArrSectionStatus.Unreachable =>
            $"Could not reach {instanceName}: no response from that address before the timeout. Check the base URL, the port, and that {instanceName} is running.",
        ArrSectionStatus.AuthenticationFailed =>
            $"{instanceName} is reachable but rejected the API key. Check the key in {instanceName} under Settings > General.",
        ArrSectionStatus.UnexpectedResponse =>
            $"Something answered but it was not {instanceName}. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than {instanceName} itself.",
        _ => "The queue could not be read.",
    };
}
