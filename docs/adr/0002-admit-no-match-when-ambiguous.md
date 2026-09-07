# 0002. Admit no match when a mapping is genuinely ambiguous

- **Status:** Accepted
- **Date:** 2026-08-26

## Context

A lookup key can match several XEM rows with no way to separate them from the names map.
`Bleach - 402` is the canonical case: absolute episode 402 and arc-relative episode 36 of
Thousand-Year Blood War are both defensible readings, and on TheXEM they can map to *different*
episodes.

The whole reason Arbitarr exists is that the surrounding ecosystem resolves this by picking one and
saying nothing.

The costs are not symmetrical, and this asymmetry is the premise under this decision and
[0003](0003-siblings-are-deranked-not-discarded.md):

- A **missing match** is visible and cheap. The episode does not arrive, the operator notices,
  searches again, and adjusts. The cost is a delay, and the system's state stays truthful.
- A **wrong match** is invisible and expensive. The wrong file downloads successfully, fills the
  slot, is marked satisfied, and stops the search. Nothing retries, because as far as the *arr app
  knows the episode is present. It surfaces when a human watches the wrong thing, possibly weeks
  later, and has to be traced back through a chain that recorded no error anywhere.

The same asymmetry governs degradation generally: an operator who cannot tell "there is genuinely
nothing" from "the thing that would have found it was broken" cannot act on either.

## Decision

When candidates cannot be separated, **none** is admitted. The result carries
`MatchProvenanceFlags.AmbiguousMapping` and the evidence that led there.

The ambiguity is reported as an inspectable flag, not inferred from a null match — a caller can
tell "ambiguous, so withheld" from "nothing found", because those need different responses.

## Alternatives rejected

- **Pick the highest-confidence candidate.** Rejected: with genuinely ambiguous input the
  confidence ordering is an artifact of scoring detail, not evidence. This manufactures a decision
  and stamps it with a number that makes it look considered.
- **Pick the first, or prefer absolute numbering.** Rejected: a fixed tiebreak is wrong exactly as
  often as it is right, and being deterministically wrong is not better than admitting ignorance.
- **Return the ambiguity as a null match.** Rejected: null already means "no candidate found".
  Collapsing the two erases the distinction the operator needs to act on, and there is no way to
  tell a coverage gap from a mapping collision after the fact.

## Consequences

- Some searches return nothing where a guess would have returned something. This is the intended
  tradeoff, not a regression, and it must not be "fixed" by adding a fallback.
- Ambiguity is countable and auditable, so the real rate is visible rather than hidden inside a
  wrong-match rate nobody measures.
- The flag is additive on `MatchProvenance` alongside the degradation flags, so a result can be
  ambiguous *and* degraded without either masking the other.
