# 0013. Backup staging lives under the config directory, not the OS temp directory

- **Status:** Accepted
- **Date:** 2026-09-10

## Context

Restore and backup both need somewhere to stage transient working files: the upload spool, the
validator's extraction, the download build, and the pre-zip snapshot
(`BackupPaths.StagingSubdirectoryName`, arb-3gd). The obvious home for scratch files like these is
the OS temp directory, and an earlier shape used exactly that — `Path.GetTempPath()` — before
arb-3gd moved staging under the config directory as `backup-staging/`, a sibling of `backups/`
(`BackupPaths`).

Two things force the choice beyond "seems fine either way":

- `RestoreService.ApplyValidatedFiles` moves the validated database and secret into place with
  `File.Move(incomingDatabase, _paths.DatabasePath, overwrite: true)` (and the matching call for
  the secret key). Its own remarks state the design: the two files are copied beside their targets
  under temporary names first, so the two replacements can be *narrowed to back-to-back renames*.
  A rename is atomic only within one volume. If the source of that copy — the staged, validated
  file — sits on a different filesystem from `_paths.DatabasePath`, the "rename" silently becomes
  a copy, which reopens the very torn-write window the copy-then-rename sequence exists to close.
- The machine-wide OS temp directory makes any staging-related assertion process-global rather
  than instance-scoped: a "nothing was staged" check breaks the moment another process, or another
  test host in the same run, stages a file under the same well-known prefix (arb-3gd).

## Decision

**`backup-staging/` is a subdirectory of this instance's own config directory, constructed by
`BackupPaths` from the same `configDirectory` `Program.cs` uses for the database and the secret —
never the OS temp directory.** Because staging shares a filesystem with `_paths.DatabasePath` and
`_paths.SecretKeyPath` by construction, `RestoreService.ApplyValidatedFiles`'s `File.Move` calls
are guaranteed renames, not cross-device copies.

Staging holds nothing durable: every writer cleans up in a `finally`
(`BackupService.WriteArchiveAsync`, `RestoreService`'s helpers), and a one-shot startup sweep
(`StagingSweepService` driving `StagingSweep.Run`) reclaims anything a hard kill left behind. The
sweep's no-race mechanism is a cut-off comparison, not ordering: `StagingSweepService` captures
`processStartUtc` once, before the sweep runs, and `StagingSweep.Run` deletes a file only when its
last-write time is strictly before that captured instant (`lastWriteUtc >= processStartUtc` is
skipped) — so a request-path writer that starts after the cut-off can never have its file swept,
regardless of how far startup has otherwise progressed by the time the sweep's enumeration
completes.

## Alternatives considered

- **Keep staging on the OS temp directory (`Path.GetTempPath()`).** Rejected. It is on a different
  volume from the config directory in the general case (a separate `tmpfs` mount is the normal
  case in a container), which would turn `ApplyValidatedFiles`'s renames back into copies and
  reopen the torn-write window they exist to close. It is also machine-wide rather than
  per-instance, which made a staging-related test or invariant race any other process using the
  same well-known prefix (arb-3gd) — including another test host in the same CI run.
- **Rely on OS/container temp cleaners to reclaim orphans instead of a startup sweep.** Rejected
  implicitly by staying on the config volume: unlike the OS temp directory, the config volume is
  not reliably cleared between runs, so without an explicit reclaimer an orphan left by a hard kill
  is permanent. This is what makes `StagingSweepService` necessary once staging moved off temp,
  not merely convenient.
- **A directory-walk backup that would need to explicitly skip staging.** Not how the archive is
  built, and not a design this repo wants to invite: `BackupService.WriteArchiveAsync` writes named
  entries only — `BackupArchiveLayout.DatabaseEntryName` from a database snapshot and
  `BackupArchiveLayout.SecretKeyEntryName` from `_paths.SecretKeyPath` — so nothing under
  `StagingDirectory` can leak into an archive by construction. `BackupPaths` keeps
  `StagingDirectory` a sibling of `BackupDirectory`, not a child of it, so a future feature that
  *does* walk `BackupDirectory` (a retention sweep, an export) still cannot pick up an in-flight
  staging file by accident.

## Consequences

- The config directory now holds transient files in addition to the two durable ones, which is why
  a startup sweep exists at all: `StagingSweepService` (one-shot, at startup) driving
  `StagingSweep.Run`, reclaiming anything left behind by a kill mid-restore/backup that a writer's
  own `finally` never got to run.
- A new staging writer must register its filename prefix in `StagingFileNames.AllPrefixes` or its
  orphans are never reclaimed by the sweep — `StagingSweep.Run` only touches files whose name
  starts with a known prefix.
- Backups must not drag staging along, and the mechanism that holds is structural rather than an
  exclusion list to keep in sync: `BackupService.WriteArchiveAsync` copies named files
  (`BackupArchiveLayout.DatabaseEntryName`, `BackupArchiveLayout.SecretKeyEntryName`) into the
  archive and never enumerates a directory, so `StagingDirectory`'s contents cannot appear in an
  archive regardless of what transient files happen to be sitting there at backup time.
- See [docs/standards/data.md](../standards/data.md#backup-restore-and-the-staging-directory) for
  the operational rule (where staging lives, that a sweep runs) this ADR's argument backs.
