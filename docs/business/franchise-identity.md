# Why similar titles are not the same series

This matters because the failure it describes is silent and self-confirming. When two distinct
works share most of their title text *and* their episode numbering, a fuzzy match produces a
confident wrong answer, the download succeeds, the file lands in the library, and nothing in the
chain reports a problem. The operator discovers it by watching the wrong thing.

## The canonical case

*Ghost in the Shell* exists as, among others:

- the 1995 film,
- *Stand Alone Complex* (two seasons),
- *Arise* (a four-part OVA series),
- *SAC_2045*.

These are separate works with separate TVDB identities. They are not seasons of one another and
must never be merged.

They also share almost everything a naive matcher looks at. The title tokens "Ghost in the Shell"
dominate every string. Several use `S01E01`-style numbering, so the episode numbers *agree* between
distinct series. String similarity and number equality both point the wrong way at once.

## Why a strict filter is also wrong

The obvious fix is to demand an exact match on the requested series and reject everything else.
This was proposed and rejected — see [ADR 0003](../adr/0003-siblings-are-deranked-not-discarded.md).

The reason is that release names do not use canonical titles. A legitimate release of the series
you asked for may carry a localised title, a release-group rendering, or an arc-specific alternate
name. A strict filter drops it, reports nothing, and gives the operator no way to see what was
discarded or why. Trading a wrong grab for a silent no-grab is not an improvement; it moves the
failure rather than removing it.

## What follows

Candidates are **classified**, not filtered: `Same`, `Sibling`, or `Unrelated`, each `Sibling`
carrying a written reason. Ranking de-ranks siblings so they lose to a genuine match, but they
remain available if nothing better exists — which is the right outcome when the requested series
genuinely has no release.

Identity is therefore never the display title. It is a provider ID plus the full set of titles a
series is legitimately known by, so a release can be positively identified without exact title
equality.
