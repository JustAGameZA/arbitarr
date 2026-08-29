namespace Arbitarr.Media.Identity;

/// <summary>
/// How a candidate <c>SeriesIdentity</c> relates to the series actually being searched for.
/// </summary>
/// <remarks>
/// <para>
/// Exists because of the Ghost in the Shell franchise-disambiguation problem: <c>Arise</c>,
/// <c>Stand Alone Complex</c>, and <c>SAC_2045</c> are three canonical, distinct
/// <c>SeriesIdentity</c> records that must never merge, yet share enough title text that a naive
/// fuzzy match would treat one as a rendering of another. Iteration 1 proposed a hard gate that
/// admits only releases matching the exact requested entry and rejects everything else outright;
/// the plan explicitly rejects that as fail-closed (violates P1: a real match can still exist under a
/// slightly different alternate title, and a hard gate would drop it with no recourse). The correct
/// v1 behavior is a *classification*, carried alongside the candidate with a recorded reason, that
/// downstream ranking (Step 3b) uses to de-rank siblings — never to discard them outright.
/// </para>
/// </remarks>
public enum FranchiseRelation
{
    /// <summary>The candidate identity is (or is confidently believed to be) the same series.</summary>
    Same,

    /// <summary>
    /// The candidate identity shares franchise lineage (title tokens, shared universe) with the
    /// requested series but is a distinct, non-mergeable work — e.g. Ghost in the Shell: Arise
    /// relative to a request for Stand Alone Complex. Never excluded outright; carries a
    /// <see cref="FranchiseClassification.Reason"/> for Step 3b's de-ranking to consume.
    /// </summary>
    Sibling,

    /// <summary>No franchise relationship was detected between the candidate and the requested series.</summary>
    Unrelated,
}

/// <summary>
/// The recorded outcome of classifying one candidate <c>SeriesIdentity</c> against the series being
/// searched for.
/// </summary>
/// <remarks>
/// This is a classification only — it carries no score and makes no admit/reject decision itself.
/// Assigning an actual de-rank weight to <see cref="FranchiseRelation.Sibling"/> results is Step 3b's
/// job; this step's responsibility ends at correctly labelling *why* a candidate is a sibling rather
/// than the same series, so that label is available for 3b to consume.
/// </remarks>
/// <param name="Relation">The detected relationship.</param>
/// <param name="Reason">
/// Human-readable justification for the classification, e.g. "sibling, not same series: shares
/// title tokens 'Ghost in the Shell' with requested series 'Ghost in the Shell: Stand Alone Complex'
/// but resolved to a distinct TVDB ID". Always populated for <see cref="FranchiseRelation.Sibling"/>.
/// </param>
public sealed record FranchiseClassification(FranchiseRelation Relation, string? Reason);
