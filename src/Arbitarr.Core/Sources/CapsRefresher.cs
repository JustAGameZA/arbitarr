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
/// </summary>
public sealed class CapsRefresher
{
    /// <summary>
    /// Every protocol family a refresh covers. See the type doc: refreshing one family only would
    /// silently leave the other's stored entry stale behind a successful-looking refresh.
    /// </summary>
    private static readonly SearchProtocol[] AllProtocols =
        [SearchProtocol.Torznab, SearchProtocol.Newznab];

    private readonly ICapsCacheStore _cacheStore;

    public CapsRefresher(ICapsCacheStore cacheStore)
    {
        _cacheStore = cacheStore ?? throw new ArgumentNullException(nameof(cacheStore));
    }

    /// <summary>
    /// Refreshes one source's stored caps for both protocol families, returning one outcome per
    /// family in <see cref="AllProtocols"/> order.
    /// </summary>
    public async Task<IReadOnlyList<CapsRefreshOutcome>> RefreshAsync(
        IUpstreamSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var outcomes = new List<CapsRefreshOutcome>(AllProtocols.Length);

        foreach (var protocol in AllProtocols)
        {
            outcomes.Add(await RefreshOneAsync(source, protocol, cancellationToken).ConfigureAwait(false));
        }

        return outcomes;
    }

    /// <summary>
    /// Refreshes every given source's stored caps, for both protocol families. Sources are walked in
    /// order and one source's failure never stops the next — the background pass must still refresh
    /// the reachable indexers when one is down, which is the case it exists for.
    /// </summary>
    public async Task<IReadOnlyList<CapsRefreshOutcome>> RefreshAllAsync(
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var outcomes = new List<CapsRefreshOutcome>(sources.Count * AllProtocols.Length);

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
    /// the upstream did not answer. The one exception NOT swallowed is a cancellation of the
    /// caller's own token — a stopping host or an aborted request is not an upstream failure, and
    /// reporting it as one would show the operator a false "indexer unreachable".
    /// </remarks>
    private async Task<CapsRefreshOutcome> RefreshOneAsync(
        IUpstreamSource source,
        SearchProtocol protocol,
        CancellationToken cancellationToken)
    {
        try
        {
            var caps = await source.GetCapsAsync(protocol, cancellationToken).ConfigureAwait(false);
            await _cacheStore
                .SaveAsync(CapsAggregator.CacheKey(source.Name, protocol), caps, cancellationToken)
                .ConfigureAwait(false);
            return new CapsRefreshOutcome(source.Name, protocol, Refreshed: true);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested is false)
        {
            return new CapsRefreshOutcome(source.Name, protocol, Refreshed: false);
        }
    }
}
