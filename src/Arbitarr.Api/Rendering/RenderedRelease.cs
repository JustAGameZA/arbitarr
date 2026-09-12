using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Pairs a merged <see cref="ReleaseCandidate"/> with the upstream source name that produced
/// it, so the renderer can compute a stable <see cref="ReleaseGuid"/> (source name + upstream
/// guid) without the pipeline needing to thread source identity through the candidate itself.
/// </summary>
/// <param name="SourceName">Name of the upstream source that produced this release.</param>
/// <param name="Candidate">The release payload, rendered byte-exact (pure passthrough).</param>
/// <param name="SuppressionAnnotation">
/// Additive metadata only (M4-7) — never mutates <paramref name="Candidate"/>'s title, size,
/// category, or guid (M1-4). Null for a release that was never suppressed. When shadow mode
/// re-admits a would-be-suppressed release, this carries a human-readable reason so the response
/// still shows the release was matched by a suppression source, just not enforced.
/// </param>
/// <param name="AlternateMembers">
/// The rest of this release's <b>dedup group</b> (arb-x7w8.8): the other sources' copies of the
/// same release, which <c>DedupStage</c> matched on equal normalised title, size within tolerance
/// and the same known protocol. Empty for a release that merged with nothing, which is every
/// release on a single-source deployment — so this is additive in exactly the way
/// <paramref name="SuppressionAnnotation"/> is, and every pre-existing call site keeps its
/// behaviour byte for byte.
///
/// <para><b>The losers are carried here rather than discarded, and that is the point.</b>
/// <c>docs/adr/0019-dedup-is-a-pipeline-stage-with-conservative-exact-merge.md</c> applies ADR
/// 0003's de-rank-never-discard to this axis: the ordering carries the preference, the set carries
/// the options. Dropping them would express the preference by destroying the alternative.</para>
///
/// <para><b>The fallback grab is NOT reachable yet, and this note must not be read as saying it
/// is.</b> Retaining the members is what makes it possible to OFFER one later; it does not offer
/// one today, because <c>SearchEndpoint</c> registers only each group's representative in the
/// release lookup, so a member's <see cref="ProxyGuid"/> resolves to nothing at the download proxy.
/// Registering members is a follow-up (its own bead), deliberately not done here. What ships is the
/// retention and the ordering; the affordance that consumes them is the next step.</para>
///
/// <para>Held on the representative rather than as a side record because the source name dedup
/// orders by already lives here, not on <see cref="ReleaseCandidate"/>, and because every
/// downstream reader (renderer, release lookup, download proxy) already holds a
/// <c>RenderedRelease</c> — a parallel structure would need threading through all of them. The
/// members are themselves <c>RenderedRelease</c>, so each carries its own source name and its own
/// computed <see cref="ProxyGuid"/> — the identity a proxy would need to grab it, once members are
/// registered.</para>
///
/// <para><b>Members are flat, never nested.</b> A member's own <see cref="AlternateMembers"/> is
/// always empty: <c>DedupStage</c> builds each group from the ungrouped merge output in one pass,
/// so there is no second level to walk and no reader has to recurse.</para>
/// </param>
public sealed record RenderedRelease(
    string SourceName,
    ReleaseCandidate Candidate,
    string? SuppressionAnnotation = null,
    IReadOnlyList<RenderedRelease>? AlternateMembers = null)
{
    /// <summary>
    /// The other members of this release's dedup group, ordered by source priority — never null,
    /// so a reader can enumerate it without a null check the way it already can with
    /// <see cref="ReleaseCandidate.Category"/>.
    /// </summary>
    public IReadOnlyList<RenderedRelease> AlternateMembers { get; init; } =
        AlternateMembers ?? Array.Empty<RenderedRelease>();

    /// <summary>
    /// The stable proxy guid for this release, used by DownloadProxyEndpoint.
    ///
    /// <para><b>MATERIALISED ONCE PER INSTANCE, NOT A COMPUTED PROPERTY (arb-0hd0).</b> This was
    /// <c>=> ReleaseGuid.Compute(...)</c>, re-evaluated on every access — five times per search
    /// request (FilterStage, InMemoryReleaseLookup's memory key, SearchEndpoint's persisted key,
    /// the URL handed to the client, IndexerXmlWriter). <see cref="ReleaseGuid"/>'s HMAC secret is
    /// a mutable process-global that <c>ReleaseGuid.Configure</c> rewrites, so those five
    /// evaluations were not guaranteed to agree: a <c>Configure</c> landing between the lookup-key
    /// evaluation and the URL evaluation issued a link matching NEITHER tier, and the download came
    /// back 404 with no exception and no log entry (arb-agh, four occurrences). Computing it once
    /// makes all five sites share one value BY CONSTRUCTION rather than by the secret happening to
    /// hold still, and drops four HMAC computations per release as a side effect.</para>
    ///
    /// <para>Initialised in the initialiser rather than via a lazily-populated backing field, and
    /// that choice is load-bearing for <c>with</c>-expressions. A record's <c>with</c> copies
    /// fields and then runs the initialisers for what it sets; a cached-on-first-access field would
    /// be COPIED, so <c>release with { Candidate = other }</c> would carry the original's guid
    /// while reporting a different candidate. Here the initialiser re-runs on every copy, so the
    /// value always tracks the current <see cref="SourceName"/> and <see cref="Candidate"/>.
    /// FilterStage rewrites titles with exactly that expression (<c>with { Candidate = ... }</c>);
    /// it happens to preserve the upstream guid today, but the correctness of this type must not
    /// rest on that staying true.</para>
    ///
    /// <para><c>ProxyGuid</c> is not a positional member, so it is not part of the generated
    /// <c>Equals</c>/<c>GetHashCode</c> — computing it here does not itself affect equality.</para>
    ///
    /// <para><b>Record equality is nonetheless NOT structural on this type, and relying on it is a
    /// trap.</b> There are now FOUR positional components, and the fourth,
    /// <see cref="AlternateMembers"/>, is an <c>IReadOnlyList&lt;RenderedRelease&gt;</c> — a
    /// reference type with no value semantics — so the synthesized <c>Equals</c> compares it by
    /// REFERENCE. Two structurally identical grouped releases, each holding an equal but distinct
    /// list, are therefore <b>not equal</b>, and a JSON round trip produces exactly that shape: the
    /// deserialized copy has a fresh list instance. An ungrouped release compares as it always did,
    /// since every empty group shares <c>Array.Empty&lt;RenderedRelease&gt;()</c>, which is why this
    /// was easy to miss. Compare grouped releases field-wise (as
    /// <c>SearchResultRefresherTests</c> does), never with <c>==</c>.</para>
    /// </summary>
    public string ProxyGuid { get; } = ReleaseGuid.Compute(new ReleaseIdentity(SourceName, Candidate.Guid));
}
