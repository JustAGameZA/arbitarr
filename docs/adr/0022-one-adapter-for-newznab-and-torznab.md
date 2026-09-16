# 0022. One adapter serves both Newznab and Torznab

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

The direct-indexer epic adds a **Source adapter** for indexers Arbitarr talks to
directly, alongside the existing `NzbHydraSource`. Newznab and Torznab are two
`Source kind`s (arb-x7w8.1), and a question follows immediately: does each family get
its own adapter?

The two families do not differ in request grammar or response schema. Both are
Torznab/Newznab-shaped feeds and caps documents on the wire; the shared schema
namespace is what lets one **Feed parser** (`TorznabFeedParser`, `Arbitarr.Core`) read a
`newznab:attr` and a `torznab:attr` identically (CONTEXT.md, "Source adapter and feed
parser"). What differs is:

- **The endpoint an indexer publishes** — a Newznab indexer typically serves `/api`, a
  Torznab one `/torznab/api`, but this is not a fixed rule Arbitarr can compute; it is
  the operator-entered `Source.ApiPath` on the row, BaseUrl-relative.
- **Which `torznab:attr`s an indexer happens to populate** — a Usenet indexer reports
  Usenet-specific attributes (poster, group, files, password), a torrent one reports
  seeders/peers; neither difference changes how the attribute is parsed, only which
  ones arrive.

Neither difference is a behavioural branch. An endpoint difference is a configuration
value read once at construction; an attribute-population difference is already absorbed
by the feed parser reading every attr it recognises and ignoring what a given indexer
did not send. There is no code path where "this is a Newznab indexer" versus "this is a
Torznab indexer" has to be asked at runtime.

## Decision

**One adapter, `NewznabSource` (`Arbitarr.Sources.Newznab`), serves both the `Newznab`
and `Torznab` Source kinds.** The endpoint comes from the row's `ApiPath`, never from a
per-kind rule; `GetCapsAsync`'s `protocol` parameter is accepted (aggregators need it)
and ignored for endpoint selection, because a direct indexer serves one feed at one path
and has no protocol-based selection to make — NZBHydra2's `/torznab/api`-excludes-Usenet
rule (#99) is deliberately not copied onto it. Search, caps, and attribute parsing are
identical between the two kinds; the only per-instance difference is configuration data,
not code.

## Alternatives rejected

### Two adapters, one per family

Rejected: the two families share request grammar, response schema, and every parsing
rule. Two adapters would be two copies of one behaviour — the endpoint-selection logic,
the auth placement, the refusal policy (redirect handling, rate limiting, circuit
breaking) would all be duplicated verbatim, or the two copies would be kept manually in
sync. A drift between the copies would be invisible: both would keep returning plausible
results, since a Torznab-only bug would only show up on a Torznab-configured row, and
nothing forces the same test to run against both. This is the same failure mode the
Feed parser's Core placement already prevents for parsing; splitting the adapter would
reopen it one layer up.

### Per-family subclasses of one base adapter

Rejected: this only makes sense if there is a branch to put in the subclass, and there
is none. Every candidate branch point — endpoint, attribute set — is already handled by
data (the row's `ApiPath`) or by the shared parser (namespace matching, attr-presence
checks). A subclass with no method to override is a distinction the code never asks,
carried only in the type name; it adds an inheritance hierarchy to navigate for zero
behavioural payoff, and invites a future contributor to add a branch to justify the
split that should have gone in the base class instead.

## Consequences

- **A new Source kind that genuinely differs in request grammar or schema still needs
  its own adapter.** This decision rests on Newznab and Torznab sharing wire format; it
  is not a claim that one adapter should serve every kind regardless of protocol. `NzbHydra`
  keeps `NzbHydraSource` because it is an aggregator with its own endpoint-selection
  behaviour (the `/torznab/api`-excludes-Usenet rule this ADR declines to copy onto direct
  indexers) — a real behavioural difference, not merely a configuration one.
- **Sharing the adapter is the opposite call from duplicating `RadarrInstanceRepository`
  for Radarr** ([docs/standards/architecture.md](../standards/architecture.md), Radarr
  precedent), for one reason: the Radarr repository is a secret-reading call site whose
  invariant is a caller *count* — duplicating it keeps that count at one per instance
  type, which is the property being protected. `NewznabSource` reads no secret invariant
  that a second copy would threaten; the only cost of splitting it would be a
  maintenance duplicate, which sharing avoids directly. The two decisions look like they
  point opposite ways only if "duplicate vs. share" is read as one rule; they are
  answers to two different questions.
- **`SourceAdapterIsolationTests`** (introduced with `NewznabSource`, #331) allow-lists
  `Arbitarr.Core` as the only Arbitarr reference a source adapter may take. A future
  per-family split would still have to honour that isolation boundary; this ADR does not
  relax it, and reopening the split re-litigates this ADR rather than the isolation rule.
- **The endpoint is read from `Source.ApiPath`, never computed from kind.** Any change
  that reintroduces a kind-to-path mapping (e.g. "Torznab kind always means
  `/torznab/api`") reverses the reasoning above — that Arbitarr does not need to guess an
  operator's configured path — and needs its own ADR, not a tidy-up.

## Cross-references

- [ADR 0014](0014-refuse-upstream-download-redirects.md) — the redirect refusal policy
  `NewznabSource` carries as its own adapter concern, not something the shared feed
  parser holds.
- [ADR 0019](0019-dedup-is-a-pipeline-stage-with-conservative-exact-merge.md) — dedup is
  explicitly rejected at adapter level (an adapter sees only its own indexer); this ADR's
  one-adapter decision does not change that boundary, since neither Newznab-family
  adapter — shared or split — would gain visibility into another source's results.
- [ADR 0021](0021-cardigann-out-of-v1.md) — the same "generic Newznab/Torznab only"
  boundary this ADR builds an adapter for; Cardigann's declarative per-tracker model is
  exactly the kind of real per-instance behavioural difference that would justify a
  different adapter shape, which is why it stays out of v1 rather than being folded in
  here.
- The endpoint-from-row decision recorded in this ADR is the same one described in the
  merged #331 PR body as "the endpoint is `BaseUrl` + the Source row's `ApiPath`", with
  NZBHydra2's `/torznab/api`-excludes-Usenet rule (#99) named there as deliberately not
  copied.
