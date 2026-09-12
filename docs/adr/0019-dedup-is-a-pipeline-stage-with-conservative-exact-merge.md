# 0019. Dedup is a pipeline stage, and it merges only on exact evidence

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

Today Arbitarr has exactly one upstream source, and deduplication is NZBHydra2's job. Hydra
aggregates the indexers, groups what they return, and hands Arbitarr a single list.
`IDedupStage` (`src/Arbitarr.Core/Pipeline/IDedupStage.cs`) is a marker interface with no
implementation anywhere, because nothing has needed one.

The direct-indexer epic makes Arbitarr the aggregator. Once an operator configures two indexers
that both carry the same release — or, more often, configures a direct indexer *and* keeps Hydra,
so a release arrives once from each — the same file appears twice in one answer. Dedup stops being
Hydra's job and becomes Arbitarr's, and two questions have to be settled before anything is built:
who owns it, and how strong the evidence for a merge has to be.

Both reference implementations answer the second question, and neither answer can be adopted:

- Prowlarr deduplicates by release GUID, keeping the result from the highest-priority indexer. A
  GUID is minted by the indexer that issued it, so two indexers describing one file produce two
  GUIDs. Across indexers the rule is a no-op — it collapses an indexer's duplicates of itself,
  which is the case that does not arise.
- NZBHydra2 groups fuzzily on title, size, poster and age, and its duplicate detection excludes
  torrents outright — the fuzzy grouping is Usenet-only. That exclusion is worth noticing because it
  points the same way as the third condition below: even the fuzzy reference declines to reason
  across delivery protocols. Fuzzy grouping merges releases that are
  merely similar, and the cost of being wrong is not symmetric. Under
  [ADR 0002](0002-admit-no-match-when-ambiguous.md), a **false split** shows the operator a
  duplicate row: visible, cheap, self-correcting. A **false merge** hides one release behind
  another, possibly better, one. Nothing reports an error, the hidden release is simply not
  offered, and the operator cannot tell a merged result from a result that was never returned.
  That is a wrong answer, not a visible one — the exact shape ADR 0002 exists to refuse.

## Decision

**Deduplication is a pipeline stage implementing `IDedupStage`, covering every source including
NZBHydra2. No adapter deduplicates its own results.**

**Two candidates merge only on exact evidence, all three conditions together:**

1. **Normalised title equality** — equality, never similarity. No edit distance, no token overlap,
   no score threshold.
2. **Size within a tight tolerance**, to absorb the small disagreements indexers report for the
   same file.
3. **Same `ProtocolKind`** (`src/Arbitarr.Core/Releases/ProtocolKind.cs`), read from
   `ReleaseCandidate.Protocol`, whose default is `ProtocolKind.Unknown`. **`Unknown` matches
   nothing, including another `Unknown`** — two candidates that both failed to report a protocol
   are not thereby evidence of being the same artifact. So an under-populated candidate never
   merges, which keeps a missing field from becoming an accidental match.

**The dedup stage owns its own normalisation, in `Arbitarr.Core`.** It is a small, self-contained
transform — case-folding and whitespace/punctuation collapse — and it is deliberately **not** the
deny-list `TitleNormalizer` in `src/Arbitarr.Ai/Normalization/`. Two reasons, either sufficient:
`Arbitarr.Api`, where the pipeline stages live, does not reference `Arbitarr.Ai` at all; and that
normaliser is default-OFF behind `SettingKey.TitleNormalizationEnabled`, so depending on it would
make dedup's behaviour follow an unrelated AI setting. It is also not deobfuscation — an obfuscated
Usenet title is normalised as the literal text it is. The Ai deny-list stays an AI-prompt concern.
**"Normalised title equality" in this ADR means that Core normaliser**, which arb-x7w8.8 specifies
and tests.

**A group retains all its members, ordered by source priority.** The losers are never discarded:
a failed grab from the first member can fall back to the next, which is only possible if the group
still holds it. This is [ADR 0003](0003-siblings-are-deranked-not-discarded.md)'s de-rank-never-discard
applied to a different axis — the ordering carries the preference, the set carries the options.

That ordering has a precondition worth stating, because it is not free: `UpstreamMergeStage` tags
each candidate with its originating source **name** (`RenderedRelease(source.Name, …)`), while the
rank lives on `Source.Priority` (an `int`, **higher wins**). The dedup stage therefore needs a
name→priority lookup; it cannot read a priority off the candidate it is holding.

The owner ratified this policy on 2026-09-12 (arb-x7w8.9); this ADR records it rather than
proposing it.

## Alternatives rejected

### Deduplicate inside each source adapter

Rejected: placement *is* the decision. An adapter sees only its own indexer's results, so
adapter-level dedup can only collapse one indexer's duplicates of itself — the case that needs no
handling. It cannot see the duplicate that matters, which spans two indexers, and it would have to
be reimplemented in every adapter while still missing that case. A pipeline stage sees the merged
fan-out, so it covers Hydra-sourced results and direct-indexer results in one place.

### Prowlarr's GUID dedup, adopted as-is

Rejected: a no-op across indexers, as described above. Its keep-the-highest-priority-indexer half
is worth borrowing — it survives here as the group's *ordering* — but its matching half identifies
nothing that Arbitarr needs identified.

### NZBHydra2's fuzzy grouping, adopted as-is

Rejected: a false merge is invisible and a false split is visible, so a policy that trades splits
for merges is trading a cheap error for an expensive one. The same asymmetry is why ADR 0002
admits no match instead of guessing and why ADR 0003 de-ranks instead of discarding. Similarity
thresholds also have to be tuned, and a threshold that is wrong is wrong silently.

### Merge on strong evidence but keep only the winner

Rejected: discarding the other members throws away the fallback that makes multi-indexer
aggregation worth having. If the preferred member's grab fails, the operator gets nothing, even
though another indexer was holding the same release a moment earlier. Ordering expresses the
preference at no cost; deletion expresses it by destroying the alternative.

## Consequences

- **Two rows for one release is the expected failure mode, and it must not be "fixed" by loosening
  the match.** Equality was chosen knowing it splits some true duplicates. Any change that
  introduces similarity scoring, a fuzzy title distance, or a cross-`ProtocolKind` merge reverses
  this decision and needs an ADR superseding this one, not a tuning commit.
- **The Core normaliser is the whole surface of the title rule**, so what it folds is
  load-bearing: broadening it merges more, narrowing it merges less. It is case-folding and
  whitespace/punctuation collapse — not deobfuscation, and not the `Arbitarr.Ai` deny-list. An
  obfuscated Usenet title is therefore compared as the literal text it is, so two indexers carrying
  the same obfuscated post merge only if they spell it identically, and otherwise split. A split is
  the acceptable outcome here.
- **Nothing in this decision may reach for `Arbitarr.Ai`.** The project reference does not exist,
  and adding one to borrow a normaliser would couple the dedup stage to the AI surface and to a
  default-OFF setting. If the two normalisations ever need to agree, the shared piece moves to
  `Arbitarr.Core`; the dependency does not get added.
- **Groups are ordered, not reduced**, so every downstream consumer sees a group of N members and
  must not assume N is 1. Tests assert the member count, not only the group count — a stage that
  silently kept one member would pass a group-count-only assertion.
- **Dedup covers Hydra too.** Hydra has already deduplicated what it returns; the stage running
  over those results again is harmless under an equality rule and is what makes the
  Hydra-plus-direct-indexer overlap disappear.
- **This ADR unblocks arb-x7w8.8**, which implements `IDedupStage` — today a marker interface with
  no implementation — along with the Core normaliser, the name→`Source.Priority` lookup, and the
  tests that pin all of the above.
