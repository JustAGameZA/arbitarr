# Why one episode has several legitimate numbers

This matters because it is the reason Arbitarr cannot simply parse a number out of a release name
and look it up. A single episode of a long-running series genuinely has several correct numbers at
the same time, assigned by different parties for different purposes, and a release name usually
does not say which one it is using. Any design that assumes a number identifies an episode is
already wrong; it just has not met the failing case yet.

## The three schemes

**Absolute numbering** counts every episode from the start of the series, ignoring seasons.
Long-running anime is commonly released this way — `Bleach - 402` is episode 402 of the whole run.
Fansub and release groups favour it because it is stable: it does not change when a broadcaster
re-cuts seasons.

**TVDB seasonal numbering** is what TheTVDB publishes for the original broadcast run — season and
episode within that season. This is what Sonarr asks for, because it is what its library is
organised by.

**Arc-relative numbering** counts within a story arc rather than the original run. When a series
returns after a long gap, or a distributor re-packages it, the new material is often numbered from
1 again within its arc. *Bleach: Thousand-Year Blood War* is the canonical case: its episodes have
arc-relative numbers, absolute numbers continuing the original run, and TVDB numbers that may match
neither.

## Why this collides

`Bleach - 402` is genuinely ambiguous. It can be absolute episode 402, or it can be arc-relative
episode 36 of Thousand-Year Blood War — and on TheXEM those can map to **different episodes**. Both
readings are defensible from the release name alone. Nothing in the string resolves it.

This is not an edge case in the sense of being rare. It is structural: it happens to every series
long enough to have arcs, which is most of the content where a broker is worth having at all.

## What follows

Arbitarr generates the plural readings — a candidate numbering set, one candidate per applicable
scheme — and scores them against identity evidence, rather than committing to a scheme up front.

When the evidence does not separate them, no match is admitted. See
[ADR 0002](../adr/0002-admit-no-match-when-ambiguous.md) and
[why-silence-is-worse-than-nothing.md](why-silence-is-worse-than-nothing.md).

## Where the mappings come from

TheXEM (thexem.info) is a community-maintained mapping between scene, absolute, and TVDB numbering.
It is the source that makes arc-relative resolution possible at all.

Two of its states must never be confused. **No coverage** means XEM answered and simply has no
entries for this series — a legitimate, permanent condition, and one worth caching, because
re-asking hourly will not change the answer. **Unreachable** means XEM did not answer, which is
transient and must not be cached, because caching an outage extends it past its end.
