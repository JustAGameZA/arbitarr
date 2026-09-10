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

**The shipped backup archive (#56) covers `arbitarr.db` and `release-guid-secret.key`, plus a
plain-text manifest naming the instant and the schema version — and deliberately excludes
`arbitarr-logs.db`.** The config database is the secret-bearing half (source API keys, the admin
key, per-client key hashes), so the archive is a credential either way; adding an unbounded,
unvetted text surface to a file operators carry around widens the blast radius for no recovery
benefit, and logs are not configuration. Exporting the log store is a support-bundle feature and a
different artefact with different handling — not an extra entry here.

### Backup, restore, and the staging directory

**Every backup and restore location is derived from the one injected config directory.**
`BackupPaths` builds `BackupDirectory` (`backups/`) and `StagingDirectory` (`backup-staging/`) by
combining the same `configDirectory` that `Program.cs` uses for the database and the secret, so
these paths can never point somewhere else. Neither directory is under `wwwroot`, and
`UseStaticFiles` serves only `wwwroot` — nothing in the request pipeline maps either path, which is
the property to preserve if the static-file configuration is ever revisited.

**`backup-staging/` is a sibling of `backups/`, not a child of it**, and holds only transient
restore/backup working files: the upload spool, the validator's extraction, the download build, and
the pre-zip snapshot. Nothing in it is intended to survive a process lifetime — every writer cleans
it up in a `finally`, and a startup sweep (`StagingSweepService` / `StagingSweep`) reclaims any
orphan left behind by a hard kill, using the process-start instant as a cut-off so it never deletes
a file an in-flight operation on the current run is still writing. `StagingFileNames.AllPrefixes` is
the sweep's whole contract: a new staging writer must register its prefix there, or its orphans are
never reclaimed.

**Because `backup-staging/` and the config database share the config directory's filesystem,
`RestoreService.ApplyValidatedFiles`'s `File.Move` calls are renames, not cross-device copies** —
the validated database and secret are copied beside their targets first (still within the config
directory) and then moved onto the live paths, which is what makes that final step atomic. Moving
staging back to the OS temp directory would put it on a different volume from the config directory
in the general case and silently turn that rename back into a copy.

### Connection pools are keyed by the full string

**Microsoft.Data.Sqlite keys its connection pools by the FULL connection string, not by the
file.** Two strings naming one file are two independent pools, and clearing one leaves the other's
handles open. This is why a restore that swaps `arbitarr.db` has to drop every pool that names it,
and why "every pool that names it" has to be a closed set.

**Every connection string naming the application database is built by
`DatabaseConnectionStrings`** (`src/Arbitarr.Data/DatabaseConnectionStrings.cs`) — never formatted
inline at a call site. It is the only place that knows the complete set of shapes, and
`DatabaseConnectionStrings.ForDatabase(path)` enumerates them. **A new shape must be added to
`ForDatabase`**, or it is a pool nothing clears. The failure is silent in the worst way on Linux:
the file swap succeeds, the stale pooled handle keeps serving the replaced inode, and the process
carries on reading the old database while the restored one sits on disk looking applied. The log
store's string is deliberately absent from that set — it names a separate file a restore never
replaces, and clearing it would be the over-reach described next.

**`SqliteConnection.ClearAllPools()` is banned.** It is process-global: it force-closes every pooled
connection in the process, including those of unrelated databases and of whatever test happens to
be running alongside — the mechanism behind arb-cbc/arb-5ba. `ProductionProcessGlobalStateTests`
bans it across every `src/` assembly and `TestProcessGlobalStateTests` across every test
assembly, both in `tests/Arbitarr.Architecture.Tests`, both by reading the IL with Cecil so that an
alias or a wrapper cannot evade the ban. **The replacement is
`SqlitePoolCleaner.ClearPoolsFor(path)`** (`src/Arbitarr.Data/Backup/SqlitePoolCleaner.cs`),
which clears exactly the pools `ForDatabase` enumerates for that one file; tests use
`Arbitarr.TestSupport`'s `SqliteTestDatabase` / `SqlitePools`, which scope the clear the same way.

**What the IL scan does and does not close.** `NoInlineDatabaseConnectionStringsTests` reads
`Arbitarr.Data`'s IL and fails any type outside its named allow-list that constructs a
`SqliteConnectionStringBuilder` or a `SqliteConnection` — it closes the **builder** shape. It
cannot see a string hand-concatenated inside a type that is allowed to open connections, so those
types are **trusted by convention** to take every string from `DatabaseConnectionStrings`; that
obligation is stated at each of their call sites, not enforced by the scan. Do not read the green
test as proof that no inline string exists anywhere — it proves no type outside the list builds
one.

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

## Time-bounded reads run client-side

**The SQLite EF Core provider cannot translate a `DateTimeOffset` comparison.** EF throws rather
than degrading, so every time-window predicate on `OccurredAt` runs in memory after the row is
materialised: `EventRepository.QueryAsync`, `CountAsync`, `GetAllAsync`, `PruneAsync` and
`GetAgreementAsync` all compare that column client-side. Kind, shadow mode, cursor, ordering and
`LIMIT` do translate and ride the `(Kind, OccurredAt)` index.

*Why it matters:* a time-filtered read is bounded by the **scan shape**, not by the page limit.
`QueryAsync` pulls SQL-bounded batches newest-first and stops the moment it descends past
`Since`; that early exit is the memory bound. A single `ToListAsync()` followed by a client-side
`Where`/`Take` is correct and unbounded. Any new time-bounded read must batch the same way, and
must not restate `EventQuery.MaxLimit` as a bound on the work.

---

## Settings

**The catalog is the allow-list.** `SettingsCatalog` feeds both the PUT allow-list and the GET
projection, so anything added to it becomes both writable *and* readable. Sensitive values are
excluded and given their own write-only route.

**Validation rejects; it never clamps.** A value outside bounds is an error the caller must see,
not something to silently round into range. Validators live in `SettingsValidator` — one floor, in
one place, reached through the repository's switch, not duplicated at the endpoint.
