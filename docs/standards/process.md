# Standards — Process

Rules about tests, verification, and getting a change merged. Most of these exist because a test
here has silently failed to test anything, or a command has reported success on a failing build.

---

## Verification commands

Backend, from the repo root:

```bash
dotnet build                 # expect 0 warnings, 0 errors
dotnet test -m:1             # sequential; use for measured counts
```

The root `Directory.Build.props` enables the documentation-file writer solution-wide, so a
malformed XML doc comment (CS157x/CS158x) is a real warning under the "0 warnings" rule above;
CS1591 (missing summary) and CS1573 (incomplete `<param>` list) are suppressed there as the same
"missing documentation" class CS1591 already covers, not malformed XML.

Frontend, from `src/Arbitarr.Web`:

```bash
npm run typecheck            # tsc --noEmit
npm test                     # vitest run
npm run lint                 # eslint --max-warnings=0
```

**Parallel runs are supported** since the test isolation work (#163, arb-rga). A local
`dotnet test -m:1` is still the simplest way to get one total comparable to the count CI's
`gate` job ratchets — CI reaches the same number by summing the four matrix groups' `.trx`
files.

**The fast local path is `scripts/test-affected.sh`.** It maps the changed paths to the test
assemblies that can reach them through the project-reference graph and runs only those, under the
PR lane's filter; `--dry-run` prints the selection without running it. It is a convenience for the
edit loop, not a substitute for the commands above before a push — see [Lanes](#lanes) for what it
may and may not be used for.

**`dotnet build | tail` exits 0 on a failing build** — the pipeline's status is `tail`'s. Redirect
to a file and check `$?` separately. This has masked a real failure more than once, including a
build that passed only because it was stale.

Environment notes: vitest output carries ANSI codes (`sed 's/\x1b\[[0-9;]*m//g'`); git-bash here
has no `bc` and no `python3` (sum with `awk '{s+=$1} END {print s}'`); a bare `grep -rn` over a
worktree scans `node_modules` and times out.

---

## Test-count floors

The floor is **master's last measured count**, and it lives in CI rather than in the tree. There
is no floor file to edit, and none to conflict on.

Every `Build & test` run records what it measured — backend by summing `executed="N"` across the
`.trx` files every matrix group uploaded, frontend from `numPassedTests` in `vitest-report.json` —
into a `test-counts` artifact. Before summing, the `gate` job asserts the downloaded `.trx` set by
basename against `TEST_ASSEMBLIES` (each of the ten exactly once); the `guards` job separately
verifies that the backend matrix groups union to that same list, and that the list matches the
`tests/` tree (see [PR flow](#pr-flow) below and the comments in `build-test.yml`). Every run then
resolves its floor by downloading that artifact from
the latest *successful* `Build & test` run on `master` other than itself, and fails if either count
came in below it. Selecting "other than itself" is what makes a push to master enforce the ratchet
too: master is compared against its own predecessor, so a shrink merged by force is still caught.

Both floors are enforced in the `gate` job, which is the one job holding the `actions: read`
permission the lookup needs — so the pair is always ratcheted against the *same* master run rather
than against two runs two jobs happened to resolve separately.

**You never write a floor by hand.** Adding tests raises the floor automatically once the PR
merges and master measures the new total. A rebase needs no re-measurement, which is the whole
point: two PRs that both add tests no longer conflict, and neither has to re-run both suites to
settle a number.

The one exception is `BOOTSTRAP_BACKEND_FLOOR` / `BOOTSTRAP_FRONTEND_FLOOR` in
`.github/workflows/build-test.yml`, used only when no artifact can be found (the first run after
the ratchet landed, or an expired artifact). Those constants are still **measurements, never
arithmetic** — change them only to a number a master run actually printed, never to the old value
plus the tests you wrote.

Two counts and two parsers, deliberately, so neither suite's count can mask a collapse in the
other. The frontend parses *passed*, not *total*, so a skipped or `todo` test cannot hold the floor
up without running. Both are ratcheted from the same master run, so the pair moves together.

A failing run also uploads evidence the counts alone cannot reconstruct. `Upload failed attempt
evidence (group <group>)` and `Upload failed attempt evidence (frontend)` run `if: failure()`, are
named `failed-attempt-<run_attempt>-<group>` / `-frontend` so each attempt keeps its own copy, and
hold 14 days — long enough that a flake found on a Friday is still investigable (#198, arb-e2x).
The gate never reads them: it downloads `pattern: trx-*`, and these names cannot match, which is
the point rather than a detail — a matching name would be summed into `executed=` a second time
and surface as a floor breach. The frontend count reaches the gate through a job output, not an
artifact, so nothing uploaded here can move it either. A green run never exercises the
`if: failure()` path — a passing test step means `failure()` is false, so the upload step is
skipped by construction, not by luck. `workflow_dispatch`'s `simulate_test_failure` input
(arb-5fs) exercises that path on demand: when set, a step immediately after the test step runs
`exit 1`, gated on `github.event_name == 'workflow_dispatch' && inputs.simulate_test_failure` so
it is structurally impossible to fire on `push` or `pull_request`, which never populate that
input. Triggering it cannot move the test-count floor even on master: `Record test counts` and
`Upload test counts` inherit `success()` and never run on a failed run, and the floor lookup
filters on `status=success`, so running this on a feature branch is prudence rather than the
guard itself.

**Recorded blind spot** (#182 architectural review): a `TEST_FILTER` change that narrows discovery
and execution *consistently* to a nonzero count passes every per-assembly check — the shards agree,
and the sum equals what discovery recorded — so it is caught only by the whole-run ratchet floor,
which a narrowing in one assembly can hide inside another assembly's growth. Per-assembly floors
would close it.

---

## Test categories

Three xunit traits carve tests out of the merge path, and nothing else does. `Category=Timing`
marks a test that asserts elapsed wall time; `Category=Load` marks one that asserts behaviour under
deliberate contention; `Category=Quarantine` marks a known-flaky test that is not allowed to block
merges. **A `Quarantine` trait must always be accompanied by a `Bead` trait matching
`^arb-[a-z0-9]{3,}$`**, and `Arbitarr.Architecture.Tests` fails the build if one ever appears
without it — quarantine is a tracked, visible exception with an owner, never a silent skip. Apply a
trait to the narrowest thing that earns it: a class only when every fact in it is timing- or
load-bound, otherwise the individual `[Fact]`s, so a mixed class keeps its deterministic tests in
the merge path. The PR and master lanes run one identical filter,
`Category!=Timing&Category!=Load&Category!=Quarantine` — identical because the floor a run is
measured against must come from a run that measured the same set, which is also why the
`test-counts` artifact records the `filter` it was produced under and a run whose filter differs
falls back to the bootstrap constants with a loud notice rather than comparing two different
suites. The nightly workflow (arb-rga.8) runs the suite **unfiltered**, so nothing tagged here
stops being run; it stops being run *on the merge path*. There are no automatic retries in any
lane — quarantine-with-a-bead is the only sanctioned way for a flaky test to stop blocking merges.
See [ADR 0011](../adr/0011-test-strategy-lanes-isolation-quarantine.md).

---

## Cecil IL scans in Architecture.Tests

`tests/Arbitarr.Architecture.Tests` carries four test classes that read compiled IL with Mono.Cecil
rather than reflecting over a loaded assembly or grepping source — grep is defeated by an alias, a
fully-qualified name, or a wrapper, and reflection can see the types a method mentions but not the
calls or constructions its body makes. Reading the IL sees the actual `call`/`callvirt`/`newobj`
instruction whatever the source spelled it as. All four open their target assemblies' build output
directly by file path (`BuiltAssemblies.ResolvePath`, arb-hxa) rather than via `ProjectReference`,
because at least one target (`Arbitarr.Host`) fails to reference that way (NU1605); that makes every
scan depend on the solution having been **built first**, so a missing assembly fails the scan loudly
rather than passing vacuously over an empty set. The scanned assembly-name lists themselves live once,
on `BuiltAssemblies.TestAssemblyNames` / `BuiltAssemblies.ProductionAssemblyNames`
(`tests/Arbitarr.Architecture.Tests/BuiltAssemblies.cs`, arb-hxa) — not in the individual test
classes — and are guarded against drift by the `guards` job described under [PR flow](#pr-flow)
below.

- **`ProductionProcessGlobalStateTests`** scans every `src/` assembly named in
  `BuiltAssemblies.ProductionAssemblyNames` and fails on any call to
  `SqliteConnection.ClearAllPools()`. It closes the production half of the pool-hammer ban; see
  [Connection pools are keyed by the full string](data.md#connection-pools-are-keyed-by-the-full-string)
  for what the ban protects and its test-side counterpart.
- **`TestProcessGlobalStateTests`** scans every assembly named in `BuiltAssemblies.TestAssemblyNames`
  (all ten test projects) and fails on the same `ClearAllPools()` call plus
  `Environment.SetEnvironmentVariable` — the second call is banned in test IL only, because two test
  hosts starting concurrently in one process overwrite each other's environment; production
  configuration code has legitimate reasons to set it and is not scanned for it. Also owns the shared
  `FindBannedCalls` IL walk that `ProductionProcessGlobalStateTests` reuses rather than duplicates.
- **`NoInlineDatabaseConnectionStringsTests`** scans `Arbitarr.Data` only and fails any type outside
  its named allow-lists that constructs a `SqliteConnectionStringBuilder` or a `SqliteConnection`. See
  [Connection pools are keyed by the full string](data.md#connection-pools-are-keyed-by-the-full-string)
  for the "trusted by convention" gap this scan deliberately leaves open.
- **`QuarantineTraitTests`** scans every assembly named in `BuiltAssemblies.TestAssemblyNames` for a
  `Category=Quarantine` trait attribute (class- or method-level) that lacks an accompanying
  `Bead=arb-xxx` trait, per [Quarantine](#quarantine) above. It reads attribute rows rather than
  using xunit's own test discovery so that a quarantined test in an assembly that fails to load, or
  one hidden behind a custom discoverer, still cannot escape the check by simply not appearing.

Seven other test classes in the same project fall outside the Cecil IL scans above. Five —
`AiMediaIsolationTests`, `CoreIsolationTests`,
`AssemblyNamingTests`, `DependencyDirectionTests` and `HostIsolationTests` — load assemblies with
`System.Reflection` (`Assembly.LoadFrom`) instead of Cecil; they check reference graphs and naming,
not IL bodies, so they do not need to see inside a method. The other two, `SourceTreeNamingTests` and
`SecretReaderSingleCallerTests`, scan the source tree as text (`.csproj`/`.sln` contents and call-site
line matches respectively), not compiled output at all. None of the seven are Cecil scans and none are
listed above.

---

## Lanes

Three lanes run the tests, and they differ in *what* they run and *what they are allowed to
write*. The decision is [ADR 0011](../adr/0011-test-strategy-lanes-isolation-quarantine.md); this
is the mechanism.

**PR / master lane — `.github/workflows/build-test.yml`.** Runs on every pull request and every
push to master, under the one workflow-level `TEST_FILTER`,
`Category!=Timing&Category!=Load&Category!=Quarantine`. The job layout (`prep` → three-way
`Backend (A|B|C)` matrix → `Frontend` and `Guards` → the `gate` job named **`Build & test`**) is
described in full under [PR flow](#pr-flow); the floor `gate` enforces is carried between runs by
the `test-counts` artifact, described under [Test-count floors](#test-count-floors) — this lane is
its **only** producer.

**Nightly lane — `.github/workflows/nightly.yml`.** Cron `0 3 * * *` (03:00 UTC) plus
`workflow_dispatch`; deliberately no `pull_request` or `push` trigger and not a required check.
Runs the **unfiltered** suite — no `--filter` anywhere — of all ten test assemblies one at a time
(one trx per project, because a single solution-level trx is overwritten by each assembly in
turn), plus the frontend, plus a flake hunt that reruns `Arbitarr.Integration.Tests` and
`Arbitarr.Data.Tests` five times each. A summary job renders per-assembly totals and a failures
table, and compares the trx files *found* against the count each job *expected*; a shortfall or a
missing expectation file raises an **INCOMPLETE** banner above the failures heading, because a
job that died after three of ten assemblies leaves perfectly green trx files behind and would
otherwise read as an all-clear. This lane **never writes the `test-counts` artifact**: the ratchet
selects by artifact name, and an unfiltered count is larger than any filtered run can reach, so
one such artifact would block every subsequent PR. Its artifacts are named `nightly-*`, and that
rule is restated at the top of the file — keep it there.

The nightly trx parser lives **inline in `nightly.yml`** and stays there until **either** a second
consumer needs the failure-table rendering **or** a fifth parser bug is found. Until one of those
triggers fires, extracting it to `scripts/` with fixture tests is premature — a second copy of a
parser nothing else calls is a second place for the next bug to hide. (Recorded from #155's
architecture review.)

**Local lane — `scripts/test-affected.sh`.** Selects test assemblies from the changed paths via
the project-reference graph (rebuilt from the `.csproj` files on every run, never hard-coded) and
runs them under the PR lane's filter, copied verbatim into the script. **It is never referenced by
any workflow, and must not be.** CI runs the full suite so that a mapping bug in this script cannot
narrow what CI checks; the script's job is to make the edit loop faster, and the workflow's job is
to not trust it. A change outside `src/` and `tests/` selects everything, with a printed reason.

### Quarantine

`Category=Quarantine` takes a test off the merge path. The conditions, each of which is a
mechanism rather than a request:

- **A `Bead=arb-xxx` trait is mandatory**, matching `^arb-[a-z0-9]{3,}$`.
  `QuarantineTraitTests` in `tests/Arbitarr.Architecture.Tests` reads every test assembly's
  attributes and fails the build on a `Quarantine` trait without one — at class level or method
  level — and proves its own non-vacuity against a bait class carrying both shapes.
- **Quarantined tests still run**, every night, in the unfiltered lane. Quarantine is "not on the
  merge path", never "not run".
- **Filing the bead is a manual step.** Beads run in stealth mode here (the store never reaches
  the public remote), so nightly cannot file one when it finds a new flake. Whoever quarantines
  a test creates the bead and names it in the trait.
- **No retries and no serialisation as a flake fix.** Not `--retry`, not vitest `retry`, not
  `DisableTestParallelization` or a collection fixture that exists only to stop two tests
  overlapping. A test that fails once fails the PR; the only sanctioned way for a known-flaky test
  to stop blocking merges is quarantine with a bead, and the bead's job is to get it fixed and
  un-quarantined.

See [ADR 0011](../adr/0011-test-strategy-lanes-isolation-quarantine.md) for the decision and the
alternatives it beat.

---

## Non-vacuous assertions

**Every "secret must not appear in X" assertion needs a positive control.**
`Assert.DoesNotContain(secret, body)` passes just as happily when the secret was never in play — an
empty set contains nothing. The test must first demonstrate that a planted secret *would* fail it,
then assert the real response carries none. More absence assertions do not fix a vacuous one.

`LogSecretInjectionTests` is the reference: it asserts `LogMessageCleanser.Replacement` **is
present**, proving the secret reached the cleanser and was scrubbed rather than never arriving.

Asserting the fixture was created (a `201`, a non-null value) proves the secret **exists**. It does
not prove it would be **detectable if it leaked**. Only the second makes the assertion bite.

**Mutation-test them.** This shape has shipped three times:

- #57's webhook test passed with a real leak because it drove an endpoint that bypassed the dispatcher.
- #80's key test passed with a live `debugLastKey` leak because it searched for the *first* minted key while the leak returned the *last*.
- #78 came close.

Non-vacuity discipline alone missed all three; only mutation caught them.

Prove it **without putting vulnerable code in the repository**: a throwaway console project outside
the repo holding both implementations side by side gives the same evidence and leaves nothing
behind. Never mutate files in place, and never leave a mutation uncommitted in a worktree.

**When one such assertion is found vacuous, sweep its whole file** — if the pattern failed once it
was never established.

**Gate logic that needs a positive control lives in a sourced script under `.github/scripts/`**, so
the control invokes the shipped code rather than a verbatim copy of it. Inline workflow bash can only
be exercised by slicing the YAML into a throwaway script, and a control run against that slice is
evidence about the slice — it stays green while the workflow drifts away from it. The shard step and
the test-count gate are therefore
[`shard-filter.sh`](../../.github/scripts/shard-filter.sh) and
[`test-count-gate.sh`](../../.github/scripts/test-count-gate.sh), which the workflow sources and
[`test-count-gate-controls.sh`](../../.github/scripts/test-count-gate-controls.sh) — run by the
`guards` job on every PR — sources too.

The workflow calls **one** name, `run_test_count_gate`, and that function fixes the order its three
checks run in. Ordering that lives in a workflow step can only be *read* by a control; ordering that
lives in the sourced script is *executed* by one, so a reorder fails a control that ran the code
rather than one that parsed the YAML around it.

**Assert per row** where a flag is written per row. "Some row has it" still passes when an
implementation writes one value to all of them.

---

## Coverage expectations

- Every behavioral change needs coverage in the matching `tests/Arbitarr.*.Tests` project.
- Real-world regression cases are first-class. The Bleach arc-relative numbering collision and the
  Ghost in the Shell franchise trio are canonical fixtures. If you fix an identity-resolution bug,
  add the release name that triggered it as a fixture-backed test.
- Fixture data must be fully redacted: `REDACTED` for keys, RFC 5737 (`192.0.2.x`) for hosts.

---

## Git

**`git add -A` will happily stage conflict markers**, and `rebase --continue` accepts them. A local
build can still pass if it is stale. Before pushing a rebase:

```bash
git grep -n -a -P '^(<{7}|={7}|>{7})( |\r?$)'
```

Use `-a` (treat as text; CRLF files can trip binary detection) and `-P` (Perl regex; POSIX
`-E` does not interpret the `\r` escape, so `-E` silently fails to match a bare `=======`
line in a CRLF file such as `Program.cs`, `routes.tsx` or `Settings.tsx`).

...and rebuild fresh.

**Never remove conflict markers with a blind line-delete.** `sed '/^=======$/d'` has twice deleted a
closing brace that sat where the marker was. Edit the region.

**Line endings are mixed on purpose.** `src/Arbitarr.Host/Program.cs` and
`src/Arbitarr.Web/src/routes.tsx` are **CRLF**; most of the repo is LF. Never blanket-normalise.
Verify with a plain `--stat` against `--ignore-all-space --stat` — if they differ, line endings
moved.

**Branch protection is `strict: true`**: every merge puts the other open PRs behind, so they must be
rebased in turn. Expect to serialise.

Commit subjects are short and imperative ("Add XEM season-name provider"). Body only when the *why*
is not obvious from the diff.

---

## PR flow

Two required checks: `Build & test` and `Deploy review environment`.

`Build & test` is the name of the **`gate` job**, not of the whole workflow's work. The workflow runs
five jobs — `prep` (one solution build, published as a `test-build` artifact), a three-way `backend`
matrix (A = Integration, B = Data + Api + Core, C = Host + Media + Ai + Core.Identity +
Sources.NzbHydra + Architecture), `frontend`, and `guards` — and `gate` aggregates them. Only `gate`
carries a required-check name, so branch protection needs no edit when the job layout changes again.

Three properties of that arrangement are load-bearing and easy to undo by accident. `gate` runs with
`if: always()`, so it must check every needed job's `result` **explicitly** — branch protection reads
a *skipped* required check as satisfied, so a gate that merely inherited its dependencies' status
would let a red matrix group merge. Every backend group restores the *full* `test-build`
artifact rather than only its own assemblies, because `Arbitarr.Architecture.Tests`' Mono.Cecil IL
scan reads sibling build output and needs all ten test assemblies present in the same configuration
and TFM. And `guards` runs four checks that keep the hand-maintained assembly lists honest: that the matrix
groups union to exactly `TEST_ASSEMBLIES`; that `TEST_ASSEMBLIES` itself matches the `*.Tests.csproj`
projects under `tests/`; that every `TEST_ASSEMBLIES` project is present in `Arbitarr.sln` and
declares no `<AssemblyName>` override; and that `BuiltAssemblies.ProductionAssemblyNames` (the src/
assemblies `Arbitarr.Architecture.Tests` scans by file path) still matches the csproj's
`ProjectReference` graph plus its one documented exception — see [Test-count
floors](#test-count-floors) above and the comments in `build-test.yml`.

The scan-list guard lives in `guards` rather than in `Arbitarr.Architecture.Tests` because the
invariant compares the hand-written scan list against the csproj's reference graph, and a running
test can only observe assemblies that graph already delivered — so an in-test check is blind to
exactly the graph-invisible assembly (`Arbitarr.Host`, NU1605) the guard exists to police.

**A green `Deploy review environment` means the image builds and `/health` answers — nothing more.**
Nothing is deployed; no review environment exists. The name describes an intent.

For agent-opened PRs:

- Open every PR as a **draft**. Mark it ready only once the first CI run passes both required
  checks. A PR whose first run fails stays in draft while the fix lands.
- Merging requires **both** a passing code review and a passing architectural review.
- **State superseded numbers as superseded.** If a rebase changes measured counts, update the body
  rather than leaving earlier figures to read as current.

---

## The repository is public

Issue and PR bodies are as public as the code. The secrets policy applies to them too — not just to
committed files.
