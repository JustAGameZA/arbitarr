using System.Net;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-x7w8.10: the per-source API-hit budget and durable backoff gate, as an
/// <see cref="IUpstreamSource"/> DECORATOR around a real source.
///
/// <para><b>Why a decorator rather than a change inside the adapters.</b> The gate has to sit
/// between the caller and the outbound HTTP call, which is the one place an adapter also occupies —
/// but putting it in the adapters means writing it twice (NzbHydraSource and the shared
/// Newznab/Torznab adapter) and rewriting both again when the source registry lands. Wrapping
/// instead means the rules exist once, apply to every source kind including any added later, and
/// touch neither adapter, <c>MergeResult</c>, nor <c>IAsyncCircuitBreaker</c>.</para>
///
/// <para><b>THIS IS NOT A CIRCUIT BREAKER AND MUST NOT BE MERGED WITH ONE.</b> The wrapped source
/// still consults <c>IAsyncCircuitBreaker</c> itself, and that stays true: the breaker is an
/// in-process short-window fault detector, this is durable operator-facing accounting, and
/// <see href="../../../docs/adr/0020-api-hit-budget-and-durable-backoff.md">ADR 0020</see>'s whole
/// Context section is about why they are not one component. The two run in series on purpose.</para>
///
/// <para><b>AT THE BUDGET THE SOURCE IS SKIPPED, NEVER FAILED</b>, and the distinction is the point
/// rather than a nicety. A budgeted indexer is working correctly — it has simply been used as much
/// as it may be today — so failing it would feed fault machinery with a non-fault and would report a
/// healthy source as broken. A skip returns an EMPTY candidate list, which
/// <c>UpstreamMergeStage</c> already unions harmlessly, so the merge still answers from the other
/// sources and <c>MergeResult</c> needs no new field (arb-x7w8.7 owns that shape). The skip is
/// EVENTED so the Activity surface can say why a source contributed nothing; an operator seeing an
/// empty result from one indexer and no explanation is exactly the invisibility this bead exists to
/// remove.</para>
///
/// <para><b>A skip does not spend budget and does not touch backoff.</b> No hit event is written
/// when no call is made — that is what keeps the count a record of API hits rather than of
/// intentions — and no outcome is recorded, because not calling a source teaches nothing about
/// whether it works.</para>
/// </summary>
public sealed class BudgetedUpstreamSource : IUpstreamSource
{
    private readonly IUpstreamSource _inner;
    private readonly Source _configuration;
    private readonly SourceApiHitCounter _counter;
    private readonly SourceBackoffStore _backoff;
    private readonly IEventSink _eventSink;

    public BudgetedUpstreamSource(
        IUpstreamSource inner,
        Source configuration,
        SourceApiHitCounter counter,
        SourceBackoffStore backoff,
        IEventSink eventSink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await IsCallableAsync(cancellationToken).ConfigureAwait(false)
            || !await _counter.HasQueryBudgetAsync(_configuration, cancellationToken).ConfigureAwait(false))
        {
            await RecordSkipAsync("query", cancellationToken).ConfigureAwait(false);
            return Array.Empty<ReleaseCandidate>();
        }

        // THE HIT IS EVENTED HERE, on the outbound call, and nowhere else. Not at the endpoint: a
        // snapshot or a warm cache answers a client request without ever reaching this method, and
        // an event written there would charge the operator's allowance for an answer that never left
        // the process. Written BEFORE the call rather than after, so a call that times out or throws
        // still counts — the indexer was contacted and its allowance was spent regardless of what
        // came back.
        await RecordHitAsync(RecordedEventKind.SourceQueryHit, cancellationToken).ConfigureAwait(false);

        return await InvokeAsync(
            () => _inner.SearchAsync(query, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
        // DELIBERATELY UNGATED AND UNCOUNTED. A caps fetch is configuration discovery, not a search
        // or a grab, and the operator's query and grab allowances are what this bead budgets. Gating
        // it would also break the Settings screen for exactly the source an operator is trying to
        // diagnose — the moment they most need to see what it supports is when it is backing off.
        => _inner.GetCapsAsync(protocol, cancellationToken);

    /// <inheritdoc />
    public async Task<Stream> FetchDownloadAsync(
        ReleaseCandidate release,
        CancellationToken cancellationToken = default)
    {
        if (!await IsCallableAsync(cancellationToken).ConfigureAwait(false)
            || !await _counter.HasGrabBudgetAsync(_configuration, cancellationToken).ConfigureAwait(false))
        {
            await RecordSkipAsync("grab", cancellationToken).ConfigureAwait(false);

            // A GRAB CANNOT BE SKIPPED SILENTLY, unlike a search. A search skip degrades to fewer
            // results and the merge still answers; a download has exactly one source and no other
            // way to succeed, so returning an empty stream would hand the client a zero-byte NZB
            // that looks like a valid one. SourceUnavailableException is the established shape for
            // "refused without touching upstream" and endpoints already answer it as a retryable
            // 503, which is the truthful status: the source is fine, it is out of allowance, and
            // trying later will work.
            throw new SourceUnavailableException(
                $"Source '{Name}' has reached its grab limit for the current window.");
        }

        await RecordHitAsync(RecordedEventKind.SourceGrabHit, cancellationToken).ConfigureAwait(false);

        return await InvokeAsync(
            () => _inner.FetchDownloadAsync(release, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> IsCallableAsync(CancellationToken cancellationToken)
        => _backoff.IsCallableAsync(Name, cancellationToken);

    /// <summary>
    /// Runs the wrapped call and resolves its outcome into the durable backoff state. The outcome
    /// classification is the load-bearing part: an authentication failure and a timeout look alike
    /// to a caller and must not be treated alike here — one is permanently disabling, the other
    /// escalates.
    /// </summary>
    private async Task<T> InvokeAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            await _backoff.RecordOutcomeAsync(Name, SourceCallOutcome.Success, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, or the client went away. Neither is evidence about the source, so recording
            // a failure here would escalate a healthy indexer every time a user navigated away.
            throw;
        }
        catch (Exception ex)
        {
            await _backoff.RecordOutcomeAsync(Name, ClassifyOutcome(ex), cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Which outcome an exception represents.
    ///
    /// <para>A 401 or a 403 is the ONLY permanently-disabling shape, and it is matched on the status
    /// code rather than on message text — text is upstream-supplied, varies per indexer and would
    /// make the rule depend on wording nobody controls.</para>
    ///
    /// <para><see cref="SourceUnavailableException"/> is deliberately NOT a fault here: it means the
    /// call was refused before touching upstream (the breaker was already open, or this decorator
    /// refused a grab above), so counting it would escalate a source for a decision Arbitarr itself
    /// made and would compound one refusal into a longer one.</para>
    /// </summary>
    private static SourceCallOutcome ClassifyOutcome(Exception exception) => exception switch
    {
        SourceUnavailableException => SourceCallOutcome.Success,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
            => SourceCallOutcome.AuthenticationFailure,
        _ => SourceCallOutcome.TransientFailure,
    };

    private ValueTask RecordHitAsync(RecordedEventKind kind, CancellationToken cancellationToken)
        // Summary, Reason and Detail are all per-SOURCE constants, never per-occurrence values. That
        // is what lets these rows FOLD onto one another, which is the shape the budget counts by
        // summing RepeatCount. Rendering a timestamp or a sequence number into any of them would
        // defeat folding and break it for every other consumer of the event store — see ADR 0020.
        => _eventSink.RecordAsync(
            kind,
            kind == RecordedEventKind.SourceQueryHit
                ? "Queried an upstream source"
                : "Fetched a download from an upstream source",
            reason: null,
            sourceDisplayName: Name,
            detail: null,
            cancellationToken: cancellationToken);

    private ValueTask RecordSkipAsync(string operation, CancellationToken cancellationToken)
        // A SourceFailed with a NULL SourceDisplayName. The null is deliberate and load-bearing: a
        // populated name arms NotificationPolicy.FoldSourceFailure's consecutive-failure counter and
        // would notify the operator that a source is DOWN when it is merely budgeted or resting —
        // the precise conflation ADR 0020 says must not happen. DownloadProxyEndpoint relies on the
        // same property for the same reason. The source is named in the Reason instead, so the
        // Activity surface can still say which one and why.
        => _eventSink.RecordAsync(
            RecordedEventKind.SourceFailed,
            $"Skipped an upstream {operation}",
            reason: $"Source '{Name}' is at its limit or backing off",
            sourceDisplayName: null,
            detail: null,
            cancellationToken: cancellationToken);
}
