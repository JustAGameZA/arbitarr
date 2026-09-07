# 0003. De-rank franchise siblings; never discard them

- **Status:** Accepted
- **Date:** 2026-08-27

## Context

*Ghost in the Shell* exists as, among others, the 1995 film, *Stand Alone Complex*, *Arise*, and
*SAC_2045* — separate works with separate TVDB identities, not seasons of one another. They share
most of their title text, and several use `S01E01`-style numbering, so the episode numbers *agree*
between distinct series. String similarity and number equality both point the wrong way at once,
and fuzzy matching conflates them.

The failure is silent and self-confirming: the download succeeds, the file lands, and nothing in
the chain reports a problem. See [0002](0002-admit-no-match-when-ambiguous.md) for the cost
asymmetry this rests on.

The first draft of the design proposed a hard gate: admit only releases matching the exact
requested entry, reject everything else. This was rejected on the plan, before any of it was
built — no hard-gate code path ever existed to be removed.

## Decision

Classification, not exclusion. `FranchiseRelation` labels a candidate `Same`, `Sibling`, or
`Unrelated`, and carries a human-readable `Reason` for every `Sibling`. Ranking consumes the label
to **de-rank** siblings. Nothing is dropped on the basis of the classification.

Classification assigns no score and makes no admit/reject decision itself; its whole job is
labelling *why* a candidate is a sibling rather than the same series.

## Alternatives rejected

- **The hard gate from the first design draft.** Rejected as fail-closed: it violates the principle that a
  real match can still exist under a slightly different alternate title. A release legitimately
  titled with an alternate rendering would be dropped outright, with no recourse and no signal
  that it happened.
- **Merge siblings and let the numbering sort it out.** Rejected: the numbering agrees across
  siblings, which is precisely why the problem exists. Merging destroys the only distinction that
  was available.
- **Classify but discard `Unrelated`.** Rejected for the same reason as the hard gate — the
  classifier's confidence is not high enough to make its negative verdict load-bearing.

## Consequences

- A sibling can still be selected if nothing better exists, which is the desired behaviour when the
  requested series genuinely has no release.
- The de-rank weight lives in ranking, not classification, so tuning it never touches the
  classifier and cannot silently turn into a filter.
- Every sibling classification carries a reason, so a surprising ranking can be explained after the
  fact rather than re-derived.
