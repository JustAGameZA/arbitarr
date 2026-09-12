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

**Protocol.** Which of those two families a *request* belongs to — the
`/torznab/api` route or the `/newznab/api` route — carried on `SearchQuery` as
`SearchProtocol` (`src/Arbitarr.Core/Sources/SearchProtocol.cs`). It selects the
upstream endpoint, because NZBHydra2 reads its `/torznab/api` as a torrent search
and excludes usenet indexers from it. Distinct from `ProtocolKind`, which says
what a returned *release* is; that type's doc comment carries the distinction.

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

**Protocol answer vs infrastructure error.** A **protocol answer** means the request was
understood: delivered with HTTP 200 as Torznab/Newznab code 100 (bad or missing API key)
or code 500 (rate limit). An empty result set is not an error at all — just a results
element with no items. An **infrastructure error** means the pipeline failed to produce an answer at
all: code 900, HTTP 5xx. See `SearchEndpoint.InfrastructureErrorResult`'s remarks
(`src/Arbitarr.Api`) for the full reasoning behind keeping these two outcomes distinct.

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

**Upstream redirect refusal.** A download answered with any 3xx — the whole
range, 304 included — is refused rather than followed
(`UpstreamRedirectRefusedException`), because a redirect on that path points at
the indexer, which is off-origin by definition and would carry the upstream key
into a second request. It is recorded as a **deliberately unnamed**
`SourceFailed` event: naming the source would feed
`NotificationPolicy.FoldSourceFailure`'s consecutive-failure counter and announce
a healthy source as down after a few *arr retries, when the real fault is an
upstream set to redirect mode rather than proxy mode. See
[ADR 0014](docs/adr/0014-refuse-upstream-download-redirects.md).

**Health item.** An outstanding operator-actionable condition, reported per source
in `/api/status`'s `health` list and rendered as a banner on the Dashboard. It is
**not** a source's circuit-breaker state: a health item names a condition the
*operator* must fix, and the source it names is typically healthy — a refused
redirect is exactly that case, which is why the Sources block shows nothing wrong.
Severity `blocking` means the affected function cannot work at all until someone
acts.

Health items are **cleared only by the specific event that proves the condition is
over** — for a refused download, an actual successful grab from that same source,
never elapsed time, a worker cycle, or a successful search.

Since arb-v3w they are also **persisted** — one row per source in `arbitarr.db`,
upserted on each refusal, deleted on a successful grab, and rehydrated into the
in-memory tracker at startup — so they **survive a restart**. `observedSinceUtc`
therefore means "when the condition began". That is the point of persisting it:
the misconfiguration behind a refused redirect outlives the process, so a restart
that dropped the item, or reset the instant to "now", reported a clean system
while every download still failed. The row count is bounded by the number of
configured sources, so the table needs no pruning. See
[ADR 0016](docs/adr/0016-persist-download-refusal-health.md). Health items also
notify now (arb-apj, shipped in #247): one notification when a source's item
appears, one when it clears, pushed from a decorator rather than through
`NotificationPolicy.FoldSourceFailure` — see
[ADR 0014](docs/adr/0014-refuse-upstream-download-redirects.md).

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

**Nor with `OllamaProbeOutcome`** (`src/Arbitarr.Core/Ai/OllamaProbeOutcome.cs`),
the result of `POST /api/admin/ai/ollama/test`. Same closed-enum discipline and
three of the same member names, but a **different set**: `Ok`, `Unreachable`,
`TlsFailure`, `UnexpectedResponse`, `ChatRejected` and `OkNoModelConfigured`.
There is deliberately no `AuthenticationFailed` — an AI backend carries no key,
so that outcome could never be produced, and offering it would send an operator
hunting for a key that does not exist. Its `UnexpectedResponse` means "something
answered but it was not Ollama's `/api/tags`", where the source enum's means
"not the Torznab caps document".

The last two arrived with arb-1rr, when a probe that only asked `/api/tags`
turned out to report a green "Connected successfully" while every classification
failed. **`ChatRejected`** is the address being right and the model list
answering, but `/api/chat` refusing the classification request itself — the
connectivity half healthy and the working half not. **`OkNoModelConfigured`** is
`/api/tags` answering with no model configured, so the `/api/chat` half was never
attempted; it is its own outcome rather than folded into `Ok` so the button
cannot claim more than it tested.

---

## AI backend

An **AI backend** is the LLM instance the classifier speaks to — today Ollama, at
the address held in `SettingKey.OllamaBaseUrl`, running the model held in
`SettingKey.OllamaModel`. Both are edited in the Settings surface's own AI
section (`Settings/Ai/Ai.tsx`, #89 for the address, #112 for the model) and
resolved per call, so changing either takes effect without a restart.

The **model** is chosen from what the instance itself reports, not typed: the
connectivity probe returns the names from `/api/tags` alongside its outcome, and
the section renders them as a picker. Typing one blind is the state #112 removed —
an operator could name a model their instance had never pulled, get a green
"Connected" from a probe that only asks whether the address is Ollama, and have
every classification fail open with nothing on screen saying why. The names travel
in their own field and never inside the probe's wording, which is what keeps
`OllamaProbeOutcome` a closed enum carrying no free text.

The **excerpt** is the one piece of upstream text admitted to that surface, and
it travels beside the enum rather than inside it. On a `ChatRejected` outcome it
is the reason Ollama gave for refusing the request, carried in
`OllamaProbeResult.ChatError` — because "Ollama rejected the request" without the
reason is a non-answer. It is bounded at `OllamaRequestException.MaxExcerptLength`
(200 characters, whitespace collapsed) and scrubbed by `SanitizedErrorDescription`
before it can reach `/api/status`, so it carries no host, address or credential.
The probe is its only writer and never assigns a raw body.

Two terms name components of the **verdict cache key**, and they are deliberately
separate because they change for different reasons.

The **prompt version** is the version tag of the classification prompt
**template**, and only the template — `Arbitarr:Ai:PromptVersion`, defaulting to
`v2`. It answers "what were we asking?". It is not a general-purpose cache-buster:
arb-p4r briefly used a bump of it to invalidate verdicts after a *decoding* change,
which both stretched the term and could be defeated by an operator who pinned the
setting explicitly.

The **decoding identity** is the token naming the sampling constants a verdict was
decoded under — `t0-s42` for temperature 0, seed 42 (`OllamaOptions.DecodingIdentity`,
arb-qg3o). It answers "how were we asking?", and it is a cache-key component in its
own right, so changing either constant invalidates previously cached verdicts by
construction. It reads no configuration, which is exactly the point: there is no
setting an operator can pin to keep serving verdicts decoded under the old sampling.

**It is not a source, and the distinction is load-bearing rather than
terminological.** A source is *searched* — it is an indexer, it appears in the
sources health table, it carries a Torznab/Newznab API key, and its probe speaks
the source API. An AI backend is none of those: it is asked to classify a release
that a source already returned, it holds no key, and probing it means asking
Ollama for its model list. Collapsing the two would make `source` mean two things
in one table and would report a healthy Ollama as `UnexpectedResponse` against
the source probe. Hence a separate section, a separate probe, and a separate
outcome enum.

Both are nonetheless configured under the **same ruling**: seed once from the
environment, then the database is authoritative — the environment is inert
afterwards, and a divergent value produces a startup warning rather than silently
losing. The reasoning, and the incident that forced it, is in
`SourceSeeder`'s type doc (`src/Arbitarr.Host/Sources/SourceSeeder.cs`);
`OllamaBaseUrlSeeder` and `OllamaModelSeeder` each apply the identical rule to
their own row.

---

## Keys — three of them, deliberately

Conflating these is the most common misreading of the configuration surface.

| Key | Direction | Purpose |
|---|---|---|
| **Source API key** | outbound | Arbitarr authenticating *to* NZBHydra2 |
| **Client key** | inbound | Sonarr/Radarr authenticating *to* Arbitarr, on the indexer routes (both protocols) |
| **Admin key** | inbound | Gating admin-**mutating** routes only, via `X-Admin-Api-Key` |

**A client key has two sources, one meaning.** Since #97 the indexer routes
accept both an environment-configured key (`Arbitarr:ApiKey` /
`Arbitarr:ClientApiKeys`) and a key minted in Settings > API keys, resolved by
the same `IClientApiKeyResolver` to the same `ClientKeyContext`, so attribution
does not care which was presented. Either **scope** satisfies them — these are
`PublicRead` routes and an admin key is not required. See
`Arbitarr.Host.Security.DbClientApiKeyResolver` for the ordering and the upgrade
guarantee.

A fourth inbound credential joined these in #44: an **operator session**. It is
not a key and has no row in the table above, but the admin gate accepts it
exactly where it accepts the admin key — so "three keys" remains true, and
"three credentials" does not.

**The admin key is still not a login, and a login is not a key.** Since #44 the
admin UI *does* have a session of its own, so the older claim that it has none is
gone. The two credentials answer different callers and neither replaces the
other: a human signs in, and Sonarr, Radarr and every scripted caller present a
key because they cannot complete an interactive login. Read-only admin pages are
ungated by design regardless of which is presented.

**One resolution type, not two.** Both credentials resolve to a
`CredentialResolution` carrying an `ApiKeyScope`, and the gate makes one scope
check over whichever answered. This type was named `AdminKeyResolution` until
arb-rh7: after #44 it started carrying session outcomes and a session label too,
not just key outcomes, so the *key*-specific name was no longer honest and was
renamed along with its paired `AdminKeyResolutionOutcome` enum (now
`CredentialResolutionOutcome`) and `IAdminKeyResolver` (now
`ICredentialResolver`), plus the `DbAdminKeyResolver` implementation (now
`DbCredentialResolver`).

**Bootstrap bypass.** The state a fresh install is in: no admin key set, so
admin-mutating routes are allowed from the local network and refused elsewhere,
letting the first key be set at all.

**Write-only.** Said of a key with a write path and no read path anywhere — true
of both the admin key and source API keys.

**Account / operator.** A human sign-in identity: a username and a password hash.
"Operator" is the person; "account" is the row. Arbitarr is single-operator by
design — there is one role, so an account carries no per-user scope column, and a
session authorises at `Admin`.

**Session.** A server-side record that a particular operator signed in, with an
idle expiry and an absolute expiry. Server-side is the load-bearing part: sign-out
revokes the row, so a caller holding a copy of the token still cannot use it. A
self-contained signed token would make sign-out a claim rather than a fact.

**Session cookie.** The transport for a session token — `HttpOnly` (script can
never read it), `SameSite=Lax`, and `Secure` only when the request arrived over
TLS. Distinct from the *session*: revoking the row ends access whether or not the
cookie is still in a browser.

**First-run setup.** The one-time path that creates the first account. Requires
zero accounts **and** a trusted-network caller — both, not either — and once an
account exists it is closed permanently.

**Trusted network.** The socket peer is loopback, RFC 1918, or RFC 4193
unique-local. Since #44 this is its own type (`TrustedNetwork`) rather than a
predicate inside the admin filter, because first-run setup needs the identical
rule and two copies would drift into two trust boundaries. Being on a trusted
network is **not** being authenticated: it gates bootstrap paths only — setting a
first key, claiming a fresh install — each of which stops applying the moment the
thing it bootstraps exists.

Why each works the way it does, and what would break if it were "tidied", is in
[ADR 0004](docs/adr/0004-admin-key-write-only-with-bootstrap-bypass.md).

**Sources' key has no clear-in-place route.** Dropping a source's key while keeping the source
configured is a real but currently hypothetical need; deferred until reported rather than solved
speculatively. See [ADR 0010](docs/adr/0010-secrets-clear-route.md).

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

**Two log sentinels, and they do not mean the same thing.** Both are fixed text
`LogMessageCleanser` can put into a stored log row in place of the original, and
an operator reading `/api/admin/logs` will meet both, so the distinction is what
tells them whether they are looking at a truncated line or a failed one:

- **Redaction timeout placeholder** — `<redaction timed out>`
  (`LogMessageCleanser.TimeoutPlaceholder`). Replaces **the whole text** of one
  field when scrubbing it exceeded the regex match timeout. Fail-closed: nothing
  of the original survives, because what could not be scrubbed cannot be shown.
  The failure unit is one row's message or exception independently, never the
  batch — a single pathological line must not cost the rows written with it.
- **Truncation marker** — `…<truncated>`
  (`LogMessageCleanser.TruncationMarker`). Appended after a text is cut at
  `MaxCleanseInputLength`, replacing only the **dropped tail**. The scrubbed head
  is still there and still readable; the tail is discarded outright, not merely
  left unscrubbed.

A whole-message sentinel therefore means *scrubbing failed*, while a trailing one
means *the line was too long*. Reading the first as the second understates a
redaction failure.

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

**Staging directory.** `backup-staging/`, a sibling of `backups/` under the config
directory, holding only transient restore/backup working files that a startup
sweep reclaims if a hard kill leaves them behind. See
[docs/standards/data.md](docs/standards/data.md#backup-restore-and-the-staging-directory)
for the full rule.

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

**Release lookup.** What resolves the **proxy GUID** in a download link back to
the source and upstream link it stands for (`IReleaseLookup`), so
`DownloadProxyEndpoint` can fetch the file without the *arr instance ever
holding the upstream URL or the key in it.

**Two-tier lookup.** The shape that lookup has since arb-tps:
`PersistentReleaseLookup` reads `InMemoryReleaseLookup` first and, on a miss, a
`ReleaseLookupEntry` row in `arbitarr.db`, repopulating memory from a store hit.
The memory tier is bounded (`MaxEntries`, a 30-minute `EntryTtl`) and those
bounds used to decide how long a link worked — a restart or a delayed grab
answered 404 on a link this application had issued. The row's lifetime is
`release_lookup_ttl` (default 14 days), its `ExpiresAt` is evaluated on every
read, and the maintenance prune follows that same boundary. See
[ADR 0015](docs/adr/0015-persist-release-lookup.md).

**Not to be confused with the query snapshot above.** Both are persisted
search-side caches with an `ExpiresAt`, but they answer different questions: a
query snapshot keeps one *result set* stable while a caller pages through it; a
release lookup keeps one *rendered release* resolvable long after the search
that produced it has been forgotten.

**Download-refusal entries.** `DownloadRefusalEntries`, one row per source, the
durable tier behind the health-item tracker described above. `SourceName` is
uniquely indexed, so a source can hold at most one row; a row is upserted on
each refusal and deleted on a successful grab from that source, never on
elapsed time or a scheduled prune — the unique index and the delete-on-clear
rule together bound the table without one. It is rehydrated into the in-memory
tracker at startup, before the host serves, by an awaited hosted service. See
[ADR 0016](docs/adr/0016-persist-download-refusal-health.md).

**Search detail format.** The machine-readable spelling of *which* search an
event row is about, written into an event's `Detail` by
`SearchQueryDescriptor.DescribeDetail` — semicolon-separated `key=value` pairs
in a fixed order, with absent parts omitted. **That method is the authority on
the grammar**, including which keys exist and why the free-text one is last; it
is not restated here, because a second copy of a wire format is a second thing
to keep true. Two consumers already parse it — the Activity surface and the
frontend's `searchDetail.ts` — which is what makes it a format rather than an
implementation detail, and why `IEventSink`'s "free-form kind-specific detail"
does not describe this kind. `tests/fixtures/search-detail.json` (arb-6jks) is
the producer/consumer parity contract for this grammar: both
`SearchQueryDescriptorTests` and `searchDetail.test.ts` read the same file, so
the two sides cannot drift with both suites green.

Being byte-identical for the same search is a *property the format is for*, not
an incidental one: `Detail` is one of the six fields `EventRepository` compares
to decide whether a new event folds onto the previous row (incrementing its
`RepeatCount`) instead of writing another. Any per-occurrence value rendered
into it — a duration, a timestamp, a result count — therefore makes every
occurrence a distinct event and defeats that folding. The same applies to an
event's `Reason`, which is in the same identity.

---

## Obfuscated title

A scrambled/hash-like Usenet release title, produced by some posting groups as
a routine anti-abuse convention, not a junk signal. Arbitarr passes it to the
classifier and to Sonarr **verbatim, by design**:
`ClassificationPrompt.UsenetGuidance` (`src/Arbitarr.Ai/ClassificationPrompt.cs`
~:31-37, Usenet-arm-only, torrent arm omits it, pinned by `ObfuscatedNameTests`)
tells the model this is not junk; Sonarr matches a grab on history, not
filename; and SABnzbd already deobfuscates post-download via
par2/`Deobfuscate.py`. The operator lever for one indexer's titles is an
**NZBHydra2 custom title mapping**, upstream of Arbitarr.

Normalization (`TitleNormalizer`/`DenyList`,
`src/Arbitarr.Ai/Normalization/`, default OFF) only **removes** tokens from a
4-entry deny list; it can never synthesise text, so it can never deobfuscate.

**Rejected, not to be re-proposed:** hash-like detection/de-ranking (collides
with `FilterStage`'s M1-4 never-rewrite invariant,
`src/Arbitarr.Api/Search/FilterStage.cs` ~:24-27, and
[ADR 0002](docs/adr/0002-admit-no-match-when-ambiguous.md)); an alternate
title from Newznab attributes (no such field exists); fetching the NZB to
read segment subjects (a second caller of
`ReadApiKeyForUpstreamRequestAsync`, forbidden by CLAUDE.md §1, and would
break Sonarr's grab-history match).

---

## Cache ages

`FreshUntil` and `ServeUntil` are two different boundaries on the same entry:
within `FreshUntil` a result is served directly with zero upstream requests;
past `ServeUntil` it is not served at all. The band between them is the
availability fallback — stale, but better than nothing when upstream is down.

---

## Caps aggregation

When Arbitarr fronts several sources, the advertised `caps` is a merge, not a
passthrough, and the merge rule differs per field. The merge runs per protocol —
each family's caps come from the upstream endpoint that family's searches use:

- **Categories: unioned** — offered if *any* source supports it. Anime search is
  part of that union: `SupportsAnimeSearch` is `true` if any contributing source
  reports it, so anime stays selectable rather than narrowing to the intersection.
- **Book categories: always excluded**, unconditionally, whatever any upstream says.
- **Search params: intersected** — advertised only if *every* source supports it.
- A source whose caps fetch fails falls back to its last-known-good cached caps,
  so one dead upstream cannot shrink the merged result.

---

## Process terms

**Test-count floor.** The minimum number of tests that must pass, so the suite
cannot silently shrink. Not a committed file: CI ratchets each run against
master's last measured counts, carried between runs as the `test-counts`
artifact. The rule and its reasoning are in
[docs/standards/process.md](docs/standards/process.md#test-count-floors).

**Positive control.** The demonstration that a "secret must not appear in X"
assertion would actually fail if the secret leaked — without one the assertion is
vacuous. See [docs/standards/process.md](docs/standards/process.md#non-vacuous-assertions).

**Review environment.** The `Deploy review environment` CI check builds the
container image and confirms `GET /health` answers. **Nothing is deployed** — no
review environment exists, and CI never reaches the Unraid target. The name
describes an intent, not a current behaviour.

**Shadow mode.** LLM arbitration runs and records its verdict without that
verdict affecting results — the default posture for `Arbitarr.Ai`.
