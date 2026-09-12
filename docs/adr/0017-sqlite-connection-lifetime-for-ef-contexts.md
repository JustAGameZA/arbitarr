# 0017. Hand EF Core a closed, EF-owned SQLite connection that configures itself on every open

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

Every `ArbitarrDbContext` is built from options assembled by `ArbitarrDbContextOptionsFactory.Create`,
which hands EF Core a `SqliteConnection` that `SqliteConnectionFactory` built. That hand-over has to
satisfy two requirements at once, and they pull in opposite directions.

The first is **shared configuration**. Raw ADO.NET access — the maintenance job's `VACUUM`, the
backup and restore paths — opens connections through `SqliteConnectionFactory.OpenConnection`, which
applies `busy_timeout` and then verifies the file is in WAL journal mode. `busy_timeout` is what
stops the background classifier's continuous writes returning `SQLITE_BUSY` to the inline reader on
the request path (AC15a); it is connection-scoped state, and SQLite's default is `0`, meaning
*fail immediately*. If EF's connections are configured by some other route than the ones raw ADO.NET
takes, the two drift, and the drift is silent: nothing reports an unconfigured connection until a
concurrent write turns into a failed request.

The second is **handle lifetime on Windows**, and three separate beads each rediscovered one part of
it after the previous one closed:

- **arb-dhua** (#230). `UseSqlite(DbConnection)` leaves ownership with the *caller* by default, so a
  disposed context did not dispose its connection, and a connection never disposed is never
  *returned* to the pool. `SqliteConnection.ClearPool` closes only what a pool *holds*, so no clear
  at any scope reached it. ~22,000 config directories had leaked under `%TEMP%` because the delete
  lost to a live handle on `arbitarr.db`. Three attempts had gone looking for a wider set of
  connection *strings* first; the inventory was never the problem.
- **arb-gz3o**. After the ownership fix, 3–4 directories per Integration run still survived with only
  `arbitarr.db`/`-wal`/`-shm` left. The attribution looked like a property of particular test classes,
  because structurally identical siblings leaked nothing.
- **arb-auam** (#290). The residue was the other half of the same invariant. `RelationalConnection`
  ADOPTS its connection **lazily**, on the context's first actual use. `Create` opened the connection
  *eagerly* before handing it over, so a context that was resolved and then disposed **without ever
  being used** never adopted the handle: the ownership flag had nothing to act on, the connection was
  neither closed nor returned to the pool, and it lived for the life of the process. Measured 3/3
  each way — an eagerly-opened connection leaked when the context went unused and did not when the
  context was touched once. That asymmetry, a leak that disappears the moment anything reads from the
  context, is why it survived arb-dhua and why it read as a test-class fault rather than what it was:
  a **production** defect. `Program.cs`, `MaintenanceHostedService` and
  `DownloadRefusalRehydrationService` all resolve a context on a path that can return early, and each
  such scope leaked one connection permanently in a long-running host.

So: ownership decides *who disposes*, and it says nothing about *when EF takes charge*. Each half
hides the other's absence, which is exactly why they were found six weeks and three beads apart.

## Decision

**`ArbitarrDbContextOptionsFactory.Create` obtains a CLOSED connection from
`SqliteConnectionFactory.CreateUnopenedConnection` and passes it to
`UseSqlite(connection, contextOwnsConnection: true)`. The connection configures itself — `busy_timeout`,
then the WAL verification — from a `DbConnection.StateChange` handler that calls the same private
`Configure` method `OpenConnection` calls, so EF and raw ADO.NET reach one implementation. The
configuration therefore runs once per OPEN, not once per connection.**

Three properties follow, and all three are load-bearing:

- **Closed on hand-over** closes arb-auam. An unused context has nothing to release, because there is
  nothing open; a used one is opened, owned and returned by EF itself. The window in which a handle is
  open but unowned no longer exists.
- **`contextOwnsConnection: true`** closes arb-dhua. Disposal returns the handle to the pool. Note
  that it only *returns* it — a pooled handle still holds a share lock on Windows, so a caller that
  needs the file deleted must also clear the pool. Both halves are required and neither is sufficient.
- **Per-open configuration** is the correct granularity rather than a compromise. EF closes and
  reopens an externally supplied `DbConnection` between operations, so this connection is opened many
  times over its life, and `busy_timeout` is connection-scoped state that a close discards. It must be
  reapplied each time. `OpenConnection` configures once only because it hands back a connection it
  opened once; the two paths agree on *what* is applied to an open connection, not on how often.

**`ConvertToWalOnce` remains the sole writer of `journal_mode` (arb-itmm).** Neither open path may
re-issue the `SET`; both only read the mode back and throw if it is not `wal`. On a not-yet-converted
file the `SET` is not bounded by `busy_timeout` — measured at 8087 ms against a 5000 ms timeout, and
unbounded while a blocker holds a transaction — which on a container's first start is a hang with no
timeout anywhere to break it.

## Alternatives rejected

### `UseSqlite(connectionString)` — let EF create and own the connection outright

The smallest change, and it does fix the leak: EF creates the connection, so it owns it
unambiguously from the start and lazy adoption never arises. Rejected because it silently drops both
pragmas. EF would open a connection `SqliteConnectionFactory` never touches, so `busy_timeout` falls
back to SQLite's default of `0` — immediate `SQLITE_BUSY`, the AC15a hazard — and the journal-mode
verification never runs on the application's own connections at all. That is precisely the drift
between EF and raw ADO.NET that routing everything through one factory exists to prevent, and it
would be invisible until a concurrent write failed a request. The connection therefore stays
factory-built; only its *opening* moves.

### A `DbConnectionInterceptor` overriding `ConnectionOpened`

EF's own extension point for work on a freshly opened connection, and it would carry the pragmas
without the connection needing to know anything. Rejected because it fires only for connections EF
opens. Raw ADO.NET callers — the maintenance job, backup, restore — do not pass through EF's
interceptor pipeline, so they would still need `OpenConnection`, leaving **two** configuration paths
to keep in agreement. That is the same drift as the previous alternative, arrived at from the other
direction, and it is worse for being plausible: both paths would be correct on the day they were
written, and only one of them would be updated the day the pragmas change.

### A `SqliteConnection` subclass that configures itself on `Open`

An override is the natural place for "always do this when opened", and it would need no event
subscription. Rejected on a hard mechanical constraint: every `SqliteConnection` subclass must chain
a constructor that takes a connection string, and
`Arbitarr.Architecture.Tests.NoInlineDatabaseConnectionStringsTests` bans exactly that shape outside
`DatabaseConnectionStrings`. The subclass cannot be written without either defeating that scan or
carving an allow-list into it, and that scan is what keeps the set of pool-clearable connection-string
shapes closed (see [docs/standards/data.md](../standards/data.md#connection-pools-are-keyed-by-the-full-string)).
A `StateChange` handler achieves the same per-open hook with no new type and no exemption.

### Keep the eager open and force EF to adopt the connection

Open the connection as before, then make something touch the context so `RelationalConnection` adopts
it — a trivial query, or a `Database.CanConnect()` in `Create`. Rejected as a no-op touch that any
cleanup deletes. It is a statement whose only purpose is its side effect, with nothing at the call
site explaining that removing it reopens a Windows-only file-handle leak; it costs a database round
trip on every scope, including the early-return scopes that are the entire reason the leak existed;
and it makes correctness depend on an extra operation rather than on the absence of one. Handing over
a closed connection removes the adoption question instead of answering it.

## Consequences

**The deferred path fails closed more WEAKLY than the raw path, and that is an accepted trade-off
rather than an oversight.** `OpenConnection` disposes the connection when `Configure` throws, so a
failed verification leaves no usable handle behind. The `StateChange` handler cannot do that — it
runs *inside* the caller's own `Open()`, where disposing the connection is not available — so on a
failed verification the connection is left `Open`. `busy_timeout` has already been applied by that
point, and EF disposes the connection regardless under `contextOwnsConnection: true`, so **nothing
leaks**. The residual exposure is narrow and specific: a caller that *catches* the throw could go on
using a handle whose journal mode was never verified. Only the verification is weaker, and only for a
caller that swallows the exception. Accepted because the alternative shapes all reintroduce a second
configuration path, and because the throw does surface out of the caller's `Open()` — which is the
load-bearing claim, since an exception from an event handler is the kind of thing a provider could
reasonably swallow.

**Pooled handles make a naive test of the per-open configuration vacuous.** `busy_timeout` is
connection-scoped state and a handle returned to the pool KEEPS it, so *any* earlier factory call on
the same file leaves a pooled handle already carrying the value — `ConvertToWalOnce` alone is enough,
because it applies the timeout on a connection of its own. A subsequent `Open()` then draws that
handle and reads back the right answer having configured nothing. Measured, not theorised: deleting
the `StateChange` registration outright left the obvious version of the test PASSING, and it still
passed with the reference value measured on a second file. Any future test of this path must both
measure its reference on a **separate database file** (a distinct pool) and **clear this file's pool**
after the conversion; with both in place the same deletion fails the test, and dropping either one
restores the vacuum. `SqlitePoolCleaner.ClearPoolsFor` is the scoped API for that clear —
`ClearAllPools` is banned (CLAUDE.md §4) and would reach into neighbouring test classes.

**Each half is pinned by a different test, deliberately, because each fails on its own terms.**

| Property | Pinned by |
|---|---|
| `contextOwnsConnection: true` on every production `UseSqlite(DbConnection)` | `Arbitarr.Architecture.Tests.SqliteConnectionOwnershipTests` (Cecil IL scan; covers both `false` and the dropped-argument overload) |
| A resolved-but-unused context leaks no handle | `Arbitarr.Integration.Tests.UnusedDbContextDoesNotLeakItsConnectionTests` |
| Closed on return; `busy_timeout` applied on the caller's open; a non-WAL file throws out of that `Open()`; the disposal asymmetry | `Arbitarr.Data.Tests.CreateUnopenedConnectionTests` |

The ownership scan exists because dropping the flag still compiles, still passes every behavioural
test on Linux, and fails only through a Windows-specific symptom a long way from the edit that caused
it. The unused-context test exists because a test that *uses* its context cannot observe the leak at
all — which is why the residue was misattributed to particular test classes for as long as it was.

**Config-directory teardown still needs its pool clear.** Ownership plus lazy-adoption-avoidance gets
the handle back to the pool; it does not close it. `ConfigDirectoryTeardown` clears the pools before
deleting, and its failure message names all three requirements — the clear, the ownership flag, and
the closed hand-over — so a future cleanup failure points at the right one rather than at an assumed
external lock holder.

**Do not "simplify" either half back.** The flag without the closed connection is arb-auam; the closed
connection without the flag is arb-dhua. Each hides the other's absence, and both symptoms are
Windows-only file-handle failures that no Linux CI run reproduces.
