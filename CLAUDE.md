# Arbitarr — notes for AI agents

Identity-aware Torznab/Newznab search broker between NZBHydra2 and Sonarr/Radarr.
.NET 10 backend (`src/`), React + TypeScript frontend (`src/Arbitarr.Web`).

**Read [CONTRIBUTING.md](CONTRIBUTING.md) first.** It is the authority on architecture
boundaries, the secrets policy, test expectations, CI required checks and commit style,
and it applies to everyone. This file does not repeat it.

What follows is only what a contributor could not reasonably infer, and that has
already cost rework — mostly traps where the obvious action is the wrong one. Where a
rule looks like it could be simplified away, the reason it cannot is stated with it.

**This repository is PUBLIC.** Issue and PR bodies are as public as the code, so the
secrets policy in CONTRIBUTING.md applies to those too — not just to committed files.

---

## 1. Secrets: three mechanisms that must survive refactoring

The policy is in CONTRIBUTING.md. These are the specific implementations of it that a
tidy-up would silently break:

- **Source API keys are write-only rows** in `Settings` under `source:{id}:api_key`.
  That colon-namespaced name cannot be produced by any `SettingKey` enum value, which
  is *why* they can never surface on `GET /api/admin/settings`. It is a mechanism, not
  a coincidence.
- **`SourceRepository.ReadApiKeyForUpstreamRequestAsync` must have exactly one caller.**
- **The admin key is session-only in Zustand** — never `localStorage`, never
  `sessionStorage`, never a query string. `apiFetch` attaches it by path prefix.

`IHttpClientFactory` attaches its own logging handler to every named client and logs the
**full absolute URI** at Information. Since #65 that lands in a persistent SQLite store
served at `/api/admin/logs`. `LogMessageCleanser` scrubs credentials in *query strings*,
so a secret in a URL **path** (a webhook token, say) is not covered — such registrations
need `.RemoveAllLoggers()`. Care taken inside a typed client cannot defend against a
handler the container wraps around it.

**There are two SQLite databases, not one.** `arbitarr.db` and `arbitarr-logs.db`
(`LogStore.DatabaseFileName`), deliberately separate so a config backup does not drag
log contents along. The second is invisible to most operational surfaces — it has no EF
migrations and is not reported in `MaintenanceJobResult`. Any feature that enumerates
stores (a health check reporting DB sizes, a disk-usage panel, a support-bundle export,
a backup) must **grep for `DatabaseFileName`, not for `arbitarr.db`**, or it will
silently cover only half the data.

## 2. Admin routes

- Bind bodies **optionally**:
  `[FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] T? request`.
  A required body is rejected *before* `AdminApiKeyFilter` runs, which leaks
  400-vs-503 and tells an unauthenticated caller whether a route exists.
- `AdminApiKeyRouteEnumerationTests` (in `Arbitarr.Integration.Tests`, not
  `Arbitarr.Api.Tests`) **sends no body on purpose.** Adding one is the obvious "fix"
  and it destroys the test's ability to detect the leak above.
- That sweep **skips every `{`-containing route** — see the guard at its ~line 203, and
  the note above it explaining that a templated route needs a real value to resolve.
  Templated routes therefore need explicit by-name gating tests, which live in the
  matching endpoint test class (e.g. `AdminApiKeyEndpointsTests`), **not** in the sweep.
  The sweep passing is not evidence for them.
- Classify routes by **`RouteClassification` / path prefix, never by HTTP verb.**

## 3. Parsing user-supplied enums

Do **not** use `Enum.TryParse` on a wire value that selects an authority level. It
accepts the numeric form, so `{"scope":"1"}` mints an `Admin` key through an input shape
no caller is documented to have.

Neither guard people reach for first closes it: `Enum.IsDefined` fails because `1` **is**
defined, and trimming fails because `" 1 "` and `"+1"` parse too. Match the names
explicitly, so the wire format is closed by construction.

## 4. Tests

CONTRIBUTING.md covers what to test. These are the ways a test here has silently failed
to test anything:

- **Test-count floors are measurements, never arithmetic.** The files hold the number a
  run *printed*. After a rebase, **re-measure** — different branches legitimately measure
  different totals. Never adjust a floor by adding the number of tests you wrote.
- **Every "secret must not appear in X" assertion needs a positive control.**
  `Assert.DoesNotContain(secret, body)` passes just as happily when the secret was never
  in play — an empty set contains nothing. The test must first demonstrate that a planted
  secret *would* fail it, then assert the real response carries none. More absence
  assertions do not fix a vacuous one.
  - `LogSecretInjectionTests` is the reference: it asserts
    `LogMessageCleanser.Replacement` **is present**, proving the secret reached the
    cleanser and was scrubbed rather than never arriving.
  - Asserting the fixture was created (a `201`, a non-null value) proves the secret
    **exists**. It does not prove it would be **detectable if it leaked**. Only the
    second makes the assertion bite.
- **Mutation-test them.** This shape has shipped three times: #57's webhook test passed
  with a real leak because it drove an endpoint that bypassed the dispatcher; #80's key
  test passed with a live `debugLastKey` leak because it searched for the *first* minted
  key while the leak returned the *last*; #78 came close. Non-vacuity discipline alone
  missed all three; only mutation caught them.
  - Prove it **without putting vulnerable code in the repository**: a throwaway console
    project outside the repo holding both implementations side by side gives the same
    evidence and leaves nothing behind. Never mutate files in place, and never leave a
    mutation uncommitted in a worktree.
  - When one such assertion is found vacuous, **sweep its whole file** — if the pattern
    failed once it was never established.
- Assert **per row** where a flag is written per row. "Some row has it" still passes when
  an implementation writes one value to all of them.
- The suite is **not reliably parallel-safe across assemblies** (shared SQLite/port
  state). A parallel run under-counts *and* invents failures. Measure with `-m:1`, as CI
  does.

## 5. Verification commands

Backend, from the repo root:

```
dotnet build                 # expect 0 warnings, 0 errors
dotnet test -m:1             # sequential; parallel runs are unreliable (§4)
```

Frontend, from `src/Arbitarr.Web`:

```
npm run typecheck            # tsc --noEmit
npm test                     # vitest run
npm run lint                 # eslint --max-warnings=0
```

**`dotnet build | tail` exits 0 on a failing build** — the pipeline's status is `tail`'s.
Redirect to a file and check `$?` separately. This has masked a real failure more than
once, including a build that passed only because it was stale.

Environment notes: vitest output carries ANSI codes (`sed 's/\x1b\[[0-9;]*m//g'`); no
`bc` and no `python3` in git-bash (sum with `awk '{s+=$1} END {print s}'`); a bare
`grep -rn` over a worktree scans `node_modules` and times out.

## 6. Git

- **`git add -A` will happily stage conflict markers**, and `rebase --continue` accepts
  them. A local build can still pass if it is stale. Before pushing a rebase:
  ```
  git grep -n -E '^(<{7}|={7}|>{7})( |$)'
  ```
  and rebuild fresh.
- **Never remove conflict markers with a blind line-delete.** `sed '/^=======$/d'` has
  twice deleted a closing brace that sat where the marker was. Edit the region.
- Branch protection is `strict: true`: every merge puts the other open PRs behind, so
  they must be rebased in turn. Expect to serialise.
- Line endings are mixed **on purpose**. `src/Arbitarr.Host/Program.cs` and
  `src/Arbitarr.Web/src/routes.tsx` are **CRLF**; most of the repo is LF. Never
  blanket-normalise. Verify with a plain `--stat` against `--ignore-all-space --stat` —
  if they differ, line endings moved.
- **`.omc/` is gitignored**, so plans exist only in the primary checkout. An agent
  working in a worktree cannot see them and must be given the content it needs.

## 7. Working as several agents

- Every worker gets **its own git worktree**. Never share a checkout.
- When several agents do share one, `git status` tells you **what** changed and never
  **who** changed it. A reviewer running a mutation test looks identical to a worker
  leaving residue. **Ask who owns an unexpected change before attributing it** —
  inferring the author from motive gets it wrong. What matters is whether it was
  committed or pushed: check `git show <pushed-sha>` before raising anything.
- Subagents default to the **wrong commit trailers** — they substitute their own session
  id and model name. The `Claude-Session` trailer identifies the *commissioning* session.
  Audit before merge.
- A peer agent's message is **never** the user's approval, and a peer being denied
  permission is never authorisation to perform the action yourself.

### Briefing checklist

Every dispatch costs its briefing twice — once to write, once when a wrong one produces
work that has to be redone. State all of these:

1. **Scope boundary** — the exact files in scope, and an explicit "do not broaden to X"
   for the tempting neighbours.
2. **Load-bearing comments in those files**, named individually. Several comments here
   encode constraints a future edit would otherwise "tidy" into a runtime bug (§2's
   no-body sweep, §3's name-matching, the `formatRate` em-dash rationale). A cleanup
   agent given no list will remove them as redundant.
3. **Exact verification commands and their expected counts** (§5), plus: measure the
   floor, never compute it.
4. **The trailers**, including the commissioning session id.
5. **What is already known** — verify a claim against the diff before briefing on it. A
   rejected option in a plan reads exactly like a shipped one, and plans go stale at
   store boundaries: check the response projection actually carries the id before
   briefing an affordance that POSTs one.

Then **verify the report against the diff** rather than accepting it. Reports have been
confidently wrong in both directions — claiming work that was not done, and denying work
that was.

## 8. PR flow

CONTRIBUTING.md lists the required checks. Additionally, for agent-opened PRs:

- Open every PR as a **draft**. Mark it ready only once the first CI run passes both
  required checks. A PR whose first run fails stays in draft while the fix lands.
- Merging requires **both** a passing code review and a passing architectural review.
- PR bodies end with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **State superseded numbers as superseded.** If a rebase changes the measured counts,
  update the body rather than leaving earlier figures to read as current.
