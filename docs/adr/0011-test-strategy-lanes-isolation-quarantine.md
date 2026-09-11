# 0011. Full suite on every PR lane, isolated and parallel, with quarantine instead of retries

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

The test suite grew past the point where a single serial `dotnet test` run stays cheap: CI's
`Build & test` job takes 4m47s wall-clock, of which 3m24s is the dotnet test step running the
whole solution in one process. `Arbitarr.Integration.Tests` alone takes 8m26s run serially
locally (321 tests) because `AssemblyInfo.cs` disables parallelism for the whole assembly — a
consequence of `ArbitarrWebApplicationFactory` and `RemoteAddressWebApplicationFactory` mutating
the process-wide `ARBITARR_CONFIG_DIR` environment variable and 42 call sites across 36 files
calling `SqliteConnection.ClearAllPools()`, which is also process-global. Both mechanisms make any
concurrent test class in the same process race any other, which is consistent with the two open
flakes (arb-cbc, arb-5ba) that manifest as `ObjectDisposedException`.

Two decisions had to be made together, because they trade against each other: how much of the
suite runs on a PR, and how a flaky test is allowed to behave when it does. Optimizing either one
without the other reintroduces the failure mode it was meant to fix — a fast-but-partial PR gate
lets a regression through on a path nothing ran, and a full-but-retried gate lets a genuinely
broken test hide behind "it passed on retry."

There is also an existing local optimization, `scripts/test-affected.sh` (Component 2 below),
already documented in `docs/standards/process.md` as the pre-push loop, but never wired into any
CI workflow — its presence in the repo does not by itself answer what a PR is required to run.

## Decision

**The PR lane runs the full suite, parallelised, never a subset.** Affected-test selection
(`scripts/test-affected.sh`, mapping changed paths to test assemblies via the project reference
graph, plus `vitest --changed`) stays a local, pre-push convenience only. No CI job is permitted
to gate on it. This keeps the ratchet and branch-protection semantics unchanged: the floor is
always measured against a run that covers everything, so a test that would have caught a
regression can never be routed around by scoping the run to "affected" files, a scope that is
itself computed from a dependency graph that can be wrong.

**Required PR checks are the backend matrix, the frontend job, and the E2E golden path.** All
three are required, not advisory — a PR cannot merge on backend-and-frontend-green alone; the
golden-path Playwright run (bootstrap admin key, add a source against a stub Torznab upstream,
search, see results) is right in the merge path. Backend isolation across the matrix groups is
achieved by moving the per-test-run config directory from an environment variable to
`IConfiguration` (`Arbitarr:ConfigDir`, read after the builder exists, set per-factory via
`UseSetting`) and by replacing all 42 `ClearAllPools()` call sites with a `SqliteTestDatabase`
fixture that owns one temp file and clears only its own pool on dispose after joining outstanding
work — eliminating the two process-global mutations that forced serial execution.

**Timing and Load tests never run in the PR lane.** They carry `Category=Timing` or
`Category=Load` and are excluded by the same filter applied identically in the PR and master
lanes: `Category!=Timing&Category!=Load&Category!=Quarantine`. They run nightly instead, against
`workflow_dispatch` and schedule triggers, alongside a flake hunt that reruns Integration and Data
five times each.

**No automatic retries anywhere in the PR lane.** Not `--retry` on the .NET side, not vitest's
`retry` option (stays `0`). A test that fails once fails the PR. The only sanctioned way for a
known-flaky test to stop blocking merges is to carry both a `Category=Quarantine` trait and a
`Bead=arb-xxx` trait naming the tracking issue, which removes it from the PR filter and moves it
to the nightly unfiltered run. An architecture test enforces that a `Quarantine` trait never
appears without a matching `Bead` trait matching `^arb-[a-z0-9]{3,}$` — quarantine is a tracked,
visible exception, never a silent skip.

**The ratchet keeps its existing semantics under a filter change.** The `test-counts` artifact
gains a `filter` field; a run whose filter does not match the artifact's stored filter falls back
to the hand-measured bootstrap constants and prints a loud notice rather than silently comparing
apples to oranges. `build-test.yml` gains a `workflow_dispatch` input, `measure-filter`, that runs
master under the new filter and prints the count without writing the artifact — that printed
number, and only a number a master run actually produced, is what goes into the bootstrap
constants. This preserves the standing rule that bootstrap constants are measurements, never
arithmetic on a previous constant.

## Alternatives rejected

- **Affected-only test selection as the PR gate.** Rejected: a dependency-graph-derived "which
  tests does this change affect" answer is a heuristic, not a proof, and using it as the merge gate
  means a change that affects a file the graph does not know about — a shared fixture, a
  configuration default, a cross-cutting concern like the config-dir isolation this ADR ships — can
  land without the tests that would have caught it ever running. It remains available as a fast
  local pre-push loop, where a false negative costs a slower CI run rather than a shipped
  regression.
- **Retry once on failure.** Rejected: a retry that passes on the second attempt is indistinguishable
  in the PR's green checkmark from a test that was never flaky, which means a genuinely
  intermittent test accumulates no pressure to get fixed or quarantined — it just costs double the
  CI time forever. Quarantine-with-a-bead makes the flake visible in the PR history and forces a
  tracking issue to exist, where a retry makes it invisible by design.
- **E2E only on master, not required on PRs.** Rejected: master-only E2E means a golden-path
  regression is caught after merge, when it is already on the branch other open PRs are about to
  be rebased onto (branch protection here is `strict: true`). Required-on-PR catches it before
  it can propagate.
- **Keep the existing `/health`-only review-environment check and skip a real E2E framework.**
  Rejected: `/health` proves the process started, not that a search actually returns results
  through the full identity-aware broker path — the golden path is exactly what nothing else in
  the suite exercises end-to-end against a running container.

## Consequences

- `-m:1` (forcing sequential test execution locally, documented in CLAUDE.md §4/§5 as required
  because the suite is "not reliably parallel-safe across assemblies") is retired once the
  isolation work (Component 1: config-dir via `IConfiguration`, `SqliteTestDatabase`, removal of
  `DisableTestParallelization`) lands and Integration passes 20 consecutive parallel local runs.
  Until then, `-m:1` remains the correct local instruction and this ADR does not change it early.
- Branch protection's required-check names must be updated to match the new job names once the PR
  lane becomes multi-job (backend matrix groups, frontend, E2E). That rename is **owner-gated**:
  it is a GitHub repository setting, not something a PR's CI configuration can self-apply, so the
  Phase 3 PR that introduces the matrix cannot merge as a required check itself until the owner
  updates branch protection to point at the new names.
- Nightly failures are visible only as a job summary; beads are local-only (this repo runs beads in
  stealth mode, per CLAUDE.md), so nightly cannot file a bead automatically when it finds a new
  flake. Filing the tracking bead for a newly discovered flake stays a manual step.
- Enforced by: the architecture test forbidding `Quarantine` without `Bead` (Component 4); the
  identical category filter applied in both PR and master lane definitions (so the ratchet floor
  and the PR's own run are always comparable); and the `measure-filter` dispatch input plus its
  loud-notice fallback, which is what stops a silent floor comparison across incompatible filters
  from ever landing.

## Status update (2026-09-11)

The isolation work referenced above (#163, arb-rga) has landed: the suite was run 20/20 times in
parallel on master (cc23d94) with 1521/1521 passing every time (Integration lane 364). The local
`-m:1` requirement described in this ADR's Consequences section is retired accordingly. A local
`dotnet test -m:1` is still the simplest way to get one total comparable to the count CI's
`gate` job ratchets — CI reaches the same number by summing the four matrix groups' `.trx`
files.
