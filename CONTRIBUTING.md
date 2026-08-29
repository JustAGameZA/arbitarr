# Contributing to Arbitarr

Thanks for your interest! Arbitarr is in early, fast-moving development, so the ground rules below matter more than usual — the codebase changes quickly and correctness claims are backed by tests and fixtures, not vibes.

## Before you start

- **Open an issue first** for anything beyond a typo fix. The architecture is still settling; a short discussion up front avoids building on a moving floor.
- Check the [issue tracker](https://github.com/JustAGameZA/arbitarr/issues) for existing discussion.

## Development setup

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then:

```bash
git clone https://github.com/JustAGameZA/arbitarr.git
cd arbitarr
dotnet build
dotnet test
```

No external services are required for the test suite — upstream responses are captured as redacted fixtures under `docs/fixtures/`.

## Project conventions

### Architecture boundaries

Project boundaries are enforced by `tests/Arbitarr.Architecture.Tests` (NetArchTest). The most important rule: **`Arbitarr.Ai` and `Arbitarr.Media` must never reference each other**, in either direction. Identity resolution is deterministic and testable; LLM arbitration is not. Keeping them separate is a design invariant, not a style preference. If your change needs data to cross that boundary, it flows through `Arbitarr.Core` contracts.

### Fail loud, degrade visibly

Arbitarr never silently guesses. Any code path that degrades (source unreachable, no mapping coverage, ambiguous mapping) must record that fact in `MatchProvenance` flags rather than collapsing into a bare `null` or a best-effort match. When a mapping is genuinely ambiguous, the correct behavior is to admit *no* match and say why. PRs that trade correctness-transparency for convenience will be asked to rework.

### Tests

- Every behavioral change needs test coverage in the matching `tests/Arbitarr.*.Tests` project.
- Real-world regression cases are first-class: the Bleach arc-relative numbering collision and the Ghost in the Shell franchise trio are canonical fixtures. If you fix an identity-resolution bug, add the release name that triggered it as a fixture-backed test.
- Fixture data must be fully redacted — see the secrets policy below.

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

## Commit messages

Short imperative subject line ("Add XEM season-name provider", not "Added..." or "misc fixes"). Body only when the *why* isn't obvious from the diff.

## Naming note

The solution currently uses the working name `Arbitarr` internally; a rename to `Arbitarr` is planned. Don't pre-empt it piecemeal — new code follows the existing `Arbitarr.*` naming until the coordinated rename lands.

## Questions?

Open a [discussion issue](https://github.com/JustAGameZA/arbitarr/issues/new/choose). For security reports, see [SECURITY.md](SECURITY.md) — do not open a public issue.
