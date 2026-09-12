using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Arbitarr.Core.Sources;

namespace Arbitarr.Core.Media;

/// <summary>
/// The connectivity test for the configured Radarr instance: a REAL authenticated request against
/// it, classified into one of the five <see cref="SourceProbeOutcome"/> values.
///
/// <para><b>WHY IT REUSES <see cref="SourceProbeOutcome"/> RATHER THAN DEFINING ITS OWN.</b> The
/// same substantive ground <see cref="SonarrConnectivityProber"/> states: CONTEXT.md distinguishes
/// that enum from <c>OllamaProbeOutcome</c> because an AI backend carries no key, so
/// <see cref="SourceProbeOutcome.AuthenticationFailed"/> could never be produced there. Radarr DOES
/// carry a key, and a wrong key is the single most likely misconfiguration this button exists to
/// catch, so all five members are reachable here and the existing enum is the right one. Defining a
/// sixth identical enum would add a type without adding a distinction.</para>
///
/// <para><b>Real request, not URL validation.</b> A shape check that passes with a wrong key teaches
/// operators to distrust the button. This issues <c>{base}/api/v3/system/status</c>, the endpoint
/// Radarr answers only for an accepted key, and reports <see cref="SourceProbeOutcome.Ok"/> only when
/// the body parses as a Radarr status document. A wrong key produces
/// <see cref="SourceProbeOutcome.AuthenticationFailed"/>, which is the point. The address pointing at
/// something that is not Radarr at all — another *arr, a reverse proxy's login page — produces
/// <see cref="SourceProbeOutcome.UnexpectedResponse"/>, a different fix.</para>
///
/// <para><b>THE SECRET NEVER COMES BACK OUT.</b> The key is written into the outbound QUERY STRING —
/// the same placement <see cref="SonarrConnectivityProber"/> and <c>ArrApiProvider</c> use, and a
/// MEASURED decision rather than a stylistic one (see this client's registration in
/// <c>Program.cs</c>): <c>IHttpClientFactory</c>'s logging handler collapses the whole query string
/// to <c>?*</c> before the message is formatted, so the key never reaches the log store at all, while
/// <c>LogMessageCleanser</c> stays the guard for every other way a key-bearing URI can reach a log
/// line. The cleanser scrubs credentials in query strings but NOT in URL paths (CLAUDE.md §1), so
/// moving the key to a path segment would be covered by neither layer. Do not "improve" this to an
/// <c>X-Api-Key</c> header: both work upstream, but a second placement would split the codebase's one
/// convention and invalidate the comments <c>SonarrKeyIsScrubbedFromLogsTests</c> and
/// <c>DisableUriRedactionSwitchTests</c> pin.</para>
///
/// <para>This type returns a bare enum, so there is no field on the result that could carry the key,
/// and no exception message, response body, or upstream URL is ever propagated to the caller.
/// Cancellation the CALLER requested is rethrown rather than classified, so an aborted request is not
/// misreported as a Radarr failure.</para>
/// </summary>
public sealed class RadarrConnectivityProber
{
    /// <summary>
    /// Long enough for a healthy Radarr on a slow LAN to answer, short enough that a wrong address
    /// fails while the operator is still looking at the button. Matches
    /// <see cref="SonarrConnectivityProber.DefaultTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public RadarrConnectivityProber(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Probes <paramref name="baseUrl"/> with <paramref name="apiKey"/> and classifies the result.
    /// Never throws for a Radarr-side failure — every reachable-world outcome is a
    /// <see cref="SourceProbeOutcome"/> — and never surfaces the key or the response.
    /// </summary>
    public async Task<SourceProbeOutcome> ProbeAsync(
        string baseUrl,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        var probeUri = BuildStatusUri(baseUrl, apiKey);

        // The timeout is imposed here through a linked token rather than by setting
        // HttpClient.Timeout: the client is shared through IHttpClientFactory and its timeout is not
        // this type's to mutate (assigning it after a request has started throws — see
        // ArrApiProvider's remarks).
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(probeUri, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            // Auth is checked before success, because a 401/403 is a success-shaped answer from the
            // operator's chair: the service is there and talking, it just refused the key.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return SourceProbeOutcome.AuthenticationFailed;
            }

            if (!response.IsSuccessStatusCode)
            {
                return SourceProbeOutcome.UnexpectedResponse;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return LooksLikeRadarrStatus(body)
                ? SourceProbeOutcome.Ok
                : SourceProbeOutcome.UnexpectedResponse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict
            // about Radarr, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired: Radarr never answered in time.
            return SourceProbeOutcome.Unreachable;
        }
        catch (HttpRequestException ex)
        {
            return Classify(ex);
        }
    }

    /// <summary>
    /// Separates a TLS failure from an ordinary connection failure, exactly as
    /// <see cref="SonarrConnectivityProber"/> and <c>SourceConnectivityProber</c> do. Both arrive as
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

        return SourceProbeOutcome.Unreachable;
    }

    /// <summary>
    /// Whether the body is Radarr's own <c>/api/v3/system/status</c> document rather than some other
    /// service that happened to answer 200. Checked on the shape (a JSON object carrying
    /// <c>appName</c> or <c>version</c>) rather than on an exact value, so a Radarr version that
    /// renames a field does not read as "not Radarr" — but a login page, a reverse-proxy error page,
    /// or a different service still does.
    ///
    /// <para>Deliberately NOT checked against <c>appName == "Radarr"</c>, even though that would
    /// distinguish Radarr from Sonarr: the two are configured in separate sections against separate
    /// addresses, so pointing one at the other is a mis-entry the operator sees immediately in the
    /// library that comes back empty, and a value check would additionally break against any fork or
    /// proxy that answers a compatible status document under a different name. Shape, not
    /// identity — the same call <see cref="SonarrConnectivityProber"/> makes.</para>
    /// </summary>
    private static bool LooksLikeRadarrStatus(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && (document.RootElement.TryGetProperty("appName", out _)
                    || document.RootElement.TryGetProperty("version", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the probe URI. The key rides in the QUERY STRING, matching
    /// <see cref="SonarrConnectivityProber"/> and <c>ArrApiProvider</c>, for the reason stated in
    /// this type's doc: that is where the logging handler's <c>?*</c> collapse fires and where
    /// <c>LogMessageCleanser</c> scrubs. A path segment is covered by neither.
    /// </summary>
    private static Uri BuildStatusUri(string baseUrl, string? apiKey)
    {
        var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var builder = new UriBuilder(new Uri(new Uri(normalized), "api/v3/system/status"))
        {
            Query = "apikey=" + Uri.EscapeDataString(apiKey ?? string.Empty),
        };
        return builder.Uri;
    }
}
