namespace Arbitarr.Core.Pipeline;

/// <summary>
/// Resolves an upstream source's name to its ordering weight, so <see cref="IDedupStage"/> can
/// order a dedup group's members without reading a priority off the candidates it is holding.
///
/// <para><b>This seam exists because the rank is not on the candidate.</b> As ADR 0019 records,
/// <c>UpstreamMergeStage</c> tags each candidate with its originating source <b>name</b>
/// (<c>RenderedRelease(source.Name, …)</c>) while the rank lives on <c>Source.Priority</c> in the
/// database. The dedup stage therefore needs a name→priority lookup; without one it would have to
/// reach into <c>Arbitarr.Data</c> from a matching rule, which is the dependency this interface
/// exists to avoid.</para>
/// </summary>
public interface ISourcePriorityLookup
{
    /// <summary>
    /// Returns the ordering weight of the source named <paramref name="sourceName"/>. <b>Higher
    /// wins</b>, matching <c>Source.Priority</c>'s documented direction. A name the lookup does not
    /// recognise must return the neutral default (zero) rather than throwing: dedup ordering is a
    /// presentation preference, and an unknown source name is not a reason to fail a search.
    /// </summary>
    int PriorityOf(string sourceName);
}

/// <summary>
/// The neutral <see cref="ISourcePriorityLookup"/>: every source reports priority zero, so a dedup
/// group's ordering falls through to the stage's documented tiebreaks (source name, then the order
/// the merge produced) and is still deterministic.
/// </summary>
/// <remarks>
/// It was the registered default in <c>Program.cs</c> so the dedup stage could be wired and
/// testable before the source registry (arb-x7w8.4) existed to expose real priorities; arb-cvru
/// bound the real <c>DbSourcePriorityLookup</c> in its place. Swapping the registration was the
/// whole change, because <see cref="IDedupStage"/> reads the interface and never a concrete type.
///
/// <para>It is retained as the explicit no-opinion lookup — for tests and for any caller with no
/// stated ordering policy, as <c>DedupStage</c>'s constructor doc names it. Zero is deliberately
/// the same neutral value <c>Source.Priority</c> defaults to, so a caller that passes this instance
/// cannot reorder a group whose sources were all left unranked.</para>
/// </remarks>
public sealed class AllEqualSourcePriority : ISourcePriorityLookup
{
    /// <summary>Shared instance; the type is stateless, so one is enough.</summary>
    public static readonly AllEqualSourcePriority Instance = new();

    public int PriorityOf(string sourceName) => 0;
}
