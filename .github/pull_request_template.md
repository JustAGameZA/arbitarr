## What & why

<!-- What does this change do, and what problem does it solve? Link the issue: Fixes #NNN -->

## How it was tested

<!-- Which tests cover this? New tests added, existing suites run. "dotnet test passes" plus anything manual. -->

## Checklist

- [ ] `dotnet build` and `dotnet test` pass locally
- [ ] Behavioral changes have test coverage in the matching `tests/Arbitarr.*.Tests` project
- [ ] No new reference between `Arbitarr.Ai` and `Arbitarr.Media` (either direction)
- [ ] Degraded paths record provenance flags — no silent `null`s or best-effort guesses
- [ ] No secrets, API keys, or real network addresses (use `192.0.2.x` placeholders; fixtures use `REDACTED`)
