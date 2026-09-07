# Architecture Decision Records

One file per decision, `NNNN-<slug>.md`, numbered in the order they were accepted.

## What belongs here

A decision earns an ADR when it passes all three parts of the test:

1. **Hard to reverse** — undoing it means changing several files, a data shape, or a public surface.
2. **Surprising without context** — a competent reader would otherwise ask "why on earth is it done this way", or would "tidy" it into a bug.
3. **A real tradeoff** — a reasonable alternative existed and was rejected for a stated reason.

A decision with no rejected alternative is not an ADR; it is just the code. A decision that is
easy to reverse is not an ADR; change it and move on.

## What does not belong here

- Vocabulary — that is [CONTEXT.md](../../CONTEXT.md).
- Checkable rules that apply to every change — those are [docs/standards/](../standards/).
- Domain background belongs in the ADR's own Context section, where it explains the decision it
  forced — not in a separate article restating the same argument.
- Anything the code already says plainly.

## Format

```markdown
# NNNN. <Title in the imperative>

- **Status:** Accepted | Superseded by [NNNN](NNNN-slug.md)
- **Date:** YYYY-MM-DD

## Context
What forced a decision. The constraint, not the solution.

## Decision
What was decided, stated so it can be checked against the code.

## Alternatives rejected
Each one, with the reason it lost. This is the part that stops the decision
being re-litigated every six months.

## Consequences
What this costs, and what now has to stay true. Name the mechanism and the test
that enforces it, if there is one.
```

**Status is never edited away.** A decision that no longer holds is marked `Superseded by`, with a
link. The record of having believed something is itself the useful part — the alternatives section
of a superseded ADR is what stops the same rejected option being proposed again.

## Index

| ADR | Decision |
|---|---|
| [0001](0001-separate-ai-and-media.md) | `Arbitarr.Ai` and `Arbitarr.Media` never reference each other |
| [0002](0002-admit-no-match-when-ambiguous.md) | Ambiguous mappings admit no match rather than guessing |
| [0003](0003-siblings-are-deranked-not-discarded.md) | Franchise siblings are de-ranked, never discarded |
| [0004](0004-admin-key-write-only-with-bootstrap-bypass.md) | The admin key is write-only, with a local-network bootstrap bypass |
| [0005](0005-separate-log-database.md) | Logs live in a second SQLite database |
| [0006](0006-generate-candidates-correctly-rather-than-filtering.md) | Numbering candidates are generated correctly, not filtered afterwards |
