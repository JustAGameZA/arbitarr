# 0006. Generate the candidate set correctly rather than filtering a bad one

- **Status:** Accepted
- **Date:** 2026-08-27

## Context

A release name does not say which numbering scheme it used, and all three schemes can be correct
for one episode at once (see [CONTEXT.md](../../CONTEXT.md)). So a release has to be read under
every applicable scheme and the readings scored against identity evidence, rather than having one
scheme detected up front.

That leaves a question about *how* the candidate set is produced. Scene season numbers commonly
start at 1 for arc-numbered anime, which makes a bare `(season=1, episode=arc_relative_ep)`
candidate very easy to emit — and that candidate is exactly the Bleach `S01E36` regression, where
arc-relative episode 36 is misread as season 1 episode 36 of the original run.

## Decision

Candidates are **generated correctly the first time**. A scene season/episode pair only becomes an
`ArcRelative` candidate qualified by the `ArcSeasonBinding` it actually binds to — via arc-title
token or absolute-range membership. It is never additionally emitted as a bare `TvdbSeasonal`-shaped
season-1 guess.

The generator is the enforcement point: `CandidateNumberingSetBuilder.Build` never returns a bare
`(1, arc_relative_ep)` candidate, and says so in its own contract.

When no arc map is available (the `NoXemCoverage` case), only an `Absolute` candidate is generated.
Arc-relative and TVDB-seasonal candidates need arc/season context that is genuinely absent, so none
is invented.

## Alternatives rejected

- **Generate everything, then filter the bad candidates downstream.** This was the earlier design.
  Rejected: the bad candidate exists transiently, so any caller consuming the raw set before the
  filter runs gets it. A filter is a second thing that has to keep working and keep being called;
  not generating the candidate removes the failure mode instead of policing it.
- **Detect the scheme up front, then parse under it.** Rejected: detection is the hard problem. A
  detector confident enough to pick one scheme is confident enough to be wrong silently, and the
  ambiguity it papers over is exactly what
  [0002](0002-admit-no-match-when-ambiguous.md) requires be surfaced.
- **Emit the season-1 candidate and let scoring de-rank it.** Rejected: scoring can only compare
  candidates it is given. A wrong candidate that scores well is indistinguishable from a right one,
  and this specific candidate scores well precisely because the numbers agree.

## Consequences

- The generator needs arc/season context to do its job, which is why the arc map is an input rather
  than something applied later.
- A caller can consume the raw candidate set safely, without knowing a filter exists.
- Where context is missing the set is smaller rather than speculative — fewer candidates, none of
  them invented.
- The Bleach case is a fixture-backed regression test, not a comment. If this generation rule is
  weakened, that test fails.
