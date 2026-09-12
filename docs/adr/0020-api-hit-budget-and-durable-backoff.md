# 0020. The API-hit budget and backoff state are durable, and separate from the circuit breaker

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

Indexers impose hard daily limits on queries and grabs. Exceeding one is not a fault the indexer
reports politely — it costs the operator their account. Today NZBHydra2 tracks those limits and
Arbitarr never sees them; once Arbitarr talks to indexers directly, it has to count its own hits
and stop before the limit, and it has to hold off an indexer that is failing rather than hammering
it.

Arbitarr already has `IAsyncCircuitBreaker`
(`src/Arbitarr.Core/Sources/CircuitBreaker/IAsyncCircuitBreaker.cs`), and the obvious move is to
put budgets and backoff into it. It is the wrong home, and the reason is a lesson this repository
has already learnt once. The breaker is an in-process, short-window fault detector: it answers
"did this source just fail several calls in a row", it recovers on its own within minutes, and its
state is not something an operator acts on. A budget is none of those. It is a day-long accounting
fact, an operator must be able to see it to understand why an indexer returned nothing, and it
must survive a restart — a process that restarts at hour 20 of a 24-hour window and starts
counting from zero will spend the remaining four hours over the limit while reporting nothing
wrong.

That is precisely the failure [ADR 0016](0016-persist-download-refusal-health.md) closed for
`DownloadRefusalTracker`: an in-memory tier reported a clean system after a restart while every
download still failed, because the condition outlived the process and the state did not. The
relationship between the budget and the breaker is the same relationship
`DownloadRefusalTracker` has to the breaker — a durable, operator-facing condition alongside a
transient, in-process one, not a special case of it.

Counting hits needs a source of truth. There are two candidate shapes: a purpose-built accounting
table (NZBHydra2's `indexerapiaccess`, one row per hit), or derivation from history already in the
database (Prowlarr's, whose `IndexerLimitService` counts history since the start of a window sized
by the limits unit). Deriving has two complications here, and both are load-bearing rather than
incidental.

First, **no event written today records a per-source upstream hit.** `SearchServed` counts client
requests and is written even when the answer came from a cache or a snapshot; successful grabs are
not evented at all, since the download path only writes on failure. Derivation therefore presumes a
new event kind rather than reusing an existing one — see the Decision.

Second, `EventRepository` **folds** a repeated event onto the previous row and increments its
`RepeatCount`, comparing six fields to decide ([CONTEXT.md](../../CONTEXT.md), "Search detail
format"). For a kind that folds, counting rows undercounts — and the tempting fix, adding a
per-occurrence value to `Detail` or `Reason` so events stop folding, would break folding for every
other consumer of those events.

## Decision

**Per-source API-hit budgets and backoff state are durable, restart-surviving, operator-visible
state, held separately from `IAsyncCircuitBreaker`. The breaker is unchanged and stays an
in-process short-window fault detector.**

**Counts derive from the events store, not a purpose-built accounting table**, over a rolling
window of 1 or 24 hours per the source's own `LimitsUnit`. "The events store" means the existing
store and its existing machinery — not any event it holds today.

**This derivation has a precondition, and it is not satisfied by any event written today.** No
existing `EventKind` records a per-source upstream hit. `SearchServed` is written once per *client
request* and is written even when nothing went upstream — `SearchEndpoint` computes a
`servedWithoutUpstreamCall` flag precisely because a snapshot or a warm cache serves without
calling a source — so counting `SearchServed` would count client traffic, not API hits, and would
charge budget for cache hits. Grabs are worse: the download path events only on *failure*
(`DownloadProxyEndpoint` writes a `SourceFailed`), so a successful grab — the one that actually
spends the operator's grab allowance — is recorded nowhere.

So this decision **requires a new `EventKind`**: per-source query and per-source grab hits, written
**at the upstream call sites**, on the outbound call itself and never on a cache hit or a snapshot
serve. It must be **explicitly opted into folding** in `EventRepository.MayCoalesce`, whose switch
deliberately has no default arm for an unknown kind and answers `false` — never folding costs
storage, folding wrongly costs a record. Until that opt-in exists the new kind does not fold, and
the `RepeatCount` rule below does not yet apply to it.

Given the opt-in, **counting must consume `RepeatCount` rather than row count.** Defeating folding
by rendering a per-occurrence value into `Detail` or `Reason` is not an available option: those
fields are part of the fold identity that every other event consumer depends on.

The honest statement of the trade is therefore **one fewer table at the cost of one new event kind
and its write sites** — not a free derivation. It still wins, because the events store already
supplies retention, folding and admin-visible history, and a new kind inherits all three; a
purpose-built counters table would have to grow each of them itself, and would then be a second
record of hits that can disagree with the events an operator is reading.

**At its limit a source is skipped, not failed.** A budgeted indexer is working correctly; it has
simply been used as much as it may be today. Failing it would feed fault machinery with a
non-fault, and both reference implementations agree on skipping.

**Backoff escalates on transient faults and resets to zero on success.** A recovered indexer is
recovered, not half-punished for a resolved fault — so recovery resets the level outright rather
than decrementing it one step.

**A startup grace window suppresses escalation shortly after the host starts.** A restart makes
every source fail at once — the upstreams are not reachable yet, or the host is still warming — and
without a grace window that single moment disables every indexer simultaneously, exactly when the
operator is watching the dashboard. Prowlarr has this window; NZBHydra2 does not, and neither does
Arbitarr today.

**A permanent disable bypasses escalation entirely.** An authentication failure is not transient:
no amount of waiting fixes a wrong key or a revoked account. It sets a permanent-disable flag
immediately and stops retrying, because retrying forever hides an operator-fixable fault behind
what looks like an intermittent one.

**Limits are nullable, and null means unlimited.** Null is not zero. An unconfigured limit must not
read as a limit of zero and skip the source on every search; a limit of zero, if an operator sets
one, legitimately means "do not query this indexer".

## Alternatives rejected

### Extend `IAsyncCircuitBreaker` to carry budgets and backoff

Rejected: it collapses two different lifetimes and two different audiences into one component. The
breaker recovers by itself in minutes and nobody needs to be told; a budget lasts a day and the
operator must see it to understand an empty result, and a permanent disable never recovers without
a human. Folding them also means a budget exhaustion would present as a source fault, which is the
one thing this decision says it is not. The breaker's own scope boundary stays exactly where it is.

### A purpose-built API-access table, one row per hit (NZBHydra2's model)

Rejected, though the margin is narrower than it first looks, because neither option is free: this
table would need a migration, a prune, a retention window and an admin surface, while the chosen
route needs a new event kind and its write sites. The comparison is therefore not "reuse something
that already exists" versus "build something" — hits are recorded nowhere today either way. It is
which mechanism the new records live in. Events win because retention, folding, pruning and
operator-visible history already exist there and a new kind inherits all four, whereas a
purpose-built table grows each of them itself and then becomes a second record of hits that can
disagree with the events an operator is reading. Per-hit rows also grow without bound, where folded
events compress a burst onto one row by construction.

### Keep budget and backoff state in memory

Rejected: this is [ADR 0016](0016-persist-download-refusal-health.md)'s failure, reintroduced. A
restart inside a 24-hour window resets the count to zero, and Arbitarr then queries past the
operator's real limit while reporting a healthy indexer — the "clean dashboard while everything
still fails" shape. A permanent disable held in memory is worse: a restart silently re-enables an
indexer whose key is known to be rejected, and the auth failures start again.

### Treat authentication failures as transient and escalate them

Rejected: escalation assumes waiting helps. For a rejected key it does not, so escalation converts
a fault the operator could fix in a minute into a slow, permanent trickle of failures that looks
intermittent. Worse, each retry spends budget and re-presents a bad credential to the indexer. An
explicit permanent disable makes the fault loud and actionable exactly once.

### Escalate from the first failure after startup, with no grace window

Rejected: a restart produces a burst of failures that says nothing about the indexers. Escalating
on it disables the whole set at once, and the operator sees a system that looks catastrophically
broken in the minutes right after a deploy — the one moment they are most likely to conclude the
deploy caused it. The grace window costs a short delay before a genuinely broken indexer starts
backing off, which is cheap against disabling every healthy one.

### Anchor the window to a clock hour or a fixed daily reset

Rejected on its own merits, and attributed to nobody: an hour-of-day reset anchor has timezone and
boundary semantics to get wrong — whose midnight, the host's or the indexer's, and what happens to
the count across a DST shift — and it gets them wrong invisibly, as a count that is merely off. A
rolling window has none of that: it asks how many hits fall in the last N hours and needs no
agreement about when a day starts.

(An earlier draft credited the clock-hour anchor to NZBHydra2. That was wrong and is corrected
here: NZBHydra2 populates `apiHits`/`apiHitLimit`/`oldestApiHit` by parsing the indexer's own
Newznab `<limits>` element and reasons from the oldest recorded hit, which is a rolling model, not
an hour-of-day reset. The rolling window sized by the limits unit that this ADR adopts is
Prowlarr's `IndexerLimitService` model, and that attribution stands.)

## Consequences

- **`IAsyncCircuitBreaker` must stay as it is.** The two components look adjacent and a future
  tidy-up will be tempted to merge them. Doing so reverses this decision and needs an ADR
  superseding this one. The distinction to hold on to: the breaker detects faults, the budget
  counts permitted usage, and a budgeted source is not a faulty source.
- **This decision supersedes the counter half of `Source`'s "own table" paragraph.**
  `src/Arbitarr.Data/Entities/Source.cs` lists three things that do not belong on the config row —
  the caps cache, the per-source query/grab counters with their window start, and the last
  error/health state — and says each "belongs in its own table, following the
  `DownloadRefusalEntry` precedent". That reasoning is right about *why* none of them belongs on
  `Source` (a config row must not be rewritten on every search, and a restored backup must not
  re-assert a stale window), and this ADR does not disturb that. It does disturb the remedy for one
  of the three: **the counters get no table at all — they are derived from events.** The durable
  **backoff** row remains its own table on the `DownloadRefusalEntry` precedent, so the paragraph
  still holds for runtime state generally; it is only the counter clause that is superseded. The
  comment is not edited here (this is a docs-only change); **arb-x7w8.10 corrects it when it lands
  the feature.**
- **Counting must survive folding, once the new kind opts into folding.** A row-counting
  implementation undercounts the moment two identical events fold, and it does so silently — the
  count is merely low, never wrong-looking. The enforcing test plants a folded event with a
  `RepeatCount` above one and asserts the count matches it; that is the mutation that catches
  row-counting, and a test that only counts distinct events would pass against the bug. Note the
  ordering: while the new kind is absent from `MayCoalesce` it does not fold at all, so a
  row-counting bug would pass every test until the opt-in lands and would then start undercounting.
  The opt-in and the `RepeatCount` consumption belong in the same change.
- **The new event kind must not be written on a cache hit.** Budget counts API hits, so the write
  belongs at the outbound call site, not at the endpoint. A kind written where `SearchServed` is
  written would charge the operator's allowance for answers that never left the process.
- **Skipped, backing off, and permanently disabled are three distinct states** and must stay
  distinguishable to the operator. Collapsing them into one "unavailable" reports a permanently
  broken key as if it were a temporary pause, which removes the signal to go and fix it.
- **Null limits are load-bearing.** Any code path that coalesces a null limit to zero turns every
  unconfigured indexer off. The distinction is the difference between "no limit configured" and "do
  not use this indexer".
- **One backoff row per source bounds the backoff table without a prune**, the same bound
  `DownloadRefusalEntries` relies on under [ADR 0016](0016-persist-download-refusal-health.md).
  That bound holds only while a row's lifetime is tied to a configured source, so a source removed
  from configuration must not leave a row behind.
- **The budget events are pruned, and by machinery that already exists.** The line above is about
  the backoff table only; it is not a claim that this decision adds nothing prunable. The new event
  kind is an operational kind, so it falls under `EventRetentionPolicy`'s `OperationalRetention`
  (7 days) like every non-`Decision` kind, swept by the existing `MaintenanceJob` event prune. No
  new prune predicate is needed for it. The longest budget window is 24 hours and sits well inside
  7 days, so retention cannot truncate a window the budget is still counting over — a margin worth
  keeping in mind if either number is ever changed, since shortening retention below a limits
  window would silently start undercounting old hits.
- **This ADR unblocks arb-x7w8.10**, which lands the budget check on the search path, the
  `SourceBackoffState` table and its migration, the new event kind with its `MayCoalesce` opt-in
  and its upstream write sites, and the correction to `Source.cs`'s counter clause noted above.
