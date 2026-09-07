# Arbitarr — working agreements

Identity-aware Torznab/Newznab search broker between NZBHydra2 and Sonarr/Radarr.
.NET 10 backend (`src/`), React + TypeScript frontend (`src/Arbitarr.Web`).

**This repository is PUBLIC.** Every push, issue body and PR body is published.

Each rule below is here because breaking it cost real rework. Where a rule looks
like it could be simplified away, the reason it cannot is stated with it.

---

## 1. Secrets and topology

- **Never commit real LAN addresses.** Use the RFC 5737 documentation range
  `192.0.2.x`, or `REDACTED`. This applies to **GitHub issue and PR bodies too** —
  they are as public as the code.
- `.githooks/pre-commit` enforces this (RFC1918 addresses, credential shapes,
  AWS/GitHub/JWT/PEM patterns) and runs in CI as part of `Build & test`.
- **`--no-verify` is forbidden.** If the hook trips on a test fixture, rename the
  fixture (`placeholder-*`), do not bypass the hook.
- The admin key is **session-only in Zustand** — never `localStorage`, never
  `sessionStorage`, never a query string. `apiFetch` attaches it by path prefix.
- Source API keys are **write-only rows** in `Settings` under `source:{id}:api_key`.
  That colon-namespaced name cannot be produced by any `SettingKey` enum value, so
  they can never surface on `GET /api/admin/settings`. **Preserve that property** —
  it is the mechanism, not a coincidence.
- `SourceRepository.ReadApiKeyForUpstreamRequestAsync` must have **exactly one caller**.

## 2. Admin routes

- Bind bodies **optionally**:
  `[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] T? request`.
  A required body is rejected *before* `AdminApiKeyFilter` runs, which leaks
  400-vs-503 and tells an unauthenticated caller whether a route exists.
- `AdminApiKeyRouteEnumerationTests` **sends no body on purpose.** Never add one —
  sending a body is exactly what stops it detecting the leak above.
- That sweep does **not** cover `{id}`-templated routes. Name those explicitly in
  tests when you add them.
- Classify routes by **`RouteClassification` / path prefix, never by HTTP verb.**

## 3. Parsing user-supplied enums

Do **not** use `Enum.TryParse` on a wire value that selects an authority level.
It accepts the numeric form, so `{"scope":"1"}` mints an `Admin` key through an
input shape no caller is documented to have.

Neither guard people reach for first actually closes it: `Enum.IsDefined` fails
because `1` **is** defined, and trimming fails because `" 1 "` and `"+1"` parse too.
Match the names explicitly, so the wire format is closed by construction.

## 4. Tests

- **Test-count floors are measurements, never arithmetic.** `tests/test-count-floor.txt`
  and `tests/frontend-test-count-floor.txt` hold the number a run *printed*. After a
  rebase, **re-measure** — different branches legitimately measure different totals.
  Never adjust a floor by adding the number of tests you wrote.
- **Every "secret must not appear in X" assertion needs a positive control.**
  `Assert.DoesNotContain(secret, body)` passes just as happily when the secret was
  never in play — an empty set contains nothing. The test must first demonstrate that
  a planted secret in that response *would* fail it, then assert the real response
  carries none. Adding more absence assertions does not fix a vacuous one.
  - `LogSecretInjectionTests` is the reference: it asserts
    `LogMessageCleanser.Replacement` **is present**, proving the secret reached the
    cleanser and was scrubbed rather than never arriving.
  - Asserting the fixture was created (a `201`, a non-null value) proves the secret
    **exists**. It does not prove the secret would be **detectable if it leaked**.
    Those are different properties and only the second makes the assertion bite.
- **Mutation-test them.** This shape has shipped three times: #57's webhook test passed
  with a real leak because it drove an endpoint that bypassed the dispatcher; #80's
  key test passed with a live `debugLastKey` leak because it searched for the *first*
  minted key while the leak returned the *last*; #78 came close. In each case
  non-vacuity discipline alone missed it and only mutation caught it.
  - Prove non-vacuity **without putting vulnerable code in the repository**: a
    throwaway console project outside the repo holding both implementations side by
    side gives the same evidence and leaves nothing behind. Never mutate files in
    place, and never leave a mutation uncommitted in a worktree.
  - When one such assertion is found vacuous, **sweep its whole file** rather than
    fixing the named test. If the pattern failed once it was never established.
- Assert **per row** where a flag is written per row. A test that checks "some row has
  it" still passes when an implementation writes one value to all of them.

## 5. Build and measurement commands

- **`dotnet build | tail` exits 0 on a failing build** — the pipeline's status is
  `tail`'s. Redirect to a file, then check `$?` separately.
- vitest output carries ANSI codes; strip with `sed 's/\x1b\[[0-9;]*m//g'`.
- No `bc` and no `python3` in git-bash. Sum with `awk '{s+=$1} END {print s}'`.
- Bare `grep -rn` over a worktree scans `node_modules` and times out. Use ripgrep or
  the editor's search tool.

## 6. Git

- **`git add -A` will happily stage conflict markers**, and `rebase --continue`
  accepts them. A local build can still pass if it is stale. Before pushing a
  rebase, always:
  ```
  git grep -n -E '^(<{7}|={7}|>{7})( |$)'
  ```
  and rebuild fresh.
- **Never remove conflict markers with a blind line-delete.** `sed '/^=======$/d'`
  has twice deleted a closing brace that sat where the marker was. Edit the region.
- Branch protection is `strict: true`: every merge puts the other open PRs behind,
  so they must be rebased in turn. Expect to serialise.
- Line endings are mixed on purpose. `src/Arbitarr.Host/Program.cs` and
  `src/Arbitarr.Web/src/routes.tsx` are **CRLF**; most of the repo is LF.
  **Never blanket-normalise.** Verify with a plain `--stat` against
  `--ignore-all-space --stat` — if they differ, line endings moved.
- Every worker gets **its own git worktree**. Never share a checkout.
- When several agents do share one, `git status` tells you **what** changed and never
  **who** changed it. A reviewer running a mutation test looks identical to a worker
  leaving residue. Ask who owns an unexpected change before attributing it — inferring
  the author from motive gets it wrong. What matters for safety is whether it was
  committed or pushed: check `git show <pushed-sha>` before raising anything.

## 7. Architecture

- `Arbitarr.Host` is the **sole DI composition root**.
- `Arbitarr.Core` may not reference any other Arbitarr project;
  `Arbitarr.Architecture.Tests.CoreIsolationTests` enforces it. That is why
  `RecordedEventKind` mirrors `EventKind` rather than reusing it.
- Recording history must never break the operation that produced it — `IEventSink`
  implementations swallow their failures. The suppression audit log is the exception:
  it is written transactionally on the request path.
- No colour literals in any `*.module.css` — design tokens only.

## 8. PR flow

- Open every PR as a **draft**. Mark ready only once the first CI run passes both
  required checks: `Build & test` and `Deploy review environment`.
- A PR whose first run fails stays in draft while the fix lands.
- Merging requires **both** a passing code review and a passing architectural review.
- Commit messages end with:
  ```
  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  Claude-Session: <the commissioning session's URL>
  ```
  The session trailer identifies the **commissioning** session — a subagent must not
  substitute its own id.
- PR bodies end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- State superseded numbers as superseded. If a rebase changes the measured counts,
  update the PR body rather than leaving the earlier figures to be read as current.
