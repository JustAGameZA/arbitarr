# 0015. Persist the release lookup as a second tier behind the in-memory one

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

A download link Arbitarr hands to Sonarr or Radarr carries a **proxy GUID**, not the upstream URL. When the *arr instance later fetches that link, `DownloadProxyEndpoint` resolves the GUID back to a source and an upstream link through `IReleaseLookup`, and fetches the file on the caller's behalf. The GUID is what keeps the upstream link — and the API key in it — out of anything the *arr instance stores or logs.

Until arb-tps (#190, `909d917`) that lookup was **process-lifetime memory only**. `InMemoryReleaseLookup` is a bounded dictionary: `MaxEntries = 10_000` and `EntryTtl = 30` minutes, both deliberate, because without them every release ever rendered stays resident for the life of the process (SEC-M3). Those bounds keep memory finite, but they also decided how long a link worked. Two consequences followed, and both were measured on the reporting instance:

- **Every restart invalidated every outstanding link.** 29 starts in 25 hours, each emptying the dictionary; 62 × 404 within two minutes of one start.
- **A link expired 30 minutes after the search that produced it**, while *arr delay profiles routinely defer a grab well past that window.

In both cases Arbitarr answered 404 for a link it had itself issued and still considered valid. The invariant being reversed is stated in `InMemoryReleaseLookup`'s own doc comment — that type is where "process-lifetime" was a design property rather than an accident, and its comment now records that it is the fast tier rather than the whole lookup.

## Decision

**`IReleaseLookup` is two tiers. `PersistentReleaseLookup` consults `InMemoryReleaseLookup` first and, on a miss, a durable `ReleaseLookupEntry` row in `arbitarr.db` through `IReleaseLookupStore`. A store hit repopulates the memory tier before returning.**

The memory tier keeps its bounds and stays the hot path, so the download proxy's zero-database promise still holds for the common case — a grab shortly after the search that produced it. The store covers exactly the two cases memory cannot: a restart, and a grab later than the in-memory TTL. The extra round trip is acceptable precisely because it is not the hot path: by definition it runs only once memory has already failed, where the alternative outcome is a failed download rather than a slower one.

Three properties of the implementation are load-bearing:

**Repopulation is keyed by the STORED GUID.** `PersistentReleaseLookup` calls `_memory.RecordAs(proxyGuid, release)` with the GUID the caller asked for, rather than re-deriving one from the rebuilt release. The two agree whenever the row was written under the current release-GUID secret — the normal case — but they diverge the moment that secret is rotated, and a re-derived key would file the entry under something nothing ever looks up. The fast path would then silently never hit, sending every download to the database: a latency mystery rather than an error.

**A failing store degrades to a miss, never to a 500.** The store call is wrapped so any exception other than `OperationCanceledException` returns `null`, leaving the caller exactly where the memory-only implementation left it — a 404, as before this type existed. A cancellation is rethrown, because that is the caller giving up rather than the store failing.

**Lifetime is a setting, and expiry is evaluated on every read.** `SettingKey.ReleaseLookupTtl` (`release_lookup_ttl`) defaults to 14 days. `SettingsValidator.ValidateReleaseLookupTtl` imposes a 1-hour floor that **rejects rather than clamps**: a silently raised value would leave the operator believing they had set something they had not, and anything at or below the old 30-minute window reintroduces the defect the setting exists to fix. There is no ceiling — a long lifetime is a disk-space choice. Each row carries an `ExpiresAt` stamped at write time, and `ReleaseLookupStore.FindAsync` compares it on every read (`ExpiresAt <= now`), so an expired row stops resolving whether or not anything has swept it.

**Pruning follows the read side, and is hygiene only.** `MaintenanceJob.PruneReleaseLookupAsync` deletes expired rows on the maintenance pass via `PrunePredicates.IsReleaseLookupEntryPrunable`, whose boundary is inclusive to match `FindAsync` exactly. The two must agree: a disagreement by a tick would leave an instant where a row still resolves but has been deleted, or is kept but no longer resolves. Because the read side decides, the prune job running late — or not at all — cannot resurrect a dead link.

**Follow-up in flight (arb-zwk).** Three gaps from #190's reviews are being closed separately: `SearchEndpoint` awaits `UpsertRangeAsync` inline with no `try`/`catch`, so a store outage turns a *search* into a 500 while the download path degrades to a miss — the postures should match; the TTL is read with `GetAwaiter().GetResult()` on every scope creation, blocking a thread-pool thread, and should be read asynchronously or cached; and an architecture test should ban blocking calls on async APIs in `Arbitarr.Host`.

## Alternatives rejected

### A self-describing HMAC GUID, with no lookup at all

Encode the source and upstream link into the GUID itself, signed so it cannot be forged, and drop the lookup entirely — nothing to persist, nothing to expire, no restart sensitivity.

Rejected on the secrets rule. The upstream link contains the NZBHydra2 API key, so a self-describing GUID puts that key in the **path** of the `/download` route. `IHttpClientFactory`'s logging handler logs every request URI with each path segment in full and collapses only the query string, and `LogMessageCleanser` scrubs credentials in query strings — so a secret in a path segment is covered by neither (CLAUDE.md §1). The opaque GUID plus a lookup keeps the key on the server side, which is the entire reason the proxy exists.

### Raise the in-memory TTL only

Keep one tier and lengthen `EntryTtl` past the delay-profile window.

Rejected: it addresses only half the measured failure. The 30-minute expiry and the restart are independent causes, and 29 restarts in 25 hours was the larger one — no in-memory TTL survives a process exit. It also trades directly against the SEC-M3 bound: a longer TTL means more resident entries, so the fix for the delay-profile case makes the unbounded-growth case worse.

### Other alternatives from the #190 review

None to record. The pull request carries no review comments and no review bodies, so the rejected options above are the ones the commit message and the code state; nothing further was raised there.

## Consequences

**A second persisted table that must be pruned.** `ReleaseLookupEntry` grows with one row per rendered release on every search, which on an instance fielding *arr RSS syncs is thousands a day. It is therefore in `MaintenanceJob`'s prune list, and its TTL is what bounds the table. A row's own `ExpiresAt` is used rather than a live setting, deliberately: a link already handed to Sonarr was promised a lifetime, so lowering the setting shortens the *next* link rather than retroactively breaking one in flight.

**Rotating the release-GUID secret no longer costs the fast path.** Because repopulation keys on the stored GUID, links issued before a rotation continue to resolve through the store and continue to populate memory. A restore that brings back a different `release-guid-secret.key` (ADR 0007) therefore degrades throughput for already-issued links, not correctness.

**A store failure is invisible.** The degrade-to-miss contract means an outage in the store is indistinguishable from a genuine miss at the call site, and nothing is logged there today. That gap is what made arb-agh expensive to diagnose — an intermittent `NotFound` from the store fallback with no trace of why — and closing it is the first item of arb-zwk (log the fallback at Warning).

**Prune paging is deferred.** `PruneReleaseLookupAsync` materialises candidate rows client-side, because SQLite's EF Core provider cannot reliably translate `DateTimeOffset` comparisons server-side. On a very large table that is a large read in one pass. It is recorded against arb-zwk and deliberately not addressed here: the maintenance job runs on an interval, off the request path, and the table is bounded by a TTL that defaults to 14 days.
