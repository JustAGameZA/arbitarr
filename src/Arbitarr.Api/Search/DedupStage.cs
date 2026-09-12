using Arbitarr.Api.Rendering;
using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Search;

/// <summary>
/// <see cref="IDedupStage"/>'s implementation (arb-x7w8.8), per
/// <c>docs/adr/0019-dedup-is-a-pipeline-stage-with-conservative-exact-merge.md</c>. Collapses the
/// copies of one release that arrive from several sources into a single <b>dedup group</b>,
/// represented as the group's first member carrying the rest on
/// <see cref="RenderedRelease.AlternateMembers"/>.
///
/// <para><b>It runs over every source, including NZBHydra2.</b> No adapter deduplicates its own
/// results: an adapter sees only its own indexer, so adapter-level dedup could only collapse one
/// indexer's duplicates of itself, which is the case that does not arise. The case that does arise
/// spans two sources — an operator running Hydra and a direct indexer gets the same release
/// twice — and only a stage over the merged fan-out can see it. Hydra having already deduplicated
/// its own output makes running this over those results harmless under an equality rule, not
/// redundant.</para>
///
/// <para><b>Two releases merge only when ALL THREE conditions hold. This is exact evidence, never
/// similarity</b> — no edit distance, no token overlap, no score threshold. ADR 0019 chose it over
/// NZBHydra2's fuzzy grouping because the two errors do not cost the same: a <b>false split</b>
/// shows the operator a duplicate row, which is visible, cheap and self-correcting, while a
/// <b>false merge</b> hides one release behind another, possibly better one with nothing reported.
/// That is ADR 0002's wrong-answer shape. <b>Two rows for one release is therefore the expected
/// failure mode and must not be "fixed" by loosening the match</b>; introducing similarity scoring,
/// a fuzzy title distance, or a cross-protocol merge reverses ADR 0019 and needs an ADR superseding
/// it, not a tuning commit.</para>
///
/// <para><b>Groups are ordered, not reduced</b>, so a downstream consumer sees a group of N members
/// and must not assume N is 1. The losers are never discarded — ADR 0003's de-rank-never-discard on
/// a different axis: the ordering carries the preference, the set carries the options, so a failed
/// grab from the first member can fall back to the next.</para>
/// </summary>
public sealed class DedupStage : IDedupStage
{
    /// <summary>
    /// How far two reported sizes for the same file may differ and still merge: <b>one part in
    /// 1000</b> of the larger of the two, floored at <see cref="MinimumSizeToleranceBytes"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Relative, because the disagreement this absorbs scales with the file.</b> Indexers
    /// describing one release report sizes that differ slightly — one counts the NZB's par2 volumes
    /// or a torrent's padding files, another does not, and some round to whole mebibytes before
    /// publishing. Those differences are a fraction of the payload, not a fixed number of bytes: a
    /// tolerance tight enough to be safe on a 200 MB episode is far too tight on a 60 GB remux, and
    /// one loose enough for the remux would be 300 MB wide on the episode — enough to merge a 720p
    /// release with a different 720p release of the same title.</para>
    ///
    /// <para><b>One part in 1000 is chosen to sit below the smallest real quality step.</b> Two
    /// genuinely different releases of one title differ by a quality or encode decision, and the
    /// narrowest such gap in practice is percent-scale, not per-mille — so a per-mille band cannot
    /// span it, while comfortably covering rounding and par2 accounting. Tighter would split true
    /// duplicates for no benefit; looser starts approaching the gap between real releases, which is
    /// the expensive direction.</para>
    ///
    /// <para>Taken against the LARGER of the two sizes so the relation is symmetric: computing it
    /// against whichever operand happened to be first would make "A merges with B" depend on
    /// enumeration order, and a matching rule may not have that property.</para>
    ///
    /// <para><b>Pinned on both sides</b> by <c>DedupStageTests</c> — a pair just inside the band
    /// merges and a pair just outside it does not — so a change to this constant cannot pass
    /// unnoticed.</para>
    /// </remarks>
    public const double RelativeSizeTolerance = 0.001;

    /// <summary>
    /// The floor under <see cref="RelativeSizeTolerance"/>: 64 KiB, applied when one part in 1000
    /// would be smaller than that.
    /// </summary>
    /// <remarks>
    /// A pure ratio collapses toward zero on small payloads — on a 1 MB release it is 1 KB, which is
    /// narrower than a single par2 block, so two indexers' honest accounts of one small file would
    /// split. The floor keeps the rule meaningful there. It is small enough in absolute terms that
    /// it cannot bridge two different releases of anything, since a payload where 64 KiB is a
    /// significant fraction is too small to have quality variants at all.
    /// </remarks>
    public const long MinimumSizeToleranceBytes = 64 * 1024;

    private readonly ISourcePriorityLookup _priorities;

    /// <param name="priorities">
    /// Name→priority lookup used to order each group's members. Required rather than optional: a
    /// group's order IS the preference it expresses, so a stage constructed without a stated
    /// ordering policy would be silently arbitrary. <see cref="AllEqualSourcePriority.Instance"/> is
    /// the explicit way to say "no opinion".
    /// </param>
    public DedupStage(ISourcePriorityLookup priorities)
    {
        _priorities = priorities ?? throw new ArgumentNullException(nameof(priorities));
    }

    public string Name => "Dedup";

    /// <summary>
    /// <see cref="IPipelineStage"/> conformance: candidates in, same candidates out unchanged.
    /// </summary>
    /// <remarks>
    /// The real work is <see cref="Deduplicate"/>, which needs the per-release source NAME to order
    /// a group by priority. That name lives on <see cref="RenderedRelease"/> and not on
    /// <see cref="ReleaseCandidate"/> (ADR 0019 states this precondition explicitly), so the base
    /// contract's signature cannot carry it. <see cref="UpstreamMergeStage.ProcessAsync"/> is a
    /// passthrough for the same reason.
    /// </remarks>
    public Task<IReadOnlyList<ReleaseCandidate>> ProcessAsync(
        IReadOnlyList<ReleaseCandidate> candidates,
        CancellationToken cancellationToken = default) => Task.FromResult(candidates);

    /// <summary>
    /// Groups <paramref name="releases"/> and returns one <see cref="RenderedRelease"/> per group —
    /// the highest-priority member, carrying the rest on
    /// <see cref="RenderedRelease.AlternateMembers"/>. Groups appear in the order their first
    /// member appeared in the input, so a single-source result set comes back in exactly the order
    /// it went in.
    /// </summary>
    /// <remarks>
    /// <para><b>Grouping is done by scanning, not by hashing on a composite key, and that is forced
    /// by the size condition.</b> "Within a tolerance" is not transitive, so it has no equality key:
    /// A can match B and B match C while A and C are a tolerance apart. A dictionary keyed on
    /// (title, protocol, quantised size) would have to pick bucket boundaries, and two sizes either
    /// side of one boundary would fail to merge however close they were. Scanning compares each
    /// candidate against the groups already formed, which is O(n·groups) — bounded by a single
    /// search's page of results, so the cost is not a concern at this size.</para>
    ///
    /// <para>A candidate joins the FIRST group it matches. With a non-transitive relation the
    /// grouping does depend on input order in principle; first-match makes that dependence
    /// deterministic rather than leaving it to a hash order, and the merge output it reads is itself
    /// order-stable.</para>
    /// </remarks>
    public IReadOnlyList<RenderedRelease> Deduplicate(IReadOnlyList<RenderedRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);

        if (releases.Count < 2)
        {
            // Nothing can merge with itself; returning the input untouched also keeps a
            // single-source deployment byte-identical to its pre-dedup behaviour.
            return releases;
        }

        var groups = new List<List<RenderedRelease>>();

        foreach (var release in releases)
        {
            var joined = false;

            foreach (var group in groups)
            {
                // Compared against the group's FIRST member (the one the group was opened with),
                // not against every member: the first member is the group's identity, and testing
                // against all of them would let a chain of pairwise-tolerable sizes drift the group
                // arbitrarily far from where it started — which is the fuzzy merging ADR 0019
                // refuses, arrived at by transitivity instead of by a threshold.
                if (AreSameRelease(group[0], release))
                {
                    group.Add(release);
                    joined = true;
                    break;
                }
            }

            if (joined is false)
            {
                groups.Add(new List<RenderedRelease> { release });
            }
        }

        var result = new List<RenderedRelease>(groups.Count);

        foreach (var group in groups)
        {
            if (group.Count == 1)
            {
                result.Add(group[0]);
                continue;
            }

            var ordered = OrderMembers(group);

            // The representative is the first member, and it carries the others. Its own
            // AlternateMembers is rebuilt from scratch here rather than appended to, so a group's
            // members stay flat and no reader has to recurse.
            result.Add(ordered[0] with { AlternateMembers = ordered.Skip(1).ToArray() });
        }

        return result;
    }

    /// <summary>
    /// Orders a group's members by source priority (higher first), ties broken by source name
    /// (ordinal), then by the order the merge produced.
    /// </summary>
    /// <remarks>
    /// The two tiebreaks exist so the order is total and reproducible: priority alone leaves every
    /// unranked source (all of them under <see cref="AllEqualSourcePriority"/>) tied, and an
    /// unstable order there would make the presented member vary between two identical searches.
    /// Source name is compared with <see cref="StringComparer.Ordinal"/> rather than a
    /// culture-aware comparer for the same reason <see cref="DedupNormalizer"/> folds invariantly —
    /// the presented result must not depend on the host's locale. The final fallback to input
    /// position handles two releases from ONE source that matched each other.
    /// </remarks>
    private List<RenderedRelease> OrderMembers(List<RenderedRelease> group) =>
        group
            .Select((release, index) => (Release: release, Index: index))
            .OrderByDescending(m => _priorities.PriorityOf(m.Release.SourceName))
            .ThenBy(m => m.Release.SourceName, StringComparer.Ordinal)
            .ThenBy(m => m.Index)
            .Select(m => m.Release)
            .ToList();

    /// <summary>
    /// ADR 0019's three conditions, all of which must hold. Deliberately not a partial or weighted
    /// match: there is no score here to tune.
    /// </summary>
    internal static bool AreSameRelease(RenderedRelease left, RenderedRelease right) =>
        SameKnownProtocol(left.Candidate.Protocol, right.Candidate.Protocol)
        && string.Equals(
            DedupNormalizer.Normalize(left.Candidate.Title),
            DedupNormalizer.Normalize(right.Candidate.Title),
            StringComparison.Ordinal)
        && SizesWithinTolerance(left.Candidate.Size, right.Candidate.Size);

    /// <summary>
    /// Condition 3: the same <see cref="ProtocolKind"/>, with <b><see cref="ProtocolKind.Unknown"/>
    /// matching nothing, including another <see cref="ProtocolKind.Unknown"/></b>.
    /// </summary>
    /// <remarks>
    /// <b>The Unknown-vs-Unknown refusal is the load-bearing half and reads like a bug if the
    /// reasoning is not stated.</b> <see cref="ProtocolKind.Unknown"/> is the DEFAULT — it is what a
    /// candidate holds when its source did not report a protocol — so two candidates that are both
    /// Unknown share only a missing field. A missing field is not evidence of being the same
    /// artifact, and treating it as one would make an under-populated candidate the easiest thing in
    /// the system to merge. Refusing it means a missing field can never become an accidental match.
    /// Even NZBHydra2's fuzzy implementation declines to reason across delivery protocols (its
    /// duplicate detection excludes torrents outright), which points the same way.
    /// </remarks>
    private static bool SameKnownProtocol(ProtocolKind left, ProtocolKind right) =>
        left is not ProtocolKind.Unknown && left == right;

    /// <summary>
    /// Condition 2: sizes within <see cref="RelativeSizeTolerance"/> of the larger of the two, never
    /// tighter than <see cref="MinimumSizeToleranceBytes"/>. The comparison is inclusive, so a pair
    /// exactly at the boundary merges — the tests pin which side that is.
    /// </summary>
    private static bool SizesWithinTolerance(long left, long right)
    {
        var difference = Math.Abs(left - right);
        var larger = Math.Max(left, right);
        var tolerance = Math.Max(MinimumSizeToleranceBytes, (long)(larger * RelativeSizeTolerance));
        return difference <= tolerance;
    }
}
