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
table (NZBHydra2's, which records each API access as its own row), or derivation from the events
store already in the database (Prowlarr's, whose limit service derives counts from its history
service). The events store here has a property that makes this non-obvious: `EventRepository`
**folds** a repeated event onto the previous row and increments its `RepeatCount`, comparing six
fields to decide ([CONTEXT.md](../../CONTEXT.md), "Search detail format"). Counting rows therefore undercounts, and
the tempting fix — adding a per-occurrence value to `Detail` or `Reason` so events stop folding —
would break folding for every other consumer of those events.

## Decision

**Per-source API-hit budgets and backoff state are durable, restart-surviving, operator-visible
state, held separately from `IAsyncCircuitBreaker`. The breaker is unchanged and stays an
in-process short-window fault detector.**

**Counts derive from the existing events store, not a purpose-built accounting table**, over a
rolling window of 1 or 24 hours per the source's own limits unit. Because `EventRepository` folds,
counting must consume `RepeatCount` rather than row count, or record budget events in a shape that
does not fold. Defeating folding by rendering a per-occurrence value into `Detail` or `Reason` is
not an available option: those fields are part of the fold identity that every other event
consumer depends on.

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

Rejected: it is a second store of something the events store already records, with its own
migration, its own prune, and its own opportunity to disagree with the events the operator is
looking at. Deriving from events is one fewer table and one fewer thing that can drift. Per-hit
rows also grow without bound and need retention machinery that the folded events already have.

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

### Anchor the window to a clock hour, as NZBHydra2 does

Rejected: an hour-of-day reset anchor has timezone and boundary semantics to get wrong, and gets
them wrong invisibly. A rolling window has neither: it asks how many hits fall in the last N hours
and needs no agreement about when a day starts.

## Consequences

- **`IAsyncCircuitBreaker` must stay as it is.** The two components look adjacent and a future
  tidy-up will be tempted to merge them. Doing so reverses this decision and needs an ADR
  superseding this one. The distinction to hold on to: the breaker detects faults, the budget
  counts permitted usage, and a budgeted source is not a faulty source.
- **Counting must survive folding.** A row-counting implementation undercounts the moment two
  identical events fold, and it does so silently — the count is merely low, never wrong-looking.
  The enforcing test plants a folded event with a `RepeatCount` above one and asserts the count
  matches it; that is the mutation that catches row-counting, and a test that only counts distinct
  events would pass against the bug.
- **Skipped, backing off, and permanently disabled are three distinct states** and must stay
  distinguishable to the operator. Collapsing them into one "unavailable" reports a permanently
  broken key as if it were a temporary pause, which removes the signal to go and fix it.
- **Null limits are load-bearing.** Any code path that coalesces a null limit to zero turns every
  unconfigured indexer off. The distinction is the difference between "no limit configured" and "do
  not use this indexer".
- **One backoff row per source bounds the table without a prune**, the same bound
  `DownloadRefusalEntries` relies on under [ADR 0016](0016-persist-download-refusal-health.md).
  That bound holds only while a row's lifetime is tied to a configured source, so a source removed
  from configuration must not leave a row behind.
