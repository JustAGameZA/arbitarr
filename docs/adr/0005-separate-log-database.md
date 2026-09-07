# 0005. Keep the log store in a second SQLite database

- **Status:** Accepted
- **Date:** 2026-09-06

## Context

Issue #65 added a persistent log store served at `/api/admin/logs`. Logs and configuration have
almost nothing in common operationally: configuration is small, precious, and worth backing up;
logs are large, disposable, and grow without bound.

They also differ in what they may contain. `IHttpClientFactory` attaches its own logging handler to
every named client and logs the full absolute URI at Information, so the log store can capture
material the configuration store never holds.

## Decision

Two databases: `arbitarr.db` for configuration and cached metadata, `arbitarr-logs.db`
(`LogStore.DatabaseFileName`) for logs.

## Alternatives rejected

- **One database with a `Logs` table.** Rejected: a config backup would silently include log
  contents, so exporting settings to share or archive would carry request history with it. Making
  the backup selective is a filter that has to keep working; separate files are a property that
  cannot be forgotten.
- **Logs to a file, not a database.** Rejected: the admin API needs to query, filter, and paginate
  them, which is what the store is for.

## Consequences

- The log database has **no EF migrations** and is absent from `MaintenanceJobResult`. It is
  invisible to most operational surfaces.
- Any feature enumerating stores — a health check reporting DB sizes, a disk-usage panel, a support
  bundle, a backup — must grep for `DatabaseFileName`, **not** for `arbitarr.db`, or it silently
  covers only half the data. This has to be re-checked every time such a feature is added.
- `LogMessageCleanser` scrubs credentials in query strings only. A secret in a URL **path** (a
  webhook token, say) is not covered, so such registrations need `.RemoveAllLoggers()` — care taken
  inside a typed client cannot defend against a handler the container wraps around it.
