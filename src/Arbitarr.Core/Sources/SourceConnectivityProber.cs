using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Arbitarr.Core.Sources;

/// <summary>
/// The §3.3 connectivity test: a REAL authenticated request against a configured source, classified
/// into one of the five <see cref="SourceProbeOutcome"/> failure modes.
///
/// <para><b>Real request, not URL validation.</b> The plan rejects a shape check outright — "a test
/// that passes on a wrong key teaches operators to distrust it". So this issues the source's own
/// capabilities call with the stored key attached, and only reports
/// <see cref="SourceProbeOutcome.Ok"/> when the source answers with something that actually parses
/// as that API. A wrong key produces <see cref="SourceProbeOutcome.AuthenticationFailed"/>, which is
/// the entire point.</para>
///
/// <para><b>Which endpoint, and why one is enough (#99).</b> This probes <c>{base}/api?t=caps</c> —
/// the Newznab endpoint. Since #99, <c>NzbHydraSource</c> chooses between <c>{base}/api</c> and
/// <c>{base}/torznab/api</c> per request, so "the endpoint NzbHydraSource uses" is no longer a
/// single address and this probe covers one of the two.
///
/// That is sufficient because of what the five <see cref="SourceProbeOutcome"/> values actually
/// discriminate, all of which are properties of the SOURCE rather than of an endpoint: DNS,
/// routing and TCP reachability (<see cref="SourceProbeOutcome.Unreachable"/>), the TLS handshake
/// (<see cref="SourceProbeOutcome.TlsFailure"/>), whether the stored key is accepted
/// (<see cref="SourceProbeOutcome.AuthenticationFailed"/>), and whether the address points at an
/// NZBHydra2 at all rather than some other service or a login page
/// (<see cref="SourceProbeOutcome.UnexpectedResponse"/>). NZBHydra2 serves both endpoints from one
/// process, one certificate and one API key, so none of those five can differ between them.
///
/// What a single probe does NOT cover is per-endpoint indexer SELECTION — the actual #99 symptom,
/// where <c>/torznab/api</c> answers successfully but with no usenet indexers in the selection.
/// Probing both endpoints would not detect that either: both would return a valid caps document
/// and both would report Ok. That symptom is a search-results question, not a connectivity one, and
/// it is covered where it lives: by the per-protocol upstream-URL tests on
/// <c>NzbHydraSource</c>. Adding a second probe call here would double the probe's latency and its
/// failure surface while answering nothing the first call has not already answered.</para>
///
/// <para><b>Short timeout.</b> §3.3 requires a wrong host to fail in seconds rather than hang the
/// UI, so the probe imposes <see cref="DefaultTimeout"/> itself through a linked cancellation token
/// rather than relying on <see cref="HttpClient.Timeout"/> — the caller's HttpClient is shared and
/// its timeout is not this type's to mutate. A timeout is reported as
/// <see cref="SourceProbeOutcome.Unreachable"/>: from the operator's chair a host that never
/// answers and a host that refuses are the same fix (check the address).</para>
///
/// <para><b>The secret never comes back out.</b> The key is written into the outbound query string
/// and is otherwise untouched: this type returns a bare enum, so there is no field on the result
/// that could carry it, and no exception message, response body, or upstream URL is ever propagated
/// to the caller. Cancellation the CALLER requested is deliberately rethrown rather than classified,
/// so an aborted request is not misreported as a source failure.</para>
/// </summary>
public sealed class SourceConnectivityProber
{
    /// <summary>
    /// §3.3's "short timeout". Long enough for a healthy source on a slow LAN to answer, short
    /// enough that a wrong address fails while the operator is still looking at the button.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public SourceConnectivityProber(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Probes <paramref name="baseUrl"/> with <paramref name="apiKey"/> and classifies the result.
    /// Never throws for a source-side failure — every reachable-world outcome is a
    /// <see cref="SourceProbeOutcome"/> — and never surfaces the key or the upstream's response.
    /// </summary>
    public async Task<SourceProbeOutcome> ProbeAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var probeUri = BuildCapsUri(baseUrl, apiKey);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(probeUri, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            // Auth is checked before success, because a 401/403 is a success-shaped answer from the
            // operator's point of view: the service is there and talking, it just refused the key.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return SourceProbeOutcome.AuthenticationFailed;
            }

            if (!response.IsSuccessStatusCode)
            {
                return SourceProbeOutcome.UnexpectedResponse;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return LooksLikeCapsResponse(body)
                ? SourceProbeOutcome.Ok
                : SourceProbeOutcome.UnexpectedResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict
            // about the source, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired: the source never answered in time.
            return SourceProbeOutcome.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>
    /// Separates a TLS failure from an ordinary connection failure. Both arrive as
    /// <see cref="HttpRequestException"/>, so the distinction lives in the inner exception:
    /// <see cref="AuthenticationException"/> is what the TLS handshake throws on an untrusted or
    /// expired certificate or a hostname mismatch, while <see cref="SocketException"/> is DNS
    /// failure, connection refused, or no route. The message text is never inspected — it is
    /// localised and platform-specific — and never propagated.
    /// </summary>
    private static SourceProbeOutcome Classify(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return SourceProbeOutcome.TlsFailure;
            }

            if (inner is SocketException)
            {
                return SourceProbeOutcome.Unreachable;
            }
        }

        // No socket or TLS cause identified. HttpRequestError carries .NET's own classification for
        // the newer cases (a bad TLS record shows up here rather than as an AuthenticationException
        // on some platforms), so consult it before defaulting.
        return exception.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => SourceProbeOutcome.TlsFailure,
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => SourceProbeOutcome.Unreachable,
            // A transport-level failure we cannot attribute more precisely is reported as
            // unreachable rather than as an unexpected response: no usable answer was received, so
            // "check the address" is the honest next step to suggest.
            _ => SourceProbeOutcome.Unreachable,
        };
    }

    /// <summary>
    /// Whether the body is the Torznab caps document a real source returns, rather than an HTML
    /// login page or an unrelated service's JSON — the check that makes
    /// <see cref="SourceProbeOutcome.UnexpectedResponse"/> mean "you are pointed at the wrong
    /// thing". Deliberately structural and shallow: it confirms a caps element is present without
    /// re-implementing <c>NzbHydraSource</c>'s parser, which lives outside Core (AC6).
    /// </summary>
    private static bool LooksLikeCapsResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        var trimmed = body.TrimStart();

        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            // Torznab caps are XML rooted at <caps>. An HTML error or login page fails this.
            try
            {
                var document = System.Xml.Linq.XDocument.Parse(body);
                return string.Equals(document.Root?.Name.LocalName, "caps", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Xml.XmlException)
            {
                return false;
            }
        }

        if (trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            // Some deployments front the API with a JSON caps document; accept a well-formed object
            // that actually mentions capabilities rather than any JSON at all.
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && (document.RootElement.TryGetProperty("caps", out _)
                        || document.RootElement.TryGetProperty("categories", out _));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the caps URI, preserving any base path the source is mounted under (a source behind a
    /// reverse proxy at <c>/hydra</c> must be probed at <c>/hydra/api</c>, not <c>/api</c>). The key
    /// travels as a query parameter because that is what the Newznab/Torznab API takes — the same
    /// choice <c>NzbHydraSource</c> makes — and this URI is never logged or returned.
    ///
    /// The <c>/api</c> suffix is the Newznab endpoint; see the type doc for why probing that one
    /// alone establishes everything the five outcomes can distinguish (#99).
    /// </summary>
    private static Uri BuildCapsUri(string baseUrl, string? apiKey)
    {
        var basePath = new Uri(baseUrl, UriKind.Absolute);
        var builder = new UriBuilder(basePath)
        {
            Path = basePath.AbsolutePath.TrimEnd('/') + "/api",
            Query = "t=caps" + (string.IsNullOrEmpty(apiKey) ? string.Empty : "&apikey=" + Uri.EscapeDataString(apiKey)),
        };

        return builder.Uri;
    }
}
