# 0002. Admit no match when a mapping is genuinely ambiguous

- **Status:** Accepted
- **Date:** 2026-08-26

## Context

A lookup key can match several XEM rows with no way to separate them from the names map.
`Bleach - 402` is the canonical case: absolute episode 402 and arc-relative episode 36 of
Thousand-Year Blood War are both defensible readings, and on TheXEM they can map to *different*
episodes.

The whole reason Arbitarr exists is that the surrounding ecosystem resolves this by picking one and
saying nothing. A wrong grab is worse than no grab: it consumes the slot, looks successful, and is
discovered only when someone watches the wrong episode.

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
