# 0007. Restart the host after a restore rather than reloading in place

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

Issue #56 added restore: an operator uploads a backup archive and Arbitarr replaces
`arbitarr.db` and `release-guid-secret.key` with the contents. Replacing the files on disk is the
easy half. Making the *running process* use them is the decision.

Three things in a live process are pinned to the state that was just replaced, and none of them
notice a file changing underneath:

- **`ReleaseGuid.Configure` is called once at startup** with the bytes of
  `release-guid-secret.key` and holds them in a static for the process lifetime. A restored key
  file changes nothing until something calls `Configure` again.
- **Every scoped `ArbitarrDbContext`** is built over a connection opened by
  `SqliteConnectionFactory` against the old `arbitarr.db`. Connections already open — including any
  in flight during the restore — keep reading the file that was replaced. On Linux the replacement
  is permitted and the pooled handles keep serving the *old* inode, so the process would carry on
  answering from the previous database while the new one sat on disk looking applied.
- **`Database.Migrate()`** runs at startup. A restored database at an older schema needs migrating
  forward before it is served.

## Decision

After a validated restore has been applied, **stop the host** (`IHostApplicationLifetime
.StopApplication`, deferred past the current response) and let the deployment's restart policy
bring it back. Startup then does all three things above in the order it already does them.

## Alternatives rejected

- **Reload in place.** Rejected: it would have to re-run `ReleaseGuid.Configure`, drain and rebuild
  every pooled connection, and re-run `Database.Migrate()` — while requests are being served. That
  is a second, less-tested copy of the composition root whose only job is to reproduce what startup
  already does correctly, and it would be exercised exactly once per restore, which is the worst
  possible test cadence for the most destructive operation in the product.
- **Restart the process from inside itself** (re-exec). Rejected: it buys nothing over stopping,
  because in a container the supervisor is what restarts things, and it would need to work
  identically under `dotnet run`, a debugger, and a container.

## Consequences

- **The deployment must set a restart policy.** The reference `docker-compose.yml` sets
  `restart: unless-stopped`, which is now load-bearing rather than a convenience. A deployment
  without one leaves the operator to start Arbitarr again by hand after a restore.
- **The copy says so, rather than promising a restart.** `RestoreService`'s result message and the
  Backup tab both state that Arbitarr shuts down and "comes back automatically if your deployment
  restarts it". This process can guarantee that it *stops*; it cannot guarantee that anything
  starts it.
- **The last-restore outcome does not survive the restart it triggers.** It is held in
  `BackupStateStore` in memory, deliberately — a restore replaces the configuration database, so an
  outcome persisted there would be overwritten by the operation it reports on. The UI says this
  instead of showing a blank panel.
- **AC6 is still open.** The plan requires the restart behaviour to be verified against a real
  deployment, not only asserted against `RestoreCoordinator` in unit tests. That verification has
  not been done.
