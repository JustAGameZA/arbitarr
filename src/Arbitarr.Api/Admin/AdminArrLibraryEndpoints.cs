using Arbitarr.Core.Media;
using Arbitarr.Data.Media;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// arb-6l9b.4: the admin-gated library reads — <c>GET /api/admin/arr/sonarr/series</c> and
/// <c>GET /api/admin/arr/radarr/movies</c>. Both are <c>.RequireAdminApiKey()</c>, so the gate is by
/// PATH PREFIX and never by verb: these are reads, but they are admin configuration-adjacent reads
/// that make an authenticated outbound call on the operator's behalf, exactly as
/// <see cref="AdminArrEndpoints"/>'s <c>GET</c> is gated like its writes and exactly as
/// <see cref="AdminArrQueueEndpoints"/>'s reads are.
///
/// <para><b>BOTH ROUTES ARE CONCRETE, NOT TEMPLATED, AND THAT IS LOAD-BEARING.</b> Paging and
/// filtering are query-string rather than path-segment specifically to keep it that way:
/// <c>AdminApiKeyRouteEnumerationTests</c>' sweep SKIPS every <c>{</c>-containing route (a templated
/// route needs a real value to resolve), so a <c>/series/{page}</c> shape would have dropped silently
/// out of the generic gating sweep. The by-name gating assertions in
/// <c>AdminArrLibraryEndpointsTests</c> pin them anyway, so neither route is gated only by
/// assumption — but the concrete shape is what keeps the sweep's coverage real. Do NOT add anything
/// to that sweep for these routes; it already picks them up, and its bodilessness is load-bearing.</para>
///
/// <para><b>NEITHER ROUTE TAKES A BODY</b>, so the required-body trap
/// (<see cref="AdminArrEndpoints"/>'s type doc) does not arise here at all: with no body parameter
/// there is no model binding to run ahead of <c>AdminApiKeyFilter</c>, and the gate is strictly
/// first. If either route ever gains a body it must be bound
/// <c>[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)]</c> and null-checked in the handler.</para>
///
/// <para><b>THE KEY IS NOT READ HERE.</b> Each handler obtains its credential from
/// <see cref="SonarrCredentialProvider"/> / <see cref="RadarrCredentialProvider"/>, which are the
/// single production callers of their repositories' <c>ReadApiKeyForUpstreamRequestAsync</c>
/// (CLAUDE.md §1). These endpoints do not touch either reader, and adding a second call site would
/// destroy precisely the guarantee those providers exist to hold —
/// <c>SecretReaderSingleCallerTests</c> fails the build if one is added.</para>
///
/// <para><b>PAGING HAPPENS HERE, NOT UPSTREAM, AND THE ORDER IS A CORRECTNESS PROPERTY.</b>
/// <c>/api/v3/series</c> and <c>/api/v3/movie</c> are UNPAGED and take no filtering parameters, which
/// is the whole reason <see cref="ArrLibraryReader{TItem}"/> fetches once, projects immediately, then
/// filters, sorts and pages in that order. <c>TotalRecords</c> is the count after filtering and
/// before paging; see that type for why getting it the other way round is silent.</para>
///
/// <para><b>THE PAGE-SIZE CONSTANTS ARE <see cref="AdminArrQueueEndpoints"/>'S, REFERENCED RATHER
/// THAN RE-TYPED.</b> Two surfaces on one screen disagreeing about what <c>?pageSize=200</c> means is
/// a difference an operator would read as a bug. The CLAMPING is its own here rather than shared,
/// which the queue endpoints' doc anticipated: their clamped values are handed to the upstream, while
/// these bound a slice taken locally, so the two clamps answer different questions even where they
/// currently agree on the numbers.</para>
/// </summary>
public static class AdminArrLibraryEndpoints
{
    /// <summary>Concrete, not templated — see the type doc. Paging and filtering are query-string.</summary>
    public const string SonarrSeriesRoute = $"{AdminArrEndpoints.SonarrRoute}/series";

    /// <summary>Concrete, not templated — see the type doc. Paging and filtering are query-string.</summary>
    public const string RadarrMoviesRoute = $"{AdminRadarrEndpoints.RadarrRoute}/movies";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(SonarrSeriesRoute, GetSonarrSeriesAsync)
            .RequireAdminApiKey();

        endpoints.MapGet(RadarrMoviesRoute, GetRadarrMoviesAsync)
            .RequireAdminApiKey();
    }

    private static async Task<IResult> GetSonarrSeriesAsync(
        SonarrCredentialProvider credentials,
        SonarrLibraryClient client,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);

        return Results.Ok(await ReadAsync(
            client,
            credential?.BaseUrl,
            credential?.ApiKey,
            q,
            page,
            pageSize,
            "Sonarr",
            "series",
            cancellationToken));
    }

    private static async Task<IResult> GetRadarrMoviesAsync(
        RadarrCredentialProvider credentials,
        RadarrLibraryClient client,
        string? q,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(cancellationToken);

        return Results.Ok(await ReadAsync(
            client,
            credential?.BaseUrl,
            credential?.ApiKey,
            q,
            page,
            pageSize,
            "Radarr",
            "movie library",
            cancellationToken));
    }

    /// <summary>
    /// The shared body: clamp the paging, short-circuit an unusable credential, read, and project onto
    /// the envelope.
    ///
    /// <para>A NULL CREDENTIAL IS <see cref="ArrSectionStatus.NotConfigured"/>, and it covers BOTH
    /// "no address stored" and "an address with no key" — the half-configured state the credential
    /// providers report as null rather than as a credential with an empty key. Reading that state as
    /// "not configured" rather than attempting the call is what stops the surface reporting
    /// <see cref="ArrSectionStatus.AuthenticationFailed"/> against an *arr that is not actually
    /// broken.</para>
    /// </summary>
    private static async Task<ArrSectionEnvelope<TItem>> ReadAsync<TItem>(
        ArrLibraryReader<TItem> client,
        Uri? baseUrl,
        string? apiKey,
        string? query,
        int? page,
        int? pageSize,
        string instanceName,
        string sectionName,
        CancellationToken cancellationToken)
    {
        var resolvedPage = ClampPage(page);
        var resolvedPageSize = ClampPageSize(pageSize);

        if (baseUrl is null || string.IsNullOrWhiteSpace(apiKey))
        {
            return Envelope(
                ArrLibraryPage<TItem>.Failed(ArrSectionStatus.NotConfigured),
                resolvedPage,
                resolvedPageSize,
                instanceName,
                sectionName);
        }

        var result = await client.ReadLibraryAsync(
            baseUrl,
            apiKey,
            query,
            resolvedPage,
            resolvedPageSize,
            cancellationToken);

        return Envelope(result, resolvedPage, resolvedPageSize, instanceName, sectionName);
    }

    private static ArrSectionEnvelope<TItem> Envelope<TItem>(
        ArrLibraryPage<TItem> result,
        int page,
        int pageSize,
        string instanceName,
        string sectionName) =>
        new(
            Status: result.Status.ToString(),
            Message: DescribeStatus(result.Status, instanceName, sectionName),
            Page: page,
            PageSize: pageSize,
            TotalRecords: result.TotalRecords,
            Records: result.Items);

    /// <summary>
    /// <b>PAGING IS CLAMPED, NOT REJECTED.</b> The reasoning is the one
    /// <c>AdminArrQueueEndpoints.ClampPageSize</c> sets out at length for its own clamp — read that
    /// comment rather than a summary of it — and it is not restated here because a rule stated in full
    /// in two places is two places to keep true. The BOUNDS themselves are that class's constants
    /// referenced rather than re-typed, so the two surfaces cannot drift apart on what
    /// <c>?pageSize=200</c> means.
    ///
    /// <para>What differs here is what the bound BUYS. On the queue surface the clamped size is handed
    /// to the upstream, so the ceiling limits how large a document this process asks for. These
    /// endpoints cannot do that — <c>/api/v3/series</c> and <c>/api/v3/movie</c> take no paging
    /// parameters and the whole library arrives regardless — so the ceiling here limits only the slice
    /// SERVED. The fetch is made affordable instead by projecting each record immediately, which drops
    /// <c>images</c> and every path field before anything is retained.</para>
    /// </summary>
    private static int ClampPageSize(int? pageSize) => pageSize switch
    {
        null => AdminArrQueueEndpoints.DefaultPageSize,
        // Below the floor rather than an exception: a zero or negative page size has no sensible
        // reading other than "the caller did not mean that".
        < 1 => 1,
        > AdminArrQueueEndpoints.MaxPageSize => AdminArrQueueEndpoints.MaxPageSize,
        _ => pageSize.Value,
    };

    /// <summary>
    /// Pages are 1-based, so anything below 1 is the first page. A page BEYOND the range is not
    /// clamped: it serves an empty <c>records</c> array with the real <c>totalRecords</c>, which is
    /// the truthful answer and lets a client detect the overshoot rather than silently being handed
    /// page 1 instead. The queue surface gets that behaviour from the upstream; here
    /// <see cref="ArrLibraryReader{TItem}"/> produces it deliberately by taking the count before the
    /// slice.
    /// </summary>
    private static int ClampPage(int? page) => page is null or < 1 ? 1 : page.Value;

    /// <summary>
    /// Fixed wording per status. Derived from the closed enum and the instance's NAME alone — never
    /// from the upstream response, an exception, the configured URL, or the key.
    ///
    /// <para>This mirrors <c>AdminArrQueueEndpoints.DescribeStatus</c> in the property it preserves
    /// rather than in its text, and the WORDING IS DELIBERATELY THIS SURFACE'S OWN: an operator
    /// reading "could not reach Sonarr" wants to know which section failed, and the two sections fail
    /// for the same reasons but are fixed by looking at different screens. Sharing the strings would
    /// buy nothing and would couple two surfaces that are free to word themselves differently.</para>
    ///
    /// <para>The property that is NOT free to differ: with the message chosen by a <c>switch</c> over
    /// a closed enum, there is no expression anywhere on this path into which upstream-derived text
    /// could be interpolated. An implementation that appended "(upstream said: ...)" would be strictly
    /// more informative and would leak whatever a hostile or misconfigured upstream chose to put in
    /// its body onto an operator's screen and into their bug report.
    /// <c>AdminArrLibraryEndpointsTests</c> plants a distinctive marker in the upstream body and
    /// asserts it is absent from the WHOLE response, so that mutation fails a test rather than
    /// shipping.</para>
    ///
    /// <para><paramref name="instanceName"/> and <paramref name="sectionName"/> are COMPILE-TIME
    /// LITERALS supplied by the two handlers ("Sonarr"/"series", "Radarr"/"movie library") and never
    /// values from the request or the database. They are the only variable parts, and each varies over
    /// exactly two literals in this file.</para>
    /// </summary>
    private static string DescribeStatus(
        ArrSectionStatus status,
        string instanceName,
        string sectionName) => status switch
    {
        ArrSectionStatus.Ok =>
            $"Read the {instanceName} {sectionName} successfully.",
        ArrSectionStatus.NotConfigured =>
            $"{instanceName} is not configured. Add its base URL and API key in the {instanceName} section, then try again.",
        ArrSectionStatus.Unreachable =>
            $"Could not reach {instanceName}: no response from that address before the timeout. Check the base URL, the port, and that {instanceName} is running.",
        ArrSectionStatus.AuthenticationFailed =>
            $"{instanceName} is reachable but rejected the API key. Check the key in {instanceName} under Settings > General.",
        ArrSectionStatus.UnexpectedResponse =>
            $"Something answered but it was not {instanceName}. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than {instanceName} itself.",
        _ => $"The {instanceName} {sectionName} could not be read.",
    };
}
