# Product intent

What Arbitarr is for, who it serves, and what it should do next. The single file here,
[roadmap.md](roadmap.md), is the statement of product intent that the PO gate reads before it rules
on a plan, an acceptance, or a backlog order.

## What belongs here

- The roadmap: candidate work ranked Now / Next / Later, each item citing the surface it changes
  and the user problem it solves, plus the non-goals and the questions only the owner can answer.
- Personas or user-journey notes, if they ever settle enough to be worth a file.

## From an item to work

A roadmap item is not a brief, and its `Shape` is the analyst's read of the likely approach, not an
approved design. An item becomes work by being cut into a bead at backlog ordering; where work is
already tracked the item cites the open bead instead. The bead is the unit of work and the item is
the reason for it. The scope boundary, the load-bearing comments and the verification commands
that [CLAUDE.md](../../CLAUDE.md) §7 requires are written when the bead is briefed, never here.

## What does not belong here

- Decisions that are hard to reverse — those are [ADRs](../adr/), and need a rejected alternative.
- Checkable rules that apply to every change — those are [standards](../standards/).
- Vocabulary — that is [CONTEXT.md](../../CONTEXT.md).
- Anything secret, internal, or unannounced. This repository is public; the roadmap describes a
  hardening gap as an item without a reproduction, or not at all.

## Status line

The roadmap opens with a status line. `LLM-derived, UNRATIFIED` means the `product-analyst` agent
produced it and the owner has not reviewed it; nothing in it is a commitment. The owner changes the
line to `ratified <date>` once reviewed. Until then the PO gate treats every item as candidate
intent, not settled intent.

Each item's `Confidence` is the analyst's confidence on the generation date, and its `Evidence` is
a snapshot of the tree on that date. Re-verify an item's evidence against the current tree before
cutting a bead from it.
