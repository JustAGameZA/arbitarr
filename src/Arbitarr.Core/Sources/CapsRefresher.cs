namespace Arbitarr.Core.Sources;

/// <summary>
/// The outcome of refreshing one source's caps for one protocol family: whether the upstream
/// answered, and for which source and protocol. Deliberately carries NO caps payload and no
/// failure detail.
/// </summary>
/// <remarks>
/// <para><b>Why no payload and no error text.</b> This record is what the admin refresh route
/// returns to the operator. Caps themselves are served from the store on the search path, so a
/// caller has no use for them here, and the exception a failed fetch produces is upstream-supplied
/// text that can carry the request URI — which, for these adapters, carries the source's API key in
/// its query string (CLAUDE.md §1: the framework redacts a query string only in ITS OWN log line,
/// not in an exception message this could interpolate). A bool and two enum-ish values cannot carry
/// a credential whatever a future edit does to the fetch path.</para>
/// </remarks>
/// <param name="SourceName">The <see cref="IUpstreamSource.Name"/> the refresh was attempted for.</param>
/// <param name="Protocol">Which protocol family's caps endpoint was asked.</param>
/// <param name="Refreshed">
/// True when the upstream answered and the stored last-known-good entry was replaced. False means
/// the fetch failed and the PREVIOUS stored entry — if any — is untouched, which is the whole point
/// of the store: a failed refresh must never be able to blank a working entry.
/// </param>
public sealed record CapsRefreshOutcome(string SourceName, SearchProtocol Protocol, bool Refreshed);

/// <summary>
/// arb-x7w8.5: fetches per-source caps from the upstreams and writes them to
/// <see cref="ICapsCacheStore"/>, so the search and caps paths can serve a near-static value from
/// the store instead of making a live call per request.
///
/// <para><b>WHY THIS EXISTS SEPARATELY FROM <see cref="CapsAggregator"/>.</b> The aggregator answers
/// "what do I advertise right now", merging across sources and falling back per source. This answers
/// "go ask the upstreams again and write down what they said" for ONE source or for all of them, and
/// reports per source whether the upstream answered. Those are different questions with different
/// callers — the add-indexer flow and the per-indexer refresh action both concern a single source
/// that has no merged answer to give, and the background pass concerns every source with no caller
/// waiting on a merge at all.</para>
///
/// <para><b>BOTH PROTOCOL FAMILIES, ALWAYS, and that is not thoroughness for its own sake.</b> The
/// store is keyed by <see cref="CapsAggregator.CacheKey"/>, which scopes the entry by protocol
/// (#99) because NZBHydra2 answers its two endpoints with different category sets. Refreshing only
/// one family would leave the other family's entry to go stale indefinitely while the operator has
/// just been told the refresh succeeded — and the stale half is exactly the half that a later
/// fallback would serve. Each family is refreshed independently: one endpoint being down does not
/// stop the other's entry being written.</para>
///
/// <para><b>A FAILED FETCH IS NOT AN ERROR HERE.</b> It is reported as
/// <see cref="CapsRefreshOutcome.Refreshed"/> false and nothing is written, leaving whatever was
/// stored before in place. Throwing instead would make one unreachable indexer fail the operator's
/// whole "add indexer" or background pass, which is the same failure mode the aggregator's
/// last-known-good fallback exists to prevent — undone one layer up.</para>
///
/// <para><b>EVERY SOURCE IS BOUNDED BY <see cref="DefaultPerSourceCeiling"/>.</b> A refresh happens
/// inline on the operator's create and update round trips, so an upstream that accepts a connection
/// and then never answers would hold that HTTP request open for as long as the per-row
/// <c>HttpClient.Timeout</c> allows — which is operator-configurable per source and therefore
/// unbounded from here. The ceiling covers BOTH protocol fetches for one source together, so the
/// cost of one source is bounded whatever its own timeout says, and a hit is reported as
/// not-refreshed exactly like any other fetch failure. See <see cref="RefreshAsync"/> for why the
/// ceiling is per source rather than per pass.</para>
/// </summary>
public sealed class CapsRefresher
{
    /// <summary>
    /// The default per-source ceiling: long enough for a healthy indexer on a slow LAN to answer
    /// both its caps endpoints, short enough that an operator saving a form against a dead address
    /// waits seconds rather than minutes. The same value and the same reasoning as
    /// <see cref="SourceConnectivityProber.DefaultTimeout"/>, which bounds the other operator-facing
    /// upstream call for the same reason.
    /// </summary>
    public static readonly TimeSpan DefaultPerSourceCeiling = TimeSpan.FromSeconds(10);

    private readonly ICapsCacheStore _cacheStore;
    private readonly TimeSpan _perSourceCeiling;

    /// <param name="perSourceCeiling">
    /// Overrides <see cref="DefaultPerSourceCeiling"/>. A parameter rather than a constant so a test
    /// can drive the give-up path in milliseconds instead of waiting out the real ceiling.
    /// </param>
    public CapsRefresher(ICapsCacheStore cacheStore, TimeSpan? perSourceCeiling = null)
    {
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
        _perSourceCeiling = perSourceCeiling ?? DefaultPerSourceCeiling;
    }

    /// <summary>
    /// Refreshes one source's stored caps for both protocol families, returning one outcome per
    /// family in <see cref="CapsAggregator.AllProtocols"/> order.
    ///
    /// <para><b>The ceiling is imposed here, once, spanning both fetches.</b> It is applied through a
    /// cancellation token linked to the caller's — the precedent
    /// <see cref="SourceConnectivityProber"/> sets, and for its reason: the adapter's
    /// <c>HttpClient</c> is shared and per-source configured, so its <c>Timeout</c> is not this
    /// type's to mutate. Per SOURCE rather than per protocol because the inline caller is waiting on
    /// the whole call: two protocols each bounded separately would still let one source cost twice
    /// the ceiling. Per source rather than per PASS because the background pass must still reach the
    /// reachable indexers when an early one is down — a whole-pass budget would let one dead upstream
    /// consume it and starve every source behind it. This is the deliberate opposite of
    /// <c>UpstreamMergeStage.DefaultFanOutCeiling</c>, which IS per PASS: that stage's legs run
    /// concurrently rather than sequentially, and a single caller is waiting on the whole fan-out
    /// rather than a background job working through sources one at a time.</para>
    ///
    /// <para>A ceiling hit is a not-refreshed outcome, never an exception, so the families it
    /// pre-empts report false and their previously stored entries stand. Caller cancellation is
    /// distinguished from the ceiling by the caller token's own state, exactly as the per-fetch catch
    /// filter does — misreporting a stopping host as an unreachable indexer would show the operator
    /// a failure that never happened.</para>
    /// </summary>
    public async Task<IReadOnlyList<CapsRefreshOutcome>> RefreshAsync(
        IUpstreamSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(_perSourceCeiling);

        var outcomes = new List<CapsRefreshOutcome>(CapsAggregator.AllProtocols.Count);

        foreach (var protocol in CapsAggregator.AllProtocols)
        {
            outcomes.Add(await RefreshOneAsync(source, protocol, ceiling.Token, cancellationToken)
                .ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>
    /// Refreshes every given source's stored caps, for both protocol families. Sources are walked in
    /// order and one source's failure never stops the next — the background pass must still refresh
    /// the reachable indexers when one is down, which is the case it exists for. Each source carries
    /// its own ceiling (see <see cref="RefreshAsync"/>), so a pass over N sources is bounded even
    /// when every one of them is a black hole.
    /// </summary>
    public async Task<IReadOnlyList<CapsRefreshOutcome>> RefreshAllAsync(
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var outcomes = new List<CapsRefreshOutcome>(sources.Count * CapsAggregator.AllProtocols.Count);

        foreach (var source in sources)
        {
            outcomes.AddRange(await RefreshAsync(source, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>
    /// One source, one protocol: fetch and store, or report the failure without writing.
    /// </summary>
    /// <remarks>
    /// The catch is broad because every adapter surfaces its failures as exceptions of its own
    /// choosing (an <c>HttpRequestException</c>, a timeout, a parse failure on a login page served
    /// where caps XML was expected), and this must treat all of them identically: do not write, say
    /// the upstream did not answer. The ceiling firing arrives here as one more of those — an
    /// <c>OperationCanceledException</c> on <paramref name="fetchToken"/> — and is deliberately
    /// treated the same way, because from the store's point of view an upstream that answered too
    /// late and one that did not answer are the same event: nothing to write.
    ///
    /// <para>The one exception NOT swallowed is a cancellation of the CALLER's own token, which is
    /// why the two tokens are separate parameters. The filter tests
    /// <paramref name="callerToken"/> and never <paramref name="fetchToken"/>: the latter is
    /// cancelled by our own ceiling as well, so filtering on it would rethrow the ceiling as if the
    /// host were stopping and turn a bounded give-up back into the unbounded failure this exists to
    /// prevent. A stopping host or an aborted request is not an upstream failure, and reporting it
    /// as one would show the operator a false "indexer unreachable".</para>
    /// </remarks>
    /// <param name="fetchToken">The ceiling-linked token the upstream call and the store write run under.</param>
    /// <param name="callerToken">The caller's own token — the sole basis for deciding a cancellation is theirs, not ours.</param>
    private async Task<CapsRefreshOutcome> RefreshOneAsync(
        IUpstreamSource source,
        SearchProtocol protocol,
        CancellationToken fetchToken,
        CancellationToken callerToken)
    {
        try
        {
            var caps = await source.GetCapsAsync(protocol, fetchToken).ConfigureAwait(false);
            await _cacheStore
                .SaveAsync(CapsAggregator.CacheKey(source.Name, protocol), caps, fetchToken)
                .ConfigureAwait(false);
            return new CapsRefreshOutcome(source.Name, protocol, Refreshed: true);
        }
        catch (Exception) when (callerToken.IsCancellationRequested is false)
        {
            return new CapsRefreshOutcome(source.Name, protocol, Refreshed: false);
        }
    }
}
