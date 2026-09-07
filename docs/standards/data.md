# Standards — Data

Rules about persistence, caching, and the records that make a match auditable.

---

## Two databases, not one

`arbitarr.db` holds configuration and cached metadata. `arbitarr-logs.db`
(`LogStore.DatabaseFileName`) holds the log store. They are deliberately separate so a config
backup does not drag log contents along. See [ADR 0005](../adr/0005-separate-log-database.md).

**Any feature that enumerates stores must grep for `DatabaseFileName`, not for `arbitarr.db`.**
That includes a health check reporting DB sizes, a disk-usage panel, a support-bundle export, and
any backup.

*Why:* the log database has no EF migrations and is not reported in `MaintenanceJobResult`, so it
is invisible to most operational surfaces. A feature that searches for the literal filename covers
exactly half the data and looks complete.

---

## Provenance is mandatory on degraded paths

**Any path that degrades records the fact in `MatchProvenanceFlags`** rather than collapsing into a
bare `null` or a best-effort match.

The flags are `[Flags]` because more than one can hold at once — the cache can be absent *and* the
upstream unreachable. **Do not collapse them into a single "degraded" value.**

*Why:* `CacheAbsent`, `SourceUnreachable`, and `NoXemCoverage` are different failures with
different remediations. An empty cache does not imply the upstream is down; an unreachable upstream
does not imply no cache exists; and no XEM coverage is a permanent, legitimate condition rather
than a transient failure — which is why it is negative-cacheable and the others are not. Collapsing
them erases exactly the distinction an operator needs to act.

**Ambiguity is reported as a flag, never inferred from a null match.** `AmbiguousMapping` and "no
candidate found" require different responses. See
[ADR 0002](../adr/0002-admit-no-match-when-ambiguous.md).

**Every match records which source resolved it** (`IdentitySource`) and the evidence behind it.
A wrong match that cannot be traced to its cause cannot be fixed.

---

## Caching

**Metadata is cached against a hash of the source snapshot it came from**, so an upstream edit
invalidates stale entries rather than serving them indefinitely (`SourceSnapshotHasher`).

**Negative outcomes are cached too.** `NoXemCoverage` is a real, stable answer; re-asking on every
request hammers an endpoint to learn the same thing. Transient failures
(`SourceUnreachable`) are **not** negative-cacheable — caching an outage extends it past its end.

TheXEM (thexem.info) is community-maintained and is what makes arc-relative resolution possible at
all. Its two negative states must never be conflated: **no coverage** means XEM answered and has no
entries for the series (permanent, legitimate, worth caching), while **unreachable** means XEM did
not answer (transient, never cached).

**`FreshUntil` and `ServeUntil` are two boundaries on one entry**, not alternatives. Inside
`FreshUntil`, serve directly with zero upstream requests. Past `ServeUntil`, do not serve at all.
The band between is the availability fallback: stale, but better than nothing while upstream is
down. A change to one is not automatically a change to the other.

**Query snapshots are keyed excluding `offset`/`limit`** (`IQuerySnapshotStore`) so pagination
through one result set stays consistent rather than re-querying per page.

---

## Prune predicates

"May this entry still be served" and "may this entry be deleted" are **distinct questions with
distinct predicates** (`PrunePredicates`) and must never be confused.

*Why:* an entry can be unservable but still worth keeping (for history, or because deleting it
loses the negative-cache result). Merging the two predicates silently changes retention.

---

## Settings

**The catalog is the allow-list.** `SettingsCatalog` feeds both the PUT allow-list and the GET
projection, so anything added to it becomes both writable *and* readable. Sensitive values are
excluded and given their own write-only route.

**Validation rejects; it never clamps.** A value outside bounds is an error the caller must see,
not something to silently round into range. Validators live in `SettingsValidator` — one floor, in
one place, reached through the repository's switch, not duplicated at the endpoint.
