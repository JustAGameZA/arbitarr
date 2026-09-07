# Standards

Checkable rules that apply across changes, in three volumes:

| Volume | Covers |
|---|---|
| [architecture.md](architecture.md) | Project boundaries, route surfaces, secrets mechanisms |
| [data.md](data.md) | Persistence, caching, the two databases, provenance |
| [process.md](process.md) | Tests, verification, git, PR flow |

## What belongs here

A rule belongs in a standards volume when it is:

- **Checkable** — a reader can look at a diff and say whether it holds.
- **General** — it applies to future changes, not just to one that already happened.
- **Reasoned** — it carries its *why*. A rule without one gets tidied away by the next person who
  cannot see the harm in breaking it, and that is a fair response to an unexplained rule.

A one-off decision is an [ADR](../adr/). A word's meaning is [CONTEXT.md](../../CONTEXT.md). A rule
so hot it is violated repeatedly may be promoted into `CLAUDE.md`'s body — that file holds at most
five such entries, and promoting one demotes the coldest back to here.

## Relationship to CONTRIBUTING.md

[CONTRIBUTING.md](../../CONTRIBUTING.md) is the entry point and states the rules everyone must
follow. These volumes are where a rule's full reasoning lives when it is longer than CONTRIBUTING
should carry. Where they overlap, they must agree; CONTRIBUTING is the summary, not a second
source of truth.
