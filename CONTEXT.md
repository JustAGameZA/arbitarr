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

| Scheme | Meaning |
|---|---|
| `ArcRelative` | Numbers relative to a story arc, not the original TVDB run |
| `TvdbSeasonal` | Season/episode as TheTVDB publishes them for the original run |
| `Absolute` | A single absolute episode number, no season component |

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

## Keys — three of them, deliberately

Conflating these is the most common misreading of the configuration surface.

| Key | Direction | Purpose |
|---|---|---|
| **Source API key** | outbound | Arbitarr authenticating *to* NZBHydra2 |
| **Client key** | inbound | Sonarr/Radarr authenticating *to* Arbitarr, on Torznab routes |
| **Admin key** | inbound | Gating admin-**mutating** routes only, via `X-Admin-Api-Key` |

The admin key is not a login. There is no user account model; the admin UI has
no session of its own. Read-only admin pages are ungated by design.

**Bootstrap bypass.** With no admin key set, admin-mutating routes are allowed
from loopback/RFC1918 addresses and refused with `503` from anywhere else — so a
fresh install can set its first key, and only from the local network. It logs at
Warning for as long as it stays open, because running unconfigured leaves the
admin surface open to the whole LAN. The bypass and
`PUT /api/admin/security/admin-key` are two halves of one fix; neither resolves
the bootstrap deadlock alone (`AdminApiKeyFilter`, `AdminSecurityEndpoints`).

**Write-only.** The admin key has one write path and no read path anywhere. It is
absent from `SettingsCatalog`, which feeds both the settings PUT allow-list and
its GET projection — so `PUT /api/admin/settings/AdminApiKey` is a 404 and
`GET /api/admin/settings` never carries the value. Source API keys are likewise
write-only rows under the colon-namespaced name `source:{id}:api_key`, which no
`SettingKey` enum value can produce. These are mechanisms, not coincidences.

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

**Snapshot versioning.** Metadata is cached against a hash of the source
snapshot it came from, so an upstream edit invalidates stale entries rather than
serving them indefinitely.

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

**Test-count floor.** A number a run actually printed, held in
`tests/test-count-floor.txt` and `tests/frontend-test-count-floor.txt`, so the
suite cannot silently shrink. It is a **measurement, never arithmetic** —
re-measure after a rebase; never adjust it by adding the number of tests written.

**Positive control.** The demonstration that a "secret must not appear in X"
assertion would actually fail if the secret leaked. `Assert.DoesNotContain`
passes just as happily when the secret was never in play, so without one the
assertion is vacuous.

**Review environment.** The `Deploy review environment` CI check builds the
container image and confirms `GET /health` answers. **Nothing is deployed** — no
review environment exists, and CI never reaches the Unraid target. The name
describes an intent, not a current behaviour.

**Shadow mode.** LLM arbitration runs and records its verdict without that
verdict affecting results — the default posture for `Arbitarr.Ai`.
