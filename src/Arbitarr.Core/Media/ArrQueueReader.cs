using System.Net;
using System.Text.Json;

namespace Arbitarr.Core.Media;

/// <summary>
/// Reads one page of a configured *arr instance's download queue and classifies the outcome into a
/// closed <see cref="ArrSectionStatus"/>. The base class both <see cref="SonarrQueueClient"/> and
/// <see cref="RadarrQueueClient"/> are, because Sonarr's and Radarr's queue endpoints are the same
/// v3 contract at the same path with the same paging parameters and the same document shape.
///
/// <para><b>WHY A SHARED BASE HERE WHEN THE REPOSITORIES AND ENDPOINTS ARE DUPLICATED.</b> The
/// duplicate-don't-generalise call recorded on <c>RadarrInstanceRepository</c> (arb-arrq D3) is about
/// surfaces whose contracts are free to DIVERGE — a stored-configuration shape, an admin wire
/// contract, a validation message. This is not one of those: the two instances speak a protocol
/// defined by someone else, and the thing that would have to be duplicated is the classification of
/// HTTP outcomes into the five statuses. Two copies of that would be two places for a security
/// property (a redirect must not be reported Ok, a 401 must not be reported Unreachable) to drift.
/// The kinds stay apart where they are genuinely separate — separate typed clients so each gets its
/// own registration, its own timeout and its own logger category, and separate credential
/// providers — and share only the wire handling they cannot diverge on without being wrong.</para>
///
/// <para><b>THE KEY RIDES IN THE QUERY STRING</b>, matching <see cref="SonarrConnectivityProber"/>,
/// <see cref="RadarrConnectivityProber"/> and <c>ArrApiProvider</c>. That is a MEASURED decision
/// documented at length on these clients' registrations in <c>Program.cs</c>, not a stylistic one:
/// <c>IHttpClientFactory</c>'s logging handler collapses the whole query string to <c>?*</c> before
/// the message is formatted, so the key never reaches the log store, while <c>LogMessageCleanser</c>
/// remains the guard for every other route a key-bearing URI can take to a log line. The cleanser
/// scrubs query strings but NOT url paths (CLAUDE.md §1), so a key moved into a path segment would
/// be covered by neither layer. Do not "improve" this to an <c>X-Api-Key</c> header either: both
/// work upstream, but a second placement would split the codebase's one convention and invalidate
/// the comments <c>SonarrKeyIsScrubbedFromLogsTests</c> and <c>DisableUriRedactionSwitchTests</c>
/// pin.</para>
///
/// <para><b>NOTHING FROM UPSTREAM ESCAPES.</b> The result is an <see cref="ArrQueuePage"/>, which has
/// no member that could carry an upstream message, an exception, or the address called; the items
/// are an explicit projection (<see cref="ArrQueueItem"/>). No exception message, response body or
/// URL is ever propagated to the caller. Cancellation the CALLER requested is rethrown rather than
/// classified, so an aborted request is not misreported as an *arr failure — the same contract the
/// connectivity probers state.</para>
/// </summary>
public abstract class ArrQueueReader
{
    /// <summary>
    /// The per-call bound, imposed through a LINKED TOKEN and never by assigning
    /// <see cref="HttpClient.Timeout"/>. The client is pooled through
    /// <c>IHttpClientFactory</c> and its timeout is not this type's to mutate — assigning it once a
    /// request has started on the instance throws, and two concurrent reads are enough to produce
    /// that (see <c>ArrApiProvider</c>'s remarks, which record it happening). The registration sets
    /// the client's own timeout once; this narrows it per call.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    protected ArrQueueReader(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Reads page <paramref name="page"/> of the queue at <paramref name="baseUrl"/>, authenticating
    /// with <paramref name="apiKey"/>. Never throws for an upstream-side failure — every
    /// reachable-world outcome is an <see cref="ArrSectionStatus"/> — and never surfaces the key, the
    /// response body, or the address.
    /// </summary>
    public async Task<ArrQueuePage> ReadQueueAsync(
        Uri baseUrl,
        string apiKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);

        var requestUri = BuildQueueUri(baseUrl, apiKey, page, pageSize);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            // Auth is checked before success, because a 401/403 is a success-shaped answer from the
            // operator's chair: the service is there and talking, it just refused the key. This is
            // the same ordering the connectivity probers use, and it is what keeps a wrong key from
            // being reported as an unreachable host.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return ArrQueuePage.Failed(ArrSectionStatus.AuthenticationFailed);
            }

            // A REDIRECT LANDS HERE AS UnexpectedResponse, AND THAT IS THE WHOLE POINT OF
            // AllowAutoRedirect = false ON THIS CLIENT'S REGISTRATION. With redirects disabled the
            // 3xx is a non-success status the handler returns rather than follows, so this process
            // never reissues a key-bearing request at a host nobody configured (SSRF). Reporting it
            // as UnexpectedResponse rather than Unreachable is also the truthful answer: something
            // DID answer, and "the base URL points at a proxy or a login redirect rather than at the
            // *arr" is the operator's actual fix. ArrQueueReaderTests asserts both halves — the
            // status, and that exactly ONE request was issued.
            if (!response.IsSuccessStatusCode)
            {
                return ArrQueuePage.Failed(ArrSectionStatus.UnexpectedResponse);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return Project(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict
            // about the *arr, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own budget expired: the instance never answered in time.
            return ArrQueuePage.Failed(ArrSectionStatus.Unreachable);
        }
        catch (HttpRequestException)
        {
            // DNS failure, connection refused, no route, a TLS handshake failure. They are one
            // answer here: the queue could not be read from that address. The probe section is where
            // those are told apart, and it already names TLS separately — repeating that distinction
            // on this surface would add a status whose fix is a different section's button.
            //
            // The exception is caught WITHOUT being inspected or propagated: its message is
            // localised, platform-specific, and can carry the request URI including its query string.
            return ArrQueuePage.Failed(ArrSectionStatus.Unreachable);
        }
    }

    /// <summary>
    /// Parses the upstream document and projects it. A body that does not parse, or that is JSON of
    /// some other shape (a login page, a proxy error, an array), is
    /// <see cref="ArrSectionStatus.UnexpectedResponse"/> — the same "something answered but it was
    /// not the API" verdict the connectivity probers reach by a shape check rather than a value one.
    /// </summary>
    private static ArrQueuePage Project(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return ArrQueuePage.Failed(ArrSectionStatus.UnexpectedResponse);
        }

        UpstreamQueueDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<UpstreamQueueDocument>(body, SerializerOptions);
        }
        catch (JsonException)
        {
            return ArrQueuePage.Failed(ArrSectionStatus.UnexpectedResponse);
        }

        // A JSON document that parsed but carries no records ARRAY is not a queue document. Checked
        // on the shape rather than on a value, matching the probers' LooksLike*Status: a queue with
        // nothing in it sends "records": [], which is an empty list and not a null one.
        if (document?.Records is null)
        {
            return ArrQueuePage.Failed(ArrSectionStatus.UnexpectedResponse);
        }

        var items = document.Records
            .Select(record => new ArrQueueItem(
                Title: record.Title,
                Status: record.Status,
                TrackedDownloadStatus: record.TrackedDownloadStatus,
                TrackedDownloadState: record.TrackedDownloadState,
                // Upstream sends these as JSON numbers that may carry a fractional part; bytes are
                // whole, and a long is what the frontend's byte formatter expects.
                Size: record.Size is null ? null : (long)record.Size.Value,
                SizeLeft: record.SizeLeft is null ? null : (long)record.SizeLeft.Value,
                TimeLeft: record.TimeLeft,
                EstimatedCompletionTime: record.EstimatedCompletionTime,
                Protocol: record.Protocol,
                DownloadClient: record.DownloadClient,
                Indexer: record.Indexer,
                StatusMessages: FlattenStatusMessages(record.StatusMessages),
                ErrorMessage: record.ErrorMessage))
            .ToList();

        return new ArrQueuePage(ArrSectionStatus.Ok, document.TotalRecords, items);
    }

    /// <summary>
    /// Flattens the upstream's <c>{title, messages[]}</c> pairs into the lines an operator reads.
    /// The title is included only when it carries something the messages do not, so the common case
    /// (a title that IS the message) does not render twice.
    /// </summary>
    private static IReadOnlyList<string> FlattenStatusMessages(List<UpstreamStatusMessage>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        foreach (var message in messages)
        {
            var nested = message.Messages?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList() ?? [];

            if (nested.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(message.Title))
                {
                    lines.Add(message.Title);
                }

                continue;
            }

            lines.AddRange(nested);
        }

        return lines;
    }

    /// <summary>
    /// Builds the queue URI. The key rides in the QUERY STRING for the reason this type's doc states:
    /// that is where the logging handler's <c>?*</c> collapse fires and where
    /// <c>LogMessageCleanser</c> scrubs. A path segment is covered by neither.
    ///
    /// <para>A base PATH is preserved (the <c>/</c>-terminated normalisation), so an *arr behind a
    /// reverse proxy at a sub-path is read at the right place rather than at the proxy's root —
    /// the same handling <c>RadarrConnectivityProber.BuildStatusUri</c> does.</para>
    ///
    /// <para>The paging parameters are passed STRAIGHT THROUGH to the upstream, which pages this
    /// endpoint itself, rather than being applied to a fetched-everything list. The endpoint has
    /// already clamped them.</para>
    /// </summary>
    private static Uri BuildQueueUri(Uri baseUrl, string apiKey, int page, int pageSize)
    {
        var text = baseUrl.ToString();
        var normalized = text.EndsWith('/') ? text : text + "/";

        var builder = new UriBuilder(new Uri(new Uri(normalized), "api/v3/queue"))
        {
            Query = $"page={page}&pageSize={pageSize}&apikey={Uri.EscapeDataString(apiKey)}",
        };

        return builder.Uri;
    }

    /// <summary>
    /// Case-insensitive because the two *arr serve camelCase while a proxy or a fork may not, and a
    /// casing difference is not a reason to report a healthy instance as unreadable.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>
/// The Sonarr queue reader. A TYPED CLIENT of its own rather than a shared instance, so it gets its
/// own <c>Program.cs</c> registration (its own primary handler with
/// <c>AllowAutoRedirect = false</c>, its own timeout) and its own logger category — which is what
/// lets <c>SonarrQueueKeyIsScrubbedFromLogsTests</c> find the rows this client's requests produced
/// and assert the key is absent from them PER ROW.
/// </summary>
public sealed class SonarrQueueClient : ArrQueueReader
{
    public SonarrQueueClient(HttpClient httpClient, TimeSpan? timeout = null)
        : base(httpClient, timeout)
    {
    }
}

/// <summary>
/// The Radarr queue reader. Separate from <see cref="SonarrQueueClient"/> for the reasons that type
/// states: a registration and a logger category of its own. Radarr's queue endpoint is the same v3
/// contract at the same path, which is why the behaviour lives on the shared base.
/// </summary>
public sealed class RadarrQueueClient : ArrQueueReader
{
    public RadarrQueueClient(HttpClient httpClient, TimeSpan? timeout = null)
        : base(httpClient, timeout)
    {
    }
}
