# 0021. Cardigann definitions are out of v1

- **Status:** Accepted
- **Date:** 2026-09-12

## Context

Once Arbitarr talks to indexers directly, a question follows immediately: which indexers can it
talk to? Generic Newznab and Torznab cover every Usenet indexer and every tracker that speaks
Torznab natively. They do not cover the several hundred trackers that speak neither, and which
Prowlarr and Jackett reach through **Cardigann** — a declarative YAML DSL describing, per tracker,
how to log in, which pages to request, and which HTML selectors hold the title, size, seeders and
download link.

Supporting Cardigann is not one feature. It is a YAML definition model, a selector engine, a
login and session mechanism, a channel for shipping definition updates, and a per-tracker test
surface — each of those larger than any single piece of the direct-indexer work. And the result
rots: a definition is pinned to a tracker's current markup, so it breaks when the tracker changes
its page, which trackers do without notice or coordination. Prowlarr can carry that maintenance
because indexer definitions *are* its product. For Arbitarr, whose product is identity-aware
matching, it is a large surface that permanently decays and sits next to the actual goal rather
than on it.

The decisive fact is what declining costs: nothing. Prowlarr and Jackett both expose their
scraped trackers as ordinary Torznab endpoints, and the epic's scope boundary keeps Arbitarr able
to consume any Torznab source. An operator who needs a scraped tracker points Arbitarr at their
existing Prowlarr or Jackett instance for exactly that tracker, and uses direct indexers for
everything else. The capability is not being declined — only reimplementing it is.

## Decision

**Arbitarr v1 speaks generic Newznab and Torznab only. Cardigann YAML definitions are not
supported, and Prowlarr and Jackett remain fully usable as ordinary Torznab sources.**

The owner decided this on 2026-09-12 (arb-x7w8.19). If it is ever revisited, Cardigann is scoped
as its own epic rather than a child of the direct-indexer work, because each of its parts is
larger than anything in that epic.

## Alternatives rejected

### Implement Cardigann definition support in v1

Rejected: it is the largest single piece of work in the epic and the only one whose value decays
without anyone touching Arbitarr — a tracker's markup change breaks a definition that was correct
when it shipped. It also buys nothing an operator cannot already have: the same trackers are
reachable today by pointing Arbitarr at Prowlarr or Jackett over Torznab. Paying a permanent
maintenance cost for a capability already available by configuration is the trade this rejects.

### Support a small hand-picked subset of Cardigann definitions

Rejected: the subset does not reduce the machinery. Reading one definition needs the same YAML
model, the same selector engine and the same login mechanism as reading all of them, so the
implementation cost is paid in full for a fraction of the coverage — while the maintenance cost
still arrives every time one of the chosen trackers changes its markup. It also creates a
supported-tracker list to argue about and extend, which is how a subset stops being one.

## Consequences

- **Prowlarr and Jackett must remain usable as ordinary Torznab sources.** That is not incidental
  compatibility; it is the entire reason declining Cardigann costs the operator nothing. Any change
  that makes a generic Torznab source harder to configure, or that assumes a Torznab endpoint is a
  single tracker rather than an aggregator, removes the fallback this decision depends on.
- **The replacement is additive, so adoption is partial by design.** An operator keeps Prowlarr for
  the one scraped tracker they need and uses Arbitarr's direct indexers for the rest. Nothing
  requires an all-or-nothing migration away from an existing aggregator.
- **Arbitarr does not ship a tracker list and has no definitions to update.** There is no
  definition update channel, no per-tracker test matrix, and no breakage to chase when a tracker
  changes its markup.
- **Reversing this is an epic, not a feature flag.** The parts named in the Context section are
  each substantial, so a future decision to support Cardigann supersedes this ADR and gets its own
  epic rather than being folded into indexer work.
