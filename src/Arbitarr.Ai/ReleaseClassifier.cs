using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Ai;

/// <summary>
/// Background-only classification entry point (Q1-B): calls <see cref="IOllamaClient"/> to obtain
/// a fresh verdict, protected by the same fail-open contract every other adapter uses via
/// <see cref="IAsyncCircuitBreaker"/> — reused, not reimplemented (M5 step 5). Never called from
/// the search request path; only <see cref="ClassifierWorker"/> and offline/background code should
/// hold a reference to this type. The suppression chain's AI slot never calls this directly — it
/// only reads <see cref="IVerdictCacheReader"/>, which this type's caller is responsible for
/// populating after a successful classification.
///
/// Fail-open (M5-3): when the circuit breaker is open, or the call otherwise fails, this returns
/// <see langword="null"/> rather than throwing out of a background loop — callers treat a null
/// result identically to "no verdict yet" (deterministic-only filtering continues to apply; the
/// cache is simply not populated for that release this cycle).
/// </summary>
public sealed class ReleaseClassifier
{
    private readonly IOllamaClient _ollamaClient;
    private readonly ILogger<ReleaseClassifier> _logger;

    /// <param name="logger">
    /// Optional, and defaulted, so every existing <c>new ReleaseClassifier(client)</c> call site
    /// keeps compiling unchanged — there are several in the test suite, and the Host resolves this
    /// type from DI, which supplies <see cref="ILogger{TCategoryName}"/> automatically without any
    /// registration change. A null logger means "log nowhere", never a null reference.
    /// </param>
    public ReleaseClassifier(IOllamaClient ollamaClient, ILogger<ReleaseClassifier>? logger = null)
    {
        _ollamaClient = ollamaClient ?? throw new ArgumentNullException(nameof(ollamaClient));
        _logger = logger ?? NullLogger<ReleaseClassifier>.Instance;
    }

    /// <summary>
    /// Attempts to classify <paramref name="candidate"/>. Returns <see langword="null"/> on any
    /// failure (circuit open, timeout, malformed response) — deterministic-only behavior is always
    /// the safe fallback (M5-3), never an unhandled exception surfacing out of the background
    /// worker's loop.
    ///
    /// <para>
    /// M5 security review (LOW): catches <see cref="Exception"/> broadly rather than an explicit
    /// list — <see cref="OllamaClient"/> can also fail with <see cref="InvalidOperationException"/>
    /// (missing/unparseable message content) or a <see cref="System.Text.Json.JsonException"/>
    /// (malformed verdict JSON), neither of which is a fail-open failure mode this type should
    /// ever let escape. The only case rethrown is a caller-requested cancellation, so genuine
    /// shutdown/cancellation still propagates instead of being swallowed as "no verdict".
    /// </para>
    ///
    /// <para>
    /// arb-p94g: the catch also LOGS, at Warning, once per failed call. It previously discarded the
    /// exception with no binding, which made a total model outage invisible — the failure produces
    /// no verdict, no throw, and no row, so 24 consecutive failed calls (audit F-007) left the
    /// operator nothing to read. The fail-open behaviour is unchanged: this still returns
    /// <see langword="null"/>, and cancellation still propagates.
    /// </para>
    ///
    /// <para>
    /// The line carries the exception TYPE and MESSAGE and NOTHING THAT IDENTIFIES THE RELEASE — no
    /// title, guid, source name, prompt text or request URL. The exception itself goes in the
    /// ILogger exception slot so the stack is available; both the rendered message and the exception
    /// pass through <c>LogMessageCleanser</c> at the log store's single choke point.
    /// </para>
    ///
    /// <para>
    /// The exception message is NOT stripped of the host, deliberately. A connection failure's
    /// message embeds <c>host:port</c>, and that is the diagnosis — <c>CredentialPatterns</c>'
    /// remarks record why the log cleanser must not gain the host arms that
    /// <c>SanitizedErrorDescription</c> has: <c>/api/admin/logs</c> is admin-gated, so removing the
    /// hostname would cost the diagnostic value without protecting anyone not already
    /// authenticated. The non-2xx path is safe by construction regardless, because
    /// <see cref="Arbitarr.Core.Ai.OllamaRequestException"/> scrubs and caps the response body at
    /// construction.
    /// </para>
    /// </summary>
    public async Task<OllamaVerdict?> TryClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        try
        {
            return await _ollamaClient.ClassifyAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Type is stated in the template as well as carried by the exception argument: the
            // rendered message is what an operator reads in the Logs tab, and a row whose text is
            // only "classification failed" sends them to the exception column to learn what kind
            // of failure it was.
            _logger.LogWarning(
                ex,
                "Classification failed and was skipped for this release ({ExceptionType}): {ExceptionMessage}. " +
                "The classifier fails open, so the search path is unaffected and the release is left unclassified.",
                ex.GetType().Name,
                ex.Message);

            return null;
        }
    }
}
