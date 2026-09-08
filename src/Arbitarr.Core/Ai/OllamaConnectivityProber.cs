using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Arbitarr.Core.Ai;

/// <summary>
/// #89's connectivity test for the AI backend: a REAL request to Ollama's own
/// <c>GET /api/tags</c>, classified into one of the <see cref="OllamaProbeOutcome"/> modes.
///
/// <para><b>Ollama's own API, not a generic reachability check.</b> <c>/api/tags</c> is the model
/// list — the cheapest endpoint Ollama serves that no other service would answer in the same shape,
/// and one that needs no model loaded and starts no inference, so probing costs the backend
/// nothing. A bare TCP connect or a <c>GET /</c> would pass against anything listening on the port,
/// which is precisely the mistake <see cref="OllamaProbeOutcome.UnexpectedResponse"/> exists to
/// report: pointing this setting at the wrong service is the most likely way to misconfigure it,
/// and a probe that cannot detect that teaches the operator to distrust the button.</para>
///
/// <para><b>Not <c>/api/chat</c>.</b> Classification's own endpoint would be the most faithful test,
/// but it loads the model — up to a ~59s cold start (docs/step0-measurements.md) — which cannot fit
/// a probe an operator waits on, and it would answer 404 for a healthy instance that simply has not
/// pulled the configured model yet. That is a model problem, not a connectivity one, and reporting
/// it here would send the operator to fix the address.</para>
///
/// <para><b>Short timeout.</b> <see cref="DefaultTimeout"/> is imposed here through a linked
/// cancellation token rather than via <see cref="HttpClient.Timeout"/> — the caller's client is
/// shared and its timeout is not this type's to mutate. Shorter than the source prober's 10s
/// because Ollama is local infrastructure rather than an internet indexer: a healthy instance on
/// the same host or LAN answers <c>/api/tags</c> in milliseconds, so a wrong address fails while
/// the operator is still looking at the button.</para>
///
/// <para><b>Nothing from the wire reaches the OPERATOR-FACING WORDING.</b> The outcome is a closed
/// enum with no string member, so no branch can put the upstream body, an exception message, or the
/// probed URL into the sentence the endpoint composes. Cancellation the CALLER requested is rethrown
/// rather than classified, so an aborted request is never misreported as a backend failure.</para>
///
/// <para><b>#112: the model NAMES do come back, in their own field.</b> <c>/api/tags</c> is the
/// model list and the probe was already reading it to classify — discarding its contents is what
/// left an operator able to see "Connected successfully" against an instance that had never pulled
/// the configured model. <see cref="OllamaProbeResult.Models"/> carries the names beside the enum,
/// never inside it: they are rendered as list items to choose from, and the wording is still derived
/// from the enum alone. A malformed entry is SKIPPED rather than making the probe fail — the
/// question the button answers is "is this Ollama", and one odd entry is not a no.</para>
/// </summary>
public sealed class OllamaConnectivityProber
{
    /// <summary>
    /// The probe's timeout: 5 seconds. Long enough for a busy local instance to answer a model-list
    /// request, short enough that a wrong address fails while the operator is still watching. Equal
    /// to <c>OllamaOptions.CallTimeout</c> by coincidence of purpose rather than by sharing — that
    /// constant lives in Arbitarr.Ai, which Core does not reference (AC6).
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    public OllamaConnectivityProber(HttpClient httpClient, TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>
    /// Probes <paramref name="baseUrl"/> and classifies the result. Never throws for a backend-side
    /// failure — every reachable-world outcome is an <see cref="OllamaProbeOutcome"/> — and never
    /// surfaces the response body or the address. On <see cref="OllamaProbeOutcome.Ok"/> the
    /// result also carries the model names the instance reported (#112).
    /// </summary>
    public async Task<OllamaProbeResult> ProbeAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            // A stored value that is not a usable address cannot be probed at all. Reported as
            // unreachable rather than thrown: from the operator's chair "this address goes nowhere"
            // is the same next step, and the settings write path already rejects such a value, so
            // reaching here at all means the row predates that validation.
            return OllamaProbeResult.From(OllamaProbeOutcome.Unreachable);
        }

        var probeUri = BuildTagsUri(parsed);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            using var response = await _httpClient
                .GetAsync(probeUri, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Includes 401/403. Ollama has no authentication, so a service demanding
                // credentials at this address is not Ollama — an authentication outcome would
                // send the operator hunting for a key that does not exist. See
                // OllamaProbeOutcome's note on why there are four outcomes and not five.
                return OllamaProbeResult.From(OllamaProbeOutcome.UnexpectedResponse);
            }

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return TryReadTagsResponse(body, out var models)
                ? new OllamaProbeResult(OllamaProbeOutcome.Ok, models)
                : OllamaProbeResult.From(OllamaProbeOutcome.UnexpectedResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The CALLER went away (request aborted, host shutting down). That is not a verdict
            // about the backend, so it must not be reported as one.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our own timeout fired: the backend never answered in time.
            return OllamaProbeResult.From(OllamaProbeOutcome.Unreachable);
        }
        catch (HttpRequestException ex)
        {
            return OllamaProbeResult.From(Classify(ex));
        }
    }

    /// <summary>
    /// Separates a TLS failure from an ordinary connection failure, exactly as
    /// <c>SourceConnectivityProber.Classify</c> does and for the same reasons: both arrive as
    /// <see cref="HttpRequestException"/>, so the distinction lives in the inner exception, and the
    /// message text is never inspected (localised, platform-specific) nor propagated.
    /// </summary>
    private static OllamaProbeOutcome Classify(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return OllamaProbeOutcome.TlsFailure;
            }

            if (inner is SocketException)
            {
                return OllamaProbeOutcome.Unreachable;
            }
        }

        return exception.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => OllamaProbeOutcome.TlsFailure,
            HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError => OllamaProbeOutcome.Unreachable,
            // A transport failure we cannot attribute more precisely is reported as unreachable
            // rather than as an unexpected response: no usable answer was received, so "check the
            // address" is the honest next step to suggest.
            _ => OllamaProbeOutcome.Unreachable,
        };
    }

    /// <summary>
    /// Whether the body is the model list Ollama actually returns, rather than some other service's
    /// 200 — and, when it is, the <c>name</c> of each entry (#112).
    ///
    /// <para><b>AN EMPTY ARRAY IS STILL A YES.</b> <c>/api/tags</c> answers <c>{"models":[...]}</c>,
    /// and an EMPTY array is a perfectly healthy instance with nothing pulled yet — so the check is
    /// for the <c>models</c> property being present and an array, never for it being non-empty, and
    /// never for the extracted name list being non-empty either. Requiring a model here would report
    /// a reachable Ollama as "not Ollama" and send the operator to fix an address that was already
    /// correct. #112 added the extraction below and must not have quietly added that requirement
    /// with it: the return value is decided before a single name is read.</para>
    ///
    /// <para>An entry without a usable string <c>name</c> is SKIPPED rather than rejecting the whole
    /// response, for the same reason: a body that is recognisably Ollama's model list stays
    /// recognisable when one element is odd, and a name the picker cannot render is simply not
    /// offered.</para>
    /// </summary>
    private static bool TryReadTagsResponse(string body, out IReadOnlyList<string> models)
    {
        models = [];

        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("models", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var names = new List<string>();
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value
                    && !string.IsNullOrWhiteSpace(value))
                {
                    names.Add(value);
                }
            }

            models = names;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the <c>/api/tags</c> URI, preserving any base path the instance is mounted under (an
    /// Ollama behind a reverse proxy at <c>/ollama</c> must be probed at <c>/ollama/api/tags</c>),
    /// the same way <c>SourceConnectivityProber.BuildCapsUri</c> does. No query string and no
    /// credential: this URI carries nothing sensitive, which is why the client it runs on needs no
    /// <c>.RemoveAllLoggers()</c> — see the registration comment in Program.cs.
    /// </summary>
    private static Uri BuildTagsUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/api/tags",
            Query = string.Empty,
        };

        return builder.Uri;
    }
}
