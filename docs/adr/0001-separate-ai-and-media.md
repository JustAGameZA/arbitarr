# 0001. Keep `Arbitarr.Ai` and `Arbitarr.Media` mutually unreferencing

- **Status:** Accepted
- **Date:** 2026-08-26

## Context

Arbitarr resolves identity two ways. `Arbitarr.Media` does it deterministically — provider
lookups, numbering candidates, an ambiguity policy — and every one of its outcomes is reproducible
from its inputs. `Arbitarr.Ai` asks a local LLM to arbitrate the cases the deterministic path
cannot settle, and its outcomes are not reproducible in that sense: the same input can yield a
different verdict, and no assertion can pin the answer without pinning the model.

**2026-09-11 note:** arb-p4r pins temperature 0 and a fixed seed, so given a fixed model the
decoding is now deterministic — the non-reproducibility premise above is weaker than when this
ADR was written. The boundary now rests on the Decision and Consequences below (CI needs no
Ollama; shadow mode), not on non-reproducibility.

The pressure to join them is constant and reasonable-sounding. The arbitration prompt wants the
numbering candidates. The scorer would like a confidence hint from the model. Each individual
crossing looks small.

## Decision

Neither project references the other, in either direction. Data that must cross moves through
`Arbitarr.Core` contracts.

`tests/Arbitarr.Architecture.Tests/AiMediaIsolationTests.cs` asserts both directions
(`Ai_Does_Not_Reference_Media`, `Media_Does_Not_Reference_Ai`) with NetArchTest, so the boundary
fails the build rather than eroding through review fatigue.

## Alternatives rejected

- **Let `Ai` reference `Media`** (the tempting direction — arbitration genuinely wants candidate
  data). Rejected: it makes the deterministic layer's public shape hostage to prompt construction,
  and every future prompt change becomes a reason to widen a domain type.
- **A shared "identity+AI" project.** Rejected: it deletes the distinction rather than managing it.
  Once the two live together there is no longer a testable core, because nothing marks where
  reproducibility stops.
- **A convention documented but unenforced.** Rejected: this exact boundary is crossed by small,
  individually-defensible edits. A rule that is not a test is a rule that decays.

## Consequences

- Crossing data needs a `Core` contract, which is deliberate friction — it forces the question of
  what the two layers actually share.
- The deterministic path stays fully testable without a model, so CI needs no Ollama.
- Shadow mode is possible at all: arbitration can run and record verdicts without touching results,
  because results do not depend on it.
