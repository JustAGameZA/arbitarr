using Arbitarr.Api.Rendering;
using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// Merge-stage implementation of <see cref="IMergeStage"/> (Arbitarr.Core.Pipeline): fans a
/// <see cref="SearchQuery"/> out to every configured <see cref="IUpstreamSource"/> in parallel
/// and unions the resulting <see cref="ReleaseCandidate"/> sets, tagging each with its
/// originating source name for later guid computation. This is the only pipeline stage M1
/// implements; Identity/Match/Dedup/Filter remain contract-only until later milestones.
///
/// A source whose search fails with <see cref="RequestLimitReachedException"/> is recorded so
/// the caller can surface a Torznab/Newznab rate-limit element (M1-9) instead of losing the
/// whole merged response; any other per-source failure is treated the same way (that source's
/// results are simply omitted from the union — zero renderer changes, M1-10).
///
/// arb-x7w8.7 extends that principle to the other failure kinds rather than replacing it. With ONE
/// source, dropping a non-rate-limit failure silently still read correctly enough as "nothing
/// matched". With N sources it does not: four of six indexers being down must not be
/// indistinguishable from six indexers agreeing there is nothing. So EVERY per-source failure is now
/// NAMED — a timeout in <see cref="MergeResult.TimedOutSources"/>, anything else in
/// <see cref="MergeResult.FailedSources"/>, alongside the existing
/// <see cref="MergeResult.RateLimitedSources"/> — and the merge proceeds with the survivors.
///
/// None of the three escalate OUT OF THIS STAGE. Non-escalation is the STAGE's invariant, not the
/// pipeline's: MergeAsync never throws on a per-source failure, so a partial merge always reaches
/// its caller as data rather than as an exception. That is deliberately weaker than "a partial
/// merge is always HTTP 200". The endpoint remains free to classify what it receives — in
/// particular a TOTAL failure, where no source answered at all — as an infrastructure error and
/// render 900/5xx. What this stage guarantees is only that the choice is the endpoint's, taken
/// with every source's outcome in hand, rather than pre-empted by an exception escaping the
/// fan-out (CONTEXT.md's protocol-answer vs infrastructure-error distinction;
/// <c>SearchEndpoint.InfrastructureErrorResult</c>'s remarks carry the full reasoning).
/// </summary>
public sealed class UpstreamMergeStage : IMergeStage
{
    /// <summary>
    /// arb-x7w8.4: the sources are RESOLVED per merge rather than injected as a fixed list. The
    /// source set now lives in the database and an operator's edit must take effect on the next
    /// request rather than the next restart — see <see cref="ISourceRegistry"/> for why that
    /// resolution is async and therefore cannot happen in this constructor.
    /// </summary>
    private readonly ISourceRegistry _registry;

    private static readonly IReadOnlyList<ReleaseCandidate> Empty = Array.Empty<ReleaseCandidate>();

    public UpstreamMergeStage(ISourceRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string Name => "UpstreamMerge";

    /// <summary>
    /// <see cref="IPipelineStage"/> conformance: candidates in, same candidates out unchanged.
    /// The actual fan-out/merge happens in <see cref="MergeAsync"/>, which additionally needs
    /// the originating <see cref="SearchQuery"/> and per-source identity that the base
    /// contract's signature doesn't carry.
    /// </summary>
    public Task<IReadOnlyList<ReleaseCandidate>> ProcessAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        CancellationToken cancellationToken = default) => Task.FromResult(candidates);

    /// <summary>Fans <paramref name="query"/> out to all sources and returns the merged, source-tagged union.</summary>
    public async Task<MergeResult> MergeAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Resolved here, at the top of the work, rather than in the constructor: this is the await
        // that a constructor-injected list could not have. Within one request the registry memoises,
        // so a second consumer in the same scope does not re-read the rows or the keys.
        var sources = await _registry.ResolveAsync(cancellationToken).ConfigureAwait(false);

        var tasks = sources.Select(async source =>
        {
            // WHY NO PER-SOURCE CancellationTokenSource HERE (lead's ruling, 2026-09-16). Each
            // adapter's HttpClient.Timeout is already built from that source's own TimeoutSeconds
            // row at resolution time (Arbitarr.Host's SourceRegistry). IUpstreamSource exposes only
            // Name, so a CTS built here could not read that budget without widening the contract —
            // it would have to invent a second, different number, and two competing bounds on the
            // same call is worse than one correct one.
            //
            // Be precise about what that existing bound covers, though: HttpClient.Timeout bounds
            // ONE HTTP REQUEST, not one search leg. A leg can outlive it by an unbounded multiple,
            // because three things sit outside any single request's clock:
            //   1. the page loop — NewznabSource issues up to MaxUpstreamCallsPerSearch sequential
            //      requests per SearchAsync, each getting the full timeout afresh;
            //   2. the rate-limiter wait, which is time spent before a request is issued at all and
            //      so is not inside any request's timeout;
            //   3. the budget decorator's DB scopes (BudgetedUpstreamSource's hit-counting and
            //      backoff reads/writes), which bracket the call rather than being part of it.
            // So what the fan-out owes today is ISOLATION, not a third timeout: whichever bound
            // fires, one source hitting it must not drop the others' results and must not vanish
            // unnamed. A genuine whole-fan-out ceiling — the only thing that would actually bound a
            // leg — is a follow-up bead (arb-4cso, arch-461 B3).
            try
            {
                var results = await source.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                return (Source: source, Results: results, Outcome: SourceOutcome.Succeeded);
            }
            // CLAUSE ORDER IS LOAD-BEARING: RequestLimitReachedException must be matched BEFORE the
            // OperationCanceledException arm. Reordering them silently reclassifies rate limits.
            catch (RequestLimitReachedException)
            {
                return (Source: source, Results: Empty, Outcome: SourceOutcome.RateLimited);
            }
            // The source's own budget fired. HttpClient.Timeout surfaces as a TaskCanceledException
            // (an OperationCanceledException) carrying the token that cancelled it.
            //
            // BOTH halves of this filter are load-bearing, and the identity half CANNOT stand alone.
            // An adapter bounds its own call by LINKING its budget to the caller's token (this is
            // what HttpClient does internally, and what the test fake models), so the token the
            // exception carries is the LINKED one in either case — it is never reference-equal to
            // the caller's raw token even when the caller is exactly who cancelled. Identity alone
            // therefore matches a genuine caller cancellation too and silently absorbs it, turning
            // an abandoned request into a fabricated per-source timeout; the caller-token guard is
            // what rules that case out. Identity then adds what the guard alone cannot: it keeps a
            // source's own budget classified as TimedOut rather than Failed in the window where the
            // caller's cancel lands between the throw and the filter.
            catch (OperationCanceledException ex)
                when (cancellationToken.IsCancellationRequested is false
                    && ex.CancellationToken != cancellationToken)
            {
                return (Source: source, Results: Empty, Outcome: SourceOutcome.TimedOut);
            }
            // Transport, protocol, parse — anything else. Previously this clause discarded the
            // failure entirely; it now records the name and nothing more, because the merge's answer
            // to a broken source is still "carry on without it". This one keeps the
            // IsCancellationRequested form on purpose: Exception carries no CancellationToken to
            // take the identity of, so the narrower test is not merely worse here, it is inexpressible
            // without first narrowing the type — which the clause above already does.
            catch (Exception) when (cancellationToken.IsCancellationRequested is false)
            {
                return (Source: source, Results: Empty, Outcome: SourceOutcome.Failed);
            }
        }).ToArray();

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);

        var merged = completed
            .SelectMany(t => t.Results.Select(r => new RenderedRelease(t.Source.Name, r)))
            .ToArray();

        return new MergeResult(
            merged,
            NamesWith(completed, SourceOutcome.RateLimited),
            NamesWith(completed, SourceOutcome.TimedOut),
            NamesWith(completed, SourceOutcome.Failed));
    }

    private static IReadOnlyList<string> NamesWith(
        IEnumerable<(IUpstreamSource Source, IReadOnlyList<ReleaseCandidate> Results, SourceOutcome Outcome)> completed,
        SourceOutcome outcome) =>
        completed.Where(t => t.Outcome == outcome).Select(t => t.Source.Name).ToArray();

    /// <summary>
    /// How one source's leg of the fan-out ended. Kept private to the stage: it is the classifier
    /// that decides which of <see cref="MergeResult"/>'s three name lists a source lands in, not a
    /// shape any caller consumes — callers read the lists.
    /// </summary>
    private enum SourceOutcome
    {
        Succeeded,
        RateLimited,
        TimedOut,
        Failed,
    }
}

/// <summary>
/// Result of an upstream fan-out/merge: the unioned releases, plus the name of every source that
/// did not contribute, separated by why.
/// </summary>
/// <remarks>
/// The three failure lists are DISJOINT and each source appears in at most one of them — a source
/// that contributed appears in none. Together with <paramref name="Releases"/>' source tags they
/// let a caller tell "no source had it" from "no source answered".
///
/// They do NOT, however, account for every source the registry resolved. Since #340 a source can be
/// SKIPPED by the budget/backoff gate — its API-hit budget exhausted, it is backing off, or it has
/// been permanently disabled — and <c>BudgetedUpstreamSource</c> then returns an empty list
/// SUCCESSFULLY rather than throwing. Such a source contributes no releases, so it carries no source
/// tag, and it failed in none of the three ways, so it appears in no list either: from this record
/// alone it is indistinguishable from a source that answered and had nothing. Its distinction is
/// carried outside this type, by the <c>SourceSkipped</c> event the decorator records. Folding it
/// back in — a fourth list, so the shape is once again total over the resolved set — is a follow-up
/// bead (arb-9ael, filed from arch-461).
///
/// A non-empty failure list is never on its own grounds for an error response: see
/// <see cref="UpstreamMergeStage"/>'s remarks and CONTEXT.md.
/// </remarks>
/// <param name="Releases">Merged, source-tagged release set (union across all responding sources).</param>
/// <param name="RateLimitedSources">Names of sources that failed with <see cref="RequestLimitReachedException"/>.</param>
/// <param name="TimedOutSources">
/// Names of sources whose search ran out of time under their own budget (the adapter's
/// <c>HttpClient.Timeout</c>, built from that source's <c>TimeoutSeconds</c> row). Excludes the
/// caller abandoning the whole request, which is not a source's fault and is not caught here.
/// </param>
/// <param name="FailedSources">
/// Names of sources that failed any other way — transport, protocol, parse. Before arb-x7w8.7 these
/// were discarded with no record at all, which is precisely what made a half-down install read as
/// an empty result set.
/// </param>
public sealed record MergeResult(
    IReadOnlyList<RenderedRelease> Releases,
    IReadOnlyList<string> RateLimitedSources,
    IReadOnlyList<string> TimedOutSources,
    IReadOnlyList<string> FailedSources);
