---
documentLanguage: en
---

# Arbitarr — shared context

The vocabulary this repository uses, and what each term means *here*. Terms are
listed because their everyday meaning is not the meaning in this codebase, or
because two neighbouring terms are routinely confused. Where a term is a type,
the type is the authority and is named.

For architecture boundaries, the secrets policy, test expectations, CI checks and
commit style, see [CONTRIBUTING.md](CONTRIBUTING.md). For the traps that have
already cost rework, see [CLAUDE.md](CLAUDE.md). This file defines words.

---

## The domain

**Broker.** Arbitarr is not an indexer and not a proxy. It sits between
Sonarr/Radarr and NZBHydra2 speaking Torznab/Newznab on *both* sides, and its
job is to answer whether a release actually is the episode that was asked for
before results pass downstream.

**Identity.** What a release *is*, independent of its display title —
`SeriesIdentity` (`src/Arbitarr.Core.Identity/SeriesIdentity.cs`): a provider ID
(TVDB/TMDB) plus every title the series is legitimately known by. Identity is
matcher input; the display title alone never is. This exists because *Ghost in
the Shell: Arise*, *Stand Alone Complex*, and *SAC_2045* share most of their
title text and overlapping `S01E01` numbering while being distinct works.

**Numbering scheme.** How the season/episode numbers of a release should be read —
`NumberingScheme` (`src/Arbitarr.Core.Identity/CandidateNumberingSet.cs`):

| Scheme | Meaning | Who numbers this way, and why |
|---|---|---|
| `ArcRelative` | Numbers relative to a story arc, not the original TVDB run | Distributors re-packaging a returning series, which often restarts at 1 within the new arc |
| `TvdbSeasonal` | Season/episode as TheTVDB publishes them for the original run | What Sonarr asks for, because its library is organised that way |
| `Absolute` | A single absolute episode number, no season component | Release groups, especially for long-running anime — stable across broadcaster re-cuts |

All three can be correct for one episode at the same time. A release name usually
does not say which it used, which is why a number never identifies an episode on
its own.

**Candidate numbering set.** The *plural* readings of one release — the same
release name decoded under each applicable scheme, carried together. Arbitarr
does not pick a scheme up front and hope; it generates the candidates and scores
them. `Bleach - 402` is genuinely both absolute episode 402 and arc-relative
episode 36 of Thousand-Year Blood War until evidence separates them.

**Franchise relation.** How a candidate identity relates to the series actually
requested — `FranchiseRelation`
(`src/Arbitarr.Media/Identity/FranchiseClassification.cs`): `Same`, `Sibling`,
or `Unrelated`. A `Sibling` is **de-ranked, never discarded**. The hard gate that
admits only exact matches was considered and rejected as fail-closed: a real
match can exist under an alternate title, and dropping it leaves no recourse.
Classification carries a `Reason`; it assigns no score itself.

**Provenance.** Why a match was made, recorded so a wrong match can be traced
back to its cause — `MatchProvenance`
(`src/Arbitarr.Core.Identity/MatchProvenance.cs`): scheme, evidence items,
confidence, which source resolved the identity, and flags.

**Identity source.** Which upstream resolved an identity — `IdentitySource`:
`None`, `ArrApi`, `Xem`, `AnimeLists`. The provider order is fixed: the API of
the *arr instance itself is authoritative, then TheXEM, then the Anime-Lists
dataset (fetched at runtime, rate-limited, never vendored).

---

## Degradation vocabulary

`MatchProvenanceFlags` are `[Flags]` — more than one can hold at once — and are
deliberately **not** collapsible into a single "degraded" value. Each names a
different failure with a different remediation:

| Flag | Means | Distinct from |
|---|---|---|
| `AmbiguousMapping` | A key matched multiple XEM rows and the names map could not separate them; **none** was admitted | A miss — the data was there, it just did not decide |
| `CacheAbsent` | No locally cached data for this lookup | `SourceUnreachable` — an empty cache does not imply the upstream is down |
| `SourceUnreachable` | The upstream could not be reached at all | `CacheAbsent` — an unreachable upstream does not imply no cache |
| `NoXemCoverage` | XEM answered, and has no entries for this series at all | `SourceUnreachable` — this is permanent and legitimate, so it is negative-cacheable |
| `NoCandidateSatisfiedIdentity` | The set is returned in full, but nothing may be promoted to a confident match | The set being empty |

**Fail loud, degrade visibly.** Any path that degrades records the fact in these
flags rather than collapsing to a bare `null` or a best-effort match. When a
mapping is genuinely ambiguous the correct behaviour is to admit *no* match and
say why.

---

## Probe outcome

`SourceProbeOutcome` (`src/Arbitarr.Core/Sources/SourceProbeOutcome.cs`) is the
result of the connectivity probe behind `POST /api/admin/sources/{id}/test`,
mirrored as a string union in `Sources/types.ts`. It is a **closed** enum with
no free-text field, so no probe failure can carry key-derived text into a
response.

| Outcome | Means | Distinct from |
|---|---|---|
| `Ok` | The source answered with the expected API shape; the key works | A reachable source — reachability alone does not prove the key |
| `Unreachable` | No usable connection: DNS, refused, no route, or past the short timeout | `TlsFailure` — nothing was negotiated at all |
| `TlsFailure` | Connected, but the TLS handshake failed | `Unreachable` — the host is there; the certificate is the problem |
| `AuthenticationFailed` | Reached and answered, but rejected the key | `UnexpectedResponse` — a clear "no", not an unreadable one |
| `UnexpectedResponse` | Answered, but not in the shape expected | `AuthenticationFailed` — the source never said the key was wrong |

The five are reported **distinctly** because each has a different fix; a single
red "failed" makes the test button decorative.

**Not to be confused with `SourceUnreachable`.** That is a `MatchProvenanceFlags`
value about identity resolution degrading on a live lookup; `Unreachable` here is
a probe outcome about one operator-triggered connectivity test. Different enums,
different questions.

---

## Keys — three of them, deliberately

Conflating these is the most common misreading of the configuration surface.

| Key | Direction | Purpose |
|---|---|---|
| **Source API key** | outbound | Arbitarr authenticating *to* NZBHydra2 |
| **Client key** | inbound | Sonarr/Radarr authenticating *to* Arbitarr, on Torznab routes |
| **Admin key** | inbound | Gating admin-**mutating** routes only, via `X-Admin-Api-Key` |

The admin key is not a login. There is no user account model; the admin UI has
no session of its own. Read-only admin pages are ungated by design.

**Bootstrap bypass.** The state a fresh install is in: no admin key set, so
admin-mutating routes are allowed from the local network and refused elsewhere,
letting the first key be set at all.

**Write-only.** Said of a key with a write path and no read path anywhere — true
of both the admin key and source API keys.

Why each works the way it does, and what would break if it were "tidied", is in
[ADR 0004](docs/adr/0004-admin-key-write-only-with-bootstrap-bypass.md).

---

## Route classification

Routes are classified as `PublicRead` or `AdminMutating`
(`src/Arbitarr.Api/Routing/RouteClassification.cs`), declared explicitly per
endpoint — there is deliberately no defaulting overload, so an endpoint cannot
ship unauthenticated-but-mutating because its author forgot to opt in.

**Classify by classification or path prefix, never by HTTP verb.**

---

## Storage

**Two SQLite databases, not one.** `arbitarr.db` holds configuration and cached
metadata; `arbitarr-logs.db` (`LogStore.DatabaseFileName`) holds the persistent
log store. They are separate so a config backup does not drag log contents
along. The log store has no EF migrations and is absent from most operational
surfaces — anything enumerating stores must grep for `DatabaseFileName`, not for
`arbitarr.db`.

**Backup archive.** The zip `GET /api/admin/backup` produces: a consistent
snapshot of `arbitarr.db` (taken through SQLite'''s backup API, not a file copy),
`release-guid-secret.key`, and a manifest naming the instant and schema version.
It excludes `arbitarr-logs.db`. It is a **credential-bearing file** — it carries
every configured source'''s API key and the secret authenticating every release
GUID this instance has issued — so it is admin-gated, never fetched through a
URL-borne token, and never written anywhere served.

**Pre-restore safety copy.** A backup archive of the *current* state, written
to the config directory immediately before a restore applies anything, so a
mistaken restore is itself recoverable. It is never swept by automatic-backup
retention — the moment it is most needed is exactly when later scheduled backups
would have pushed it off the end of a retained list.

**Snapshot versioning.** Metadata is cached against a hash of the source
snapshot it came from, so an upstream edit invalidates stale entries rather than
serving them indefinitely.

**Prune predicate.** Answers "may this row be deleted from disk?" — a
disk-space question for the scheduled maintenance job (`PrunePredicates`). It is
**not** the same question as "may this entry still be served?", which is a
read-time correctness question answered elsewhere. Conflating the two silently
changes retention.

**Query snapshot.** A stored result set backing one paginated query
(`IQuerySnapshotStore`), keyed on the query *excluding* `offset`/`limit` so
paging through it stays consistent instead of re-querying per page.

---

## Cache ages

`FreshUntil` and `ServeUntil` are two different boundaries on the same entry:
within `FreshUntil` a result is served directly with zero upstream requests;
past `ServeUntil` it is not served at all. The band between them is the
availability fallback — stale, but better than nothing when upstream is down.

---

## Caps aggregation

When Arbitarr fronts several sources, the advertised Torznab `caps` is a merge,
not a passthrough, and the merge rule differs per field:

- **Categories: unioned** — offered if *any* source supports it. Anime search is
  part of that union: `SupportsAnimeSearch` is `true` if any contributing source
  reports it, so anime stays selectable rather than narrowing to the intersection.
- **Book categories: always excluded**, unconditionally, whatever any upstream says.
- **Search params: intersected** — advertised only if *every* source supports it.
- A source whose caps fetch fails falls back to its last-known-good cached caps,
  so one dead upstream cannot shrink the merged result.

---

## Process terms

**Test-count floor.** The minimum number of tests that must pass, held in
`tests/test-count-floor.txt` and `tests/frontend-test-count-floor.txt` so the
suite cannot silently shrink. Always a *measured* number — the rule and its
reasoning are in [docs/standards/process.md](docs/standards/process.md#test-count-floors).

**Positive control.** The demonstration that a "secret must not appear in X"
assertion would actually fail if the secret leaked — without one the assertion is
vacuous. See [docs/standards/process.md](docs/standards/process.md#non-vacuous-assertions).

**Review environment.** The `Deploy review environment` CI check builds the
container image and confirms `GET /health` answers. **Nothing is deployed** — no
review environment exists, and CI never reaches the Unraid target. The name
describes an intent, not a current behaviour.

**Shadow mode.** LLM arbitration runs and records its verdict without that
verdict affecting results — the default posture for `Arbitarr.Ai`.
