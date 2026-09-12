using System.Net;
using System.Text.Json;

namespace Arbitarr.Core.Media;

/// <summary>
/// Reads a configured *arr instance's WHOLE library, projects it, then filters, sorts and pages it
/// HERE. The base class both <see cref="SonarrLibraryClient"/> and <see cref="RadarrLibraryClient"/>
/// are, classifying the HTTP outcome into the same closed <see cref="ArrSectionStatus"/> the queue
/// surface uses.
///
/// <para><b>THE UPSTREAM ENDPOINTS ARE UNPAGED, WHICH IS WHY THE PAGING LIVES HERE.</b>
/// <c>GET /api/v3/series</c> and <c>GET /api/v3/movie</c> return the entire library in one document
/// and accept NO paging or filtering parameters — unlike <c>/api/v3/queue</c>, whose parameters
/// <see cref="ArrQueueReader"/> passes straight through. Only <c>?apikey=</c> is sent. Do not add
/// <c>includeUnknownSeriesItems</c> / <c>includeUnknownMovieItems</c> (those are QUEUE parameters and
/// mean nothing here) or <c>tvdbId</c> / <c>tmdbId</c> (those select a single title and would turn a
/// library listing into a lookup).</para>
///
/// <para><b>PROJECT FIRST, THEN FILTER, THEN SORT, THEN PAGE — IN THAT ORDER.</b> Projecting
/// immediately is what makes a whole-library fetch affordable: the parsed shapes drop <c>images</c>
/// and every path field, which is most of the document's bytes. The order of the other three is a
/// correctness property, not a preference: <see cref="ArrLibraryPage{T}.TotalRecords"/> is the count
/// AFTER filtering and BEFORE paging, so computing it at either other moment produces a pager that
/// silently lies.</para>
///
/// <para><b>THIS TYPE DUPLICATES ~40 LINES OF TRANSPORT AND CLASSIFICATION FROM
/// <see cref="ArrQueueReader"/>, DELIBERATELY, AND <see cref="ArrQueueReader"/> IS THE ORIGINAL.</b>
/// The shared shape — auth checked before success, redirects landing as
/// <see cref="ArrSectionStatus.UnexpectedResponse"/> with <c>AllowAutoRedirect = false</c>, caller
/// cancellation rethrown rather than classified — is copied rather than factored out because
/// arb-6l9b.3's file had just merged when this was written, and editing a just-merged file is how
/// rebases go wrong. A THIRD copy is the signal that the cost has flipped: at that point lift the
/// transport into a shared base and point all three at it.</para>
///
/// <para><b>THE KEY RIDES IN THE QUERY STRING</b>, matching <see cref="ArrQueueReader"/>,
/// <see cref="SonarrConnectivityProber"/>, <see cref="RadarrConnectivityProber"/> and
/// <c>ArrApiProvider</c>. That is a MEASURED decision documented at length on these clients'
/// registrations in <c>Program.cs</c>: <c>IHttpClientFactory</c>'s logging handler collapses the
/// whole query string to <c>?*</c> before the message is formatted, so the key never reaches the log
/// store, while <c>LogMessageCleanser</c> — which scrubs query strings but NOT url paths (CLAUDE.md
/// §1) — remains the guard for every other route a key-bearing URI can take to a log line. Do not
/// move it to an <c>X-Api-Key</c> header: both work upstream, but a second placement would split the
/// codebase's one convention and invalidate the comments
/// <c>ArrLibraryKeyIsScrubbedFromLogsTests</c> and <c>DisableUriRedactionSwitchTests</c> pin.</para>
///
/// <para><b>NOTHING FROM UPSTREAM ESCAPES.</b> The result is an <see cref="ArrLibraryPage{T}"/>,
/// which has no member that could carry an upstream message, an exception, or the address called. No
/// exception message, response body or URL is ever propagated to the caller.</para>
/// </summary>
/// <typeparam name="TItem">
/// The slim item this kind serves — <see cref="ArrSeriesItem"/> or <see cref="ArrMovieItem"/>. The
/// type parameter sits on the READER rather than on a per-call method so each leaf's projection and
/// its title accessor are fixed by construction, with no cast anywhere on the path.
/// </typeparam>
public abstract class ArrLibraryReader<TItem>
{
    /// <summary>
    /// The per-call bound, imposed through a LINKED TOKEN and never by assigning
    /// <see cref="HttpClient.Timeout"/>. The client is pooled through <c>IHttpClientFactory</c> and
    /// its timeout is not this type's to mutate — assigning it once a request has started on the
    /// instance throws, and two concurrent reads are enough to produce that (see
    /// <c>ArrApiProvider</c>'s remarks, which record it happening). The registration sets the client's
    /// own timeout once; this narrows it per call.
    ///
    /// <para>Longer than <see cref="ArrQueueReader.DefaultTimeout"/> because the work is different: a
    /// queue page is a handful of active downloads, while this fetches and parses a whole library.</para>
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    protected ArrLibraryReader(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// The upstream path this reader's kind lists its library at, relative to the instance's base
    /// URL. A compile-time constant per leaf, never a value from a request.
    /// </summary>
    protected abstract string LibraryPath { get; }

    /// <summary>
    /// Parses the upstream document and projects it onto this kind's slim item. Returns
    /// <see langword="null"/> when the body is not a library document of this kind — which the caller
    /// turns into <see cref="ArrSectionStatus.UnexpectedResponse"/>.
    /// </summary>
    protected abstract IReadOnlyList<TItem>? ProjectAll(Stream body);

    /// <summary>The title the filter matches on and the sort orders by, for one projected item.</summary>
    protected abstract string? TitleOf(TItem item);

    /// <summary>
    /// Reads the whole library at <paramref name="baseUrl"/>, authenticating with
    /// <paramref name="apiKey"/>, then filters by <paramref name="query"/>, sorts by title and serves
    /// page <paramref name="page"/>. Never throws for an upstream-side failure — every reachable-world
    /// outcome is an <see cref="ArrSectionStatus"/> — and never surfaces the key, the response body,
    /// or the address.
    /// </summary>
    public async Task<ArrLibraryPage<TItem>> ReadLibraryAsync(
        Uri baseUrl,
        string apiKey,
        string? query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var requestUri = BuildLibraryUri(baseUrl, apiKey);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            // ResponseHeadersRead, not ResponseContentRead: a movie library is tens of megabytes, and
            // this lets the parse consume the stream as it arrives rather than buffering the whole
            // document into a string first. ReadAsStringAsync on this path would allocate the entire
            // body twice over — once as the string and once as the parsed graph.
            using var response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, timeoutSource.Token)
                .ConfigureAwait(false);

            // Auth is checked before success, because a 401/403 is a success-shaped answer from the
            // operator's chair: the service is there and talking, it just refused the key. Same
            // ordering as ArrQueueReader and the connectivity probers, and it is what keeps a wrong
            // key from being reported as an unreachable host.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ArrLibraryPage<TItem>.Failed(ArrSectionStatus.AuthenticationFailed);
            }

            // A REDIRECT LANDS HERE AS UnexpectedResponse, AND THAT IS THE WHOLE POINT OF
            // AllowAutoRedirect = false ON THIS CLIENT'S REGISTRATION. With redirects disabled the
            // 3xx is a non-success status the handler returns rather than follows, so this process
            // never reissues a key-bearing request at a host nobody configured (SSRF). Reporting it
            // as UnexpectedResponse rather than Unreachable is also the truthful answer: something
            // DID answer. The tests assert both halves — the status, and that exactly ONE request
            // was issued.
            if (!response.IsSuccessStatusCode)
            {
                return ArrLibraryPage<TItem>.Failed(ArrSectionStatus.UnexpectedResponse);
            }

            using var stream = await response.Content
                .ReadAsStreamAsync(timeoutSource.Token)
                .ConfigureAwait(false);

            var projected = ProjectAll(stream);
            if (projected is null)
            {
                return ArrLibraryPage<TItem>.Failed(ArrSectionStatus.UnexpectedResponse);
            }

            return Paginate(projected, query, page, pageSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict about
            // the *arr, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own budget expired: the instance never answered in time.
            return ArrLibraryPage<TItem>.Failed(ArrSectionStatus.Unreachable);
        }
        catch (HttpRequestException)
        {
            // DNS failure, connection refused, no route, a TLS handshake failure. They are one answer
            // here: the library could not be read from that address. The probe section is where those
            // are told apart.
            //
            // The exception is caught WITHOUT being inspected or propagated: its message is
            // localised, platform-specific, and can carry the request URI including its query string.
            return ArrLibraryPage<TItem>.Failed(ArrSectionStatus.Unreachable);
        }
    }

    /// <summary>
    /// FILTER, THEN SORT, THEN PAGE — and count between the first two steps.
    ///
    /// <para>The filter is a case-insensitive title CONTAINS via
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> rather than <c>ToLower()</c> on both sides:
    /// lowering allocates two strings per comparison per row across a whole library, and it is
    /// culture-sensitive in a way the comparison constant is not.</para>
    ///
    /// <para>The sort names <see cref="StringComparer.OrdinalIgnoreCase"/> EXPLICITLY. A bare
    /// <c>OrderBy(x =&gt; x.Title)</c> on strings uses the current culture's collation, which differs
    /// between a developer's machine, CI and a container with no ICU — so the same library would come
    /// back in a different order in each, and a paging test would flake for a reason that looks like
    /// a paging bug. Ordinal-ignore-case is stable everywhere, which is the property that
    /// matters more here than any particular alphabetisation.</para>
    ///
    /// <para><see cref="ArrLibraryPage{T}.TotalRecords"/> IS TAKEN AFTER THE FILTER AND BEFORE THE
    /// PAGE. A page beyond the range therefore serves an EMPTY list with the real total — the
    /// truthful answer that lets a client detect the overshoot. The queue surface got that for free
    /// from upstream; here it is produced deliberately.</para>
    /// </summary>
    private ArrLibraryPage<TItem> Paginate(
        IReadOnlyList<TItem> projected,
        string? query,
        int page,
        int pageSize)
    {
        IEnumerable<TItem> filtered = projected;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            filtered = filtered.Where(item =>
                TitleOf(item)?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
        }

        var matched = filtered.ToList();

        var sorted = matched
            .OrderBy(TitleOf, StringComparer.OrdinalIgnoreCase)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return new ArrLibraryPage<TItem>(ArrSectionStatus.Ok, matched.Count, sorted);
    }

    /// <summary>
    /// Builds the library URI. ONLY <c>apikey</c> is sent: these endpoints take no paging or filtering
    /// parameters, which is the whole reason this reader pages server-side. The key rides in the
    /// QUERY STRING for the reason this type's doc states — that is where the logging handler's
    /// <c>?*</c> collapse fires and where <c>LogMessageCleanser</c> scrubs; a path segment is covered
    /// by neither.
    ///
    /// <para>A base PATH is preserved (the <c>/</c>-terminated normalisation), so an *arr behind a
    /// reverse proxy at a sub-path is read at the right place rather than at the proxy's root — the
    /// same handling <see cref="ArrQueueReader"/> and <c>RadarrConnectivityProber.BuildStatusUri</c>
    /// do.</para>
    /// </summary>
    private Uri BuildLibraryUri(Uri baseUrl, string apiKey)
    {
        var text = baseUrl.ToString();
        var normalized = text.EndsWith('/') ? text : text + "/";

        var builder = new UriBuilder(new Uri(new Uri(normalized), LibraryPath))
        {
            Query = $"apikey={Uri.EscapeDataString(apiKey)}",
        };

        return builder.Uri;
    }

    /// <summary>
    /// Deserialises the upstream array, returning <see langword="null"/> for anything that is not one.
    /// Both library endpoints answer with a bare JSON ARRAY at the top level rather than the
    /// <c>{records:[...]}</c> envelope the queue uses, so a document of the wrong shape (a login page,
    /// a proxy error, an object) is caught here as
    /// <see cref="ArrSectionStatus.UnexpectedResponse"/> — the same "something answered but it was not
    /// the API" verdict reached by a shape check rather than a value one.
    /// </summary>
    private protected static List<TUpstream>? DeserializeArray<TUpstream>(Stream body)
    {
        try
        {
            // An EMPTY BODY IS CHECKED BY THE PARSE ITSELF here rather than by a string test ahead of
            // it, because there is no string: the body is a stream consumed once. System.Text.Json
            // raises JsonException on a zero-length input, and a body that is literally "null" returns
            // a null list — both land on the null return below, which the caller reads as
            // UnexpectedResponse. That is the same verdict a whitespace-only body gets through
            // ArrQueueReader's explicit IsNullOrWhiteSpace guard; only the mechanism differs, because
            // buffering the document into a string to test it would undo the streaming this endpoint
            // needs.
            return JsonSerializer.Deserialize<List<TUpstream>>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Case-insensitive because the two *arr serve camelCase while a proxy or a fork may not, and a
    /// casing difference is not a reason to report a healthy instance as unreadable.
    /// </summary>
    private protected static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// The Sonarr series reader. A TYPED CLIENT of its own rather than a shared instance, so it gets its
/// own <c>Program.cs</c> registration (its own primary handler with <c>AllowAutoRedirect = false</c>,
/// its own timeout) and its own logger category — which is what lets
/// <c>ArrLibraryKeyIsScrubbedFromLogsTests</c> find the rows this client's requests produced and
/// assert the key is absent from them PER ROW.
/// </summary>
public sealed class SonarrLibraryClient : ArrLibraryReader<ArrSeriesItem>
{
    public SonarrLibraryClient(HttpClient httpClient, TimeSpan? timeout = null)
        : base(httpClient, timeout)
    {
    }

    /// <summary>Unpaged and unfiltered upstream — see the base type's doc.</summary>
    protected override string LibraryPath => "api/v3/series";

    /// <inheritdoc />
    protected override string? TitleOf(ArrSeriesItem item) => item.Title;

    /// <inheritdoc />
    protected override IReadOnlyList<ArrSeriesItem>? ProjectAll(Stream body)
    {
        var records = DeserializeArray<UpstreamSeriesRecord>(body);
        if (records is null)
        {
            return null;
        }

        return records
            .Select(record => new ArrSeriesItem(
                Id: record.Id,
                Title: record.Title,
                Year: record.Year,
                TvdbId: record.TvdbId,
                Status: record.Status,
                Monitored: record.Monitored,
                SeasonCount: record.Statistics?.SeasonCount ?? 0,
                EpisodeFileCount: record.Statistics?.EpisodeFileCount ?? 0,
                EpisodeCount: record.Statistics?.EpisodeCount ?? 0,
                // Upstream sends bytes as a JSON number that may carry a fractional part.
                SizeOnDisk: record.SizeOnDisk is null ? null : (long)record.SizeOnDisk.Value,
                Network: record.Network))
            .ToList();
    }
}

/// <summary>
/// The Radarr movie reader. Separate from <see cref="SonarrLibraryClient"/> for the reasons that type
/// states: a registration and a logger category of its own. Radarr's library endpoint is the same
/// unpaged v3 shape at a different path, which is why the behaviour lives on the shared base.
/// </summary>
public sealed class RadarrLibraryClient : ArrLibraryReader<ArrMovieItem>
{
    public RadarrLibraryClient(HttpClient httpClient, TimeSpan? timeout = null)
        : base(httpClient, timeout)
    {
    }

    /// <summary>Unpaged and unfiltered upstream — see the base type's doc. Singular, as Radarr names it.</summary>
    protected override string LibraryPath => "api/v3/movie";

    /// <inheritdoc />
    protected override string? TitleOf(ArrMovieItem item) => item.Title;

    /// <inheritdoc />
    protected override IReadOnlyList<ArrMovieItem>? ProjectAll(Stream body)
    {
        var records = DeserializeArray<UpstreamMovieRecord>(body);
        if (records is null)
        {
            return null;
        }

        return records
            .Select(record => new ArrMovieItem(
                Id: record.Id,
                Title: record.Title,
                Year: record.Year,
                TmdbId: record.TmdbId,
                Monitored: record.Monitored,
                HasFile: record.HasFile,
                SizeOnDisk: record.SizeOnDisk is null ? null : (long)record.SizeOnDisk.Value,
                Status: record.Status))
            .ToList();
    }
}
