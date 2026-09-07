# Standards — Process

Rules about tests, verification, and getting a change merged. Most of these exist because a test
here has silently failed to test anything, or a command has reported success on a failing build.

---

## Verification commands

Backend, from the repo root:

```bash
dotnet build                 # expect 0 warnings, 0 errors
dotnet test -m:1             # sequential
```

Frontend, from `src/Arbitarr.Web`:

```bash
npm run typecheck            # tsc --noEmit
npm test                     # vitest run
npm run lint                 # eslint --max-warnings=0
```

**The suite is not reliably parallel-safe across assemblies** (shared SQLite and port state). A
parallel run under-counts *and* invents failures. Measure with `-m:1`, as CI does.

**`dotnet build | tail` exits 0 on a failing build** — the pipeline's status is `tail`'s. Redirect
to a file and check `$?` separately. This has masked a real failure more than once, including a
build that passed only because it was stale.

Environment notes: vitest output carries ANSI codes (`sed 's/\x1b\[[0-9;]*m//g'`); git-bash here
has no `bc` and no `python3` (sum with `awk '{s+=$1} END {print s}'`); a bare `grep -rn` over a
worktree scans `node_modules` and times out.

---

## Test-count floors

`tests/test-count-floor.txt` (backend, parsed from `executed="N"` in the `.trx`) and
`tests/frontend-test-count-floor.txt` (frontend, parsed from `numPassedTests` in
`vitest-report.json`) hold the minimum that must pass.

**Floors are measurements, never arithmetic.** The files hold the number a run *printed*. Never
adjust a floor by adding the number of tests you wrote. After a rebase, **re-measure** — different
branches legitimately measure different totals.

Two files and two parsers, deliberately, so neither suite's count can mask a collapse in the other.
The frontend parses *passed*, not *total*, so a skipped or `todo` test cannot hold the floor up
without running.

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
git grep -n -E '^(<{7}|={7}|>{7})( |$)'
```

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
