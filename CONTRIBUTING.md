# Contributing to Arbitarr

Thanks for your interest! Arbitarr is in early, fast-moving development, so the ground rules below matter more than usual — the codebase changes quickly and correctness claims are backed by tests and fixtures, not vibes.

## Before you start

- **Open an issue first** for anything beyond a typo fix. The architecture is still settling; a short discussion up front avoids building on a moving floor.
- Check the [issue tracker](https://github.com/JustAGameZA/arbitarr/issues) for existing discussion.
- Read [CONTEXT.md](CONTEXT.md) for the project's vocabulary — identity, numbering schemes, provenance flags, the three distinct API keys, and the terms whose meaning here differs from their everyday one.
- [docs/business/](docs/business/) explains why the domain is shaped this way; [docs/adr/](docs/adr/) records the decisions that are hard to reverse, each with the alternatives it beat; [docs/standards/](docs/standards/) holds the checkable rules in full, with their reasoning.

## Development setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then:

```bash
git clone https://github.com/JustAGameZA/arbitarr.git
cd arbitarr
dotnet build
dotnet test
```

No external services are required for the test suite — upstream responses are captured as redacted fixtures under `docs/fixtures/`.

Run the backend suite sequentially. It is not reliably parallel-safe across assemblies (shared SQLite and port state), and a parallel run both under-counts and invents failures:

```bash
dotnet test -m:1
```

### Frontend

The admin UI lives in `src/Arbitarr.Web` (React + TypeScript + Vite). From that directory:

```bash
npm ci
npm run typecheck    # tsc --noEmit
npm test             # vitest run
npm run lint         # eslint --max-warnings=0
```

`src/Arbitarr.Web/README.md` is the authority on the frontend and covers rules that are not
obvious from the code: `src/styles/theme.css` is normative and the only file permitted to hold
colour literals, the admin key is session-only Zustand (never `localStorage`, never
`sessionStorage`, never a query string), admin access is gated by path prefix and never by HTTP
verb, and `test.css: true` is load-bearing for the theme assertions. Read it before changing UI
code.

### Shareable pre-commit hook

The secret/topology guard described below lives at `.git/hooks/pre-commit`, which is not
tracked by git and so isn't installed automatically when you clone the repo. A tracked,
byte-identical copy lives at `.githooks/pre-commit`. Point git at it once per clone:

```bash
git config core.hooksPath .githooks
```

CI also re-runs this guard's checks over the whole tree on every PR (the `Build & test`
required check), so a missing local hook can't let something slip through — but installing it
locally catches problems before you push.

## Project conventions

### Architecture boundaries

Project boundaries are enforced by `tests/Arbitarr.Architecture.Tests` (NetArchTest). The most important rule: **`Arbitarr.Ai` and `Arbitarr.Media` must never reference each other**, in either direction. Identity resolution is deterministic and testable; LLM arbitration is not. Keeping them separate is a design invariant, not a style preference. If your change needs data to cross that boundary, it flows through `Arbitarr.Core` contracts.

### Fail loud, degrade visibly

Arbitarr never silently guesses. Any code path that degrades (source unreachable, no mapping coverage, ambiguous mapping) must record that fact in `MatchProvenance` flags rather than collapsing into a bare `null` or a best-effort match. When a mapping is genuinely ambiguous, the correct behavior is to admit *no* match and say why. PRs that trade correctness-transparency for convenience will be asked to rework.

### Tests

- Every behavioral change needs test coverage in the matching `tests/Arbitarr.*.Tests` project.
- Real-world regression cases are first-class: the Bleach arc-relative numbering collision and the Ghost in the Shell franchise trio are canonical fixtures. If you fix an identity-resolution bug, add the release name that triggered it as a fixture-backed test.
- Fixture data must be fully redacted — see the secrets policy below.
- **Test-count floors are measurements, never arithmetic.** `tests/test-count-floor.txt` (backend, parsed from `executed="N"` in the `.trx`) and `tests/frontend-test-count-floor.txt` (frontend, parsed from `numPassedTests` in `vitest-report.json`) hold numbers a run actually printed. Raise a floor to a value you measured — never to the old value plus the tests you wrote — and re-measure after a rebase, since different branches legitimately measure different totals. Two files and two parsers, deliberately, so neither suite's count can mask a collapse in the other.
- **Any "this secret must not appear in X" assertion needs a positive control.** `Assert.DoesNotContain(secret, body)` passes just as happily when the secret was never in play. Demonstrate first that a planted secret *would* fail the assertion, then assert the real response carries none. `LogSecretInjectionTests` is the reference: it asserts `LogMessageCleanser.Replacement` **is present**, proving the secret reached the cleanser and was scrubbed rather than never arriving. Asserting the fixture exists (a `201`, a non-null value) proves only that — not that a leak would be detected.

## Secrets and network topology — hard rule

**Nothing sensitive is ever committed.** That includes API keys, credentials, and real network addresses (even private RFC 1918 LAN IPs — they leak topology).

- Use RFC 5737 documentation addresses (`192.0.2.x`) in examples, docs, and fixtures.
- Use the literal string `REDACTED` for API keys in captured fixtures.
- A pre-commit guard blocks LAN IPs and credential-looking strings; GitHub push protection backs it up server-side. Don't bypass either — if the guard blocks you, fix the content, not the hook.

## Submitting changes

1. Fork and create a topic branch.
2. Keep PRs focused — one logical change per PR.
3. Make sure `dotnet build` and `dotnet test` pass locally.
4. Fill in the PR template, including which tests cover the change.
5. Expect review feedback to focus on the invariants above (boundaries, provenance, redaction) first.

### CI required checks

Every PR must pass two required checks before merge:

- **`Build & test`** — restores, builds (`dotnet build -m:1`, sequential to bound memory use),
  and runs the full test suite, backend and frontend. It also enforces both test-count floors
  (`tests/test-count-floor.txt` and `tests/frontend-test-count-floor.txt`)
  so the suite can't silently shrink, and re-runs the secret/topology guard over the whole tree.
- **`Deploy review environment`** — builds the container image from `Dockerfile` and smoke-checks
  that the running container answers `GET /health`. **A green tick here means the image builds
  and `/health` answers — nothing more.** It does not mean anything was deployed anywhere; no
  review environment exists, and CI never reaches the Unraid deployment target. See the comment
  at the top of `.github/workflows/deploy-review.yml` for the full explanation of how this
  check's meaning grows as later milestones land.

## Commit messages

Short imperative subject line ("Add XEM season-name provider", not "Added..." or "misc fixes"). Body only when the *why* isn't obvious from the diff.

## Questions?

Open a [discussion issue](https://github.com/JustAGameZA/arbitarr/issues/new/choose). For security reports, see [SECURITY.md](SECURITY.md) — do not open a public issue.
