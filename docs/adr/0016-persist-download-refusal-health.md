# 0016. Persist the download-refusal health item across restarts

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

arb-ln0 (#237, ADR 0014) made a refused download visible as a sticky, per-source health item on
`/api/status` — but `DownloadRefusalTracker` held it in a process-lifetime dictionary only. The
NZBHydra2 "NZB access type: Redirect" misconfiguration that causes a refusal outlives the process:
it is a setting, not a transient fault, and it stays wrong across restarts. The tracker did not.
A restart cleared every outstanding refusal, so a system that had been failing every download for
hours reported a clean dashboard for as long as it took the next download to fail again — the
exact invisibility ADR 0014 exists to close, reopened by anything that restarts the host: a
deploy, a crash, a routine update.

## Decision

**`IDownloadRefusalTracker` gains a durable tier. The Host composes
`Notifying(Persistent(DownloadRefusalTracker))` — the notifying decorator (arb-apj, ADR 0014's
"does not trigger notifications" line) outermost, the persisting decorator around the original
in-memory tracker. Rehydration is an awaited `IHostedService` that replays persisted rows through
the INNER concrete tracker, bypassing the notifying decorator, before the host serves its first
request.**

`PersistentDownloadRefusalTracker` mirrors every write to an `IDownloadRefusalStore` backed by a
`DownloadRefusalEntries` table in `arbitarr.db`, one row per source, upserted on each refusal and
deleted on a successful grab. `Snapshot()` never touches the store — it returns the in-memory
tracker's own snapshot, because once rehydration has run at startup the memory tier is
authoritative and is also the only writer; a per-request query would add a database round trip to
a public, `PublicRead`-classified endpoint for an answer it already holds. A failed store write is
caught, logged at Warning, and swallowed: the item is still shown for the rest of this process's
life, which is exactly the pre-persistence behaviour, so a store outage degrades durability, not
correctness for the request in flight.

`DownloadRefusalRehydrationService` is a plain `IHostedService`, not a `BackgroundService`,
specifically so its `StartAsync` is awaited before Kestrel accepts a request — see "Consequences"
for why that distinction is load-bearing. It replays each row through
`DownloadRefusalTracker.RecordRefusal` directly (not through `RecordRefusalAsync` on the
persisting or notifying wrapper), using the row's own `ObservedSinceUtc`/`LastObservedUtc` rather
than the moment the service runs, so a rehydrated item reports when the condition actually began.

## Alternatives rejected

### Store the row in the logs database

`arbitarr-logs.db` (ADR 0005) already exists and already survives a restart. Rejected: ADR 0005's
whole point is that logs and configuration are deliberately separate stores, so a config backup
does not drag log contents along and a log-retention sweep cannot delete configuration state. A
health item is operator-actionable configuration state, not a log line, and the logs database has
no EF migrations — there is no mechanism to add a typed table to it without reopening exactly the
split ADR 0005 drew.

### Prune the table via `MaintenanceJob`, like the release lookup (ADR 0015)

The release-lookup table needs a scheduled prune because it grows with every rendered release.
Rejected here as unnecessary complexity for a table that cannot exhibit the same problem:
`SourceName` is uniquely indexed and a row is deleted on the one event that ends its reason for
existing, so the row count is bounded by the number of configured sources by construction — adding
prune machinery would guard against a growth mode the index already makes impossible. (arb-pu58,
in flight, narrows "configured" to "historically configured": a row for a source since removed
from config is not deleted by anything today, and rehydrating it produces a health item for a
source that no longer exists. That is a bug in the bound, not a case for pruning as a general
mechanism, and is tracked separately rather than described here.)

### Rehydrate in a `BackgroundService`

A `BackgroundService`'s `StartAsync` schedules the work and returns immediately; the host does not
await it before serving requests. Rejected: that reopens the exact window this ADR exists to
close. A `BackgroundService` rehydration would let `/api/status` answer with only whatever rows had
loaded so far — on a slow store, potentially none — so a restart could still briefly report a
clean dashboard while the persisted refusals were legitimately mid-load. An awaited plain
`IHostedService` is the only shape that guarantees the load finishes before the first response.

### Lazy rehydrate on the first `Snapshot()` call

Defer the load until `/api/status` is first hit, rather than doing it unconditionally at startup.
Rejected on two counts. First, `Snapshot()` is synchronous and sits on the request path — loading
from the store there would either block a request thread on I/O or require making a read endpoint
asynchronous solely to serve a case that happens once per process lifetime. Second, two concurrent
first callers would race: without additional locking, both could observe an empty tracker and both
begin a load, doing the work twice for no benefit over doing it once at startup before anyone can
call in.

## Consequences

**`observedSinceUtc` now means "when the condition began", not "since this process started".**
That distinction is the entire value of persisting the tracker, and it is also the thing most
likely to look wrong to a future reader: after a restart, an item's age can legitimately be hours
or days older than the process's own uptime. `DownloadRefusal.ObservedSinceUtc`'s doc comment and
CONTEXT.md's health-item paragraph both state this explicitly so nothing "fixes" it back to
process-relative.

**`DownloadRefusalRehydrationService` must stay a plain `IHostedService`, never a
`BackgroundService`.** The two types look interchangeable at a glance — both are registered the
same way — but only the awaited-`StartAsync` shape closes the clean-dashboard-after-restart window
described above. A future refactor that "modernises" this to a `BackgroundService` would silently
reopen it, and the type's own doc comment (Ordering note) states the reasoning at the point most
likely to be edited.

**The single-read integration test is the enforcing mechanism.** Before this ADR,
`StatusHealthItemsSurviveRestartTests` polled `/api/status` up to 50×100ms after starting the
second host, because rehydration was not guaranteed to have finished by the time a request landed.
It now reads once: `DownloadRefusalRehydrationService.StartAsync` is awaited before the host
serves anything, so by the time a request is answered the load has necessarily completed. Restoring
any retry or poll in that test would silently re-permit the exact regression this ADR closes — a
`BackgroundService` regression would pass a polling test and fail only a single-read one.

**Rows for a source removed from configuration are not cleaned up by anything today** (arb-pu58,
in flight). The "bounded by the number of configured sources" claim above holds only for sources
that remain configured; a row survives its source's removal and rehydrates into a ghost health
item. The fix is tracked and scoped separately rather than folded in here.
