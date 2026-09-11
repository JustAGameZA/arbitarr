## What & why

<!-- What does this change do, and what problem does it solve? Link the issue: Fixes #NNN -->

## How it was tested

<!-- Which tests cover this? New tests added, existing suites run. "dotnet test passes" plus anything manual. -->

## Checklist

- [ ] `dotnet build` and `dotnet test -m:1` pass locally (use `-m:1` for the measured count; CI derives the same ratcheted count by summing the matrix groups' `.trx` files)
- [ ] UI changes: `npm run typecheck`, `npm test`, and `npm run lint` pass in `src/Arbitarr.Web`
- [ ] Behavioral changes have test coverage in the matching `tests/Arbitarr.*.Tests` project
- [ ] Test-count floors, if raised, hold a **measured** number — not the old floor plus tests added
- [ ] No new reference between `Arbitarr.Ai` and `Arbitarr.Media` (either direction)
- [ ] Degraded paths record provenance flags — no silent `null`s or best-effort guesses
- [ ] New admin routes are classified `AdminMutating` and bind their body optionally (`EmptyBodyBehavior.Allow`)
- [ ] Any "secret must not appear" assertion has a positive control proving it would fail on a real leak
- [ ] No secrets, API keys, or real network addresses (use `192.0.2.x` placeholders; fixtures use `REDACTED`)
