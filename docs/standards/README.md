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

## Who owns which rule

Three files can state the same rule. The order of authority is:

1. **[CLAUDE.md](../../CLAUDE.md)** — wins on anything it covers. It holds the small set of traps
   that have already cost rework, kept deliberately short (its body carries at most five hot
   entries) and aimed at agents working this codebase.
2. **These volumes** — the full statement of a rule and its reasoning.
3. **[CONTRIBUTING.md](../../CONTRIBUTING.md)** — the entry point and the human-facing summary.

Where any two overlap they must agree, and the more specific file wins. A rule should be stated in
full **once**: CONTRIBUTING summarises in a sentence and links here; CLAUDE.md carries only what an
agent must not get wrong. If you find yourself copying a paragraph between them, link instead.
