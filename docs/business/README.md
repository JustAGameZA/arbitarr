# Business context

Domain background and rules — the "why does this matter" that the code cannot state for itself.

One article per business question. Each opens with a paragraph saying why it matters, because an
article that starts with mechanism reads as documentation of the code rather than of the domain,
and the domain is the part that outlives the implementation.

## What belongs here

- Background a new contributor needs to understand *why* a rule exists, where the reason lives
  outside the codebase — in how the *arr ecosystem behaves, how TheXEM models data, or what an
  operator actually experiences.
- Rules that come from the domain rather than from engineering judgement.

## What does not belong here

- Vocabulary — [CONTEXT.md](../../CONTEXT.md).
- Engineering decisions — [docs/adr/](../adr/).
- Checkable rules — [docs/standards/](../standards/).

## Articles

| Article | Question it answers |
|---|---|
| [numbering-schemes.md](numbering-schemes.md) | Why one episode has several legitimate numbers |
| [franchise-identity.md](franchise-identity.md) | Why similar titles are not the same series |
| [why-silence-is-worse-than-nothing.md](why-silence-is-worse-than-nothing.md) | Why a wrong match costs more than a missing one |
