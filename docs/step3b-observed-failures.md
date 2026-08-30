# Step 3b — Observed Numbering/Classification Failures (Evidence Report)

## SYNTHETIC / NO LIVE OLLAMA — READ BEFORE USING THIS DOCUMENT

This is a plain-observation report, not a design document, and it does not test any real code
path. Specifically:

- **No live Ollama instance was used.** `IOllamaClient` (`src/Arbitarr.Ai/IOllamaClient.cs`) is
  treated only as a hypothetical fixed-verdict stand-in for this exercise: every release below is
  reasoned about as if a synthetic fake `IOllamaClient` returned a constant
  `OllamaVerdict(Verdict: "accept", Confidence: 1.0)` uniformly, for every candidate, with no model
  actually invoked. This assumption is stated once here and applied throughout; it is not
  re-derived per item.
- **No release-title parser exists in this codebase yet.** The only numbering-candidate-generation
  code that exists today is `CandidateNumberingSetBuilder.Build` in
  `src/Arbitarr.Media/Numbering/CandidateNumberingSetBuilder.cs`, which consumes an already-parsed
  `RawReleaseNumbering` (`SceneSeason`, `SceneEpisode`, `Absolute`, `ArcTitleToken`). Nothing in the
  repository turns a raw `<title>` string (e.g. `Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0
  H 264-playWEB`) into a `RawReleaseNumbering` yet. Every "candidate numbering produced" entry
  below is therefore **manual/observational reasoning against fixture title text** — a human
  simulating what a plausible future parser would extract, then hand-tracing
  `CandidateNumberingSetBuilder.Build`'s documented behavior against that input and the arc map
  from `tests/Arbitarr.Media.Tests/BleachArcRelativeNumberingTests.cs`. It is **not** the output of
  any actual program run.
- No scorer, `Scoring/` directory, or numbering-decision algorithm was written or is proposed by
  this document. This is evidence-gathering only, per the plan's evidence-first rule for M6.

Fixture source: `docs/fixtures/nzbhydra/` (see that directory's `README.md` for capture
provenance, redaction method, and known gaps). All apikeys are `REDACTED` and all host addresses
are already rewritten to the RFC 5737 documentation range `192.0.2.0/24` by the fixture capture
itself; no titles or links quoted below introduce any other identifying data.

---

## 1. `ghost-in-the-shell-arise-alternative-architecture.xml` (0 items)

Query: `t=search&q=Ghost in the Shell Arise Alternative Architecture`. Genuine zero-result
fixture — the channel element contains no `<item>` entries at all.

- Candidate numbering produced: N/A — zero results, no release data to reason about.
- Expected: N/A — *Ghost in the Shell: Arise* (2013-2014, a 4-part OVA + compilation film series,
  numbered as Border 1-4 rather than conventional season/episode) is a distinct series identity
  from both *Stand Alone Complex* and the generic/2045 franchise entries; no numbering can be
  evaluated without any returned releases.
- Mismatch class: **no data - zero results.**

## 2. `ac10-sweep-onepiece-zero-results.xml` (0 items)

Query: `t=search&q=One Piece&limit=100` (also tried as `t=tvsearch`, same result). Genuine
zero-result fixture — no `<item>` entries.

- Candidate numbering produced: N/A — zero results.
- Expected: N/A — *One Piece* uses a single long-running absolute/TVDB-seasonal numbering scheme
  with no arc-relative scene-season convention comparable to Bleach's; irrelevant here since no
  releases were returned to number.
- Mismatch class: **no data - zero results.**

## 3. `ghost-in-the-shell-stand-alone-complex.xml` (3 items)

Query: `t=search&q=Ghost in the Shell Stand Alone Complex`. All 3 items are single episodes from
EZTV, category 5000 (TV), title pattern `Ghost In The Shell Stand Alone Complex S0#E##`.

| Title (scene numbering as written) | Candidate numbering produced (manual reasoning) | Expected | Mismatch class |
|---|---|---|---|
| `...S02E01 Di Reactivation Reembody...` | SceneSeason=2, SceneEpisode=1, Absolute=null, ArcTitleToken=null → no arc map applies to this franchise entry (no XEM-style arc data exists for *Stand Alone Complex* in the fixtures/tests seen), so `Build` with `arcMap=null` would only look at `raw.Absolute`, which is null → **empty candidate set** (no candidates at all). | *Stand Alone Complex* Season 2 ("2nd GIG"), Episode 1, titled "Reactivation Reembody" — this really is TVDB-seasonal S02E01, no arc renumbering applies to this series. | **no data — title has scene season/episode but no absolute number, and no arc map exists for this series, so the builder as documented produces nothing usable without a `TvdbSeasonal`-scheme candidate, which `Build` does not generate at all (it only ever emits `ArcRelative` or `Absolute` schemes).** |
| `...S02E21 In Escape in Defeat Embarrassment...` | Same shape: SceneSeason=2, SceneEpisode=21, Absolute=null, ArcTitleToken=null → empty candidate set under the same reasoning. | *Stand Alone Complex* Season 2, Episode 21. | Same as above — **no data / scheme gap** (see note below). |
| `...S02E02 Di Well-Fed Me Night Cruise...` | SceneSeason=2, SceneEpisode=2, Absolute=null, ArcTitleToken=null → empty candidate set. | *Stand Alone Complex* Season 2, Episode 2. | Same as above. |

Note on this fixture: all three titles carry a completely ordinary, unambiguous TVDB-style
`S02E##` marker with no arc vocabulary and no absolute number. `CandidateNumberingSetBuilder.Build`
as documented never emits a `NumberingScheme.TvdbSeasonal` candidate under any input shown in the
builder or its tests — its two production paths are `ArcRelative` (only when an `arcMap` is
supplied and either an arc binding resolves or scene season ≠ 1) and `Absolute` (only when
`raw.Absolute` is present). A plain "season 2, episode 21, no arc map, no absolute" release —
exactly this fixture's shape — falls through both paths and would produce **zero candidates**,
even though the title is completely unambiguous to a human reader. This is observed directly from
reading the builder's code, not inferred.

## 4. `ghost-in-the-shell-generic.xml` (27 items)

Query: `t=search&q=The Ghost in the Shell`. Two distinct sub-series appear under one query, which
is itself the franchise-disambiguation condition this fixture exists to exercise (per the
fixtures README, AC-M2).

- 3 items are re-runs of the same *Stand Alone Complex* S02 releases already covered in fixture 3
  above (identical titles, same reasoning/mismatch class applies — omitted here to avoid
  duplication).
- 24 items are *Ghost in the Shell: SAC_2045* (2020-2022 CG-animated continuation), title patterns
  `Ghost in the Shell SAC 2045 S0#E##` / `Ghost in the Shell SAC2045 S0# ...` (season-pack, no
  episode number). Distinct episodes observed: S01E01, E03, E05-E12 (8 distinct S01 episodes) and
  S02E01 (pack only — no per-episode S02 item other than E03, E05, E06, E07, E08, E11, E13, E16,
  E24), plus 4 season-pack items with no episode number (`S01`, `S02` × several quality/dub
  variants).

| Representative title | Candidate numbering produced | Expected | Mismatch class |
|---|---|---|---|
| `Ghost in the Shell SAC 2045 S01E01 720p WEB H264-CONFRONT` | SceneSeason=1, SceneEpisode=1, Absolute=null, ArcTitleToken=null. No arc map exists for SAC_2045 (an entirely separate series identity from the original *Stand Alone Complex*, per README's AC-M2 disambiguation intent). With `arcMap=null`, `Build` only checks `raw.Absolute` (null) → **empty candidate set.** | *SAC_2045* Season 1, Episode 1 — ordinary TVDB-seasonal numbering, no arc renumbering. | **no data / scheme gap** — same root cause as fixture 3: `Build` has no `TvdbSeasonal` production path, so an unambiguous plain season/episode title with no arc map yields nothing. |
| `Ghost in the Shell SAC 2045 S02E13 1080p WEB H264-SUGOI` | SceneSeason=2, SceneEpisode=13, Absolute=null, ArcTitleToken=null → empty candidate set (same reasoning). | *SAC_2045* Season 2, Episode 13. | **no data / scheme gap.** |
| `Ghost in the Shell SAC2045 S01 JAPANESE 1080p WEBRip x265` (season pack) | No episode marker at all — SceneSeason=1, SceneEpisode=null, Absolute=null, ArcTitleToken=null. `Build` requires *both* `SceneSeason` and `SceneEpisode` to attempt arc binding, and has no `Absolute` to fall back on → **empty candidate set**, additionally because this is a season pack, not a single-episode release, which is a distinct problem (numbering scheme aside) not modelled by `RawReleaseNumbering`/`NumberingCandidate` at all (both are single-episode shaped). | Season pack covering all of *SAC_2045* Season 1. | **title has no per-episode season/episode markers (season pack) — no candidate-set shape exists for pack releases at all**, distinct from the plain scheme-gap mismatch above. |

Cross-series note: the presence of *Stand Alone Complex* S02 items and *SAC_2045* S01/S02 items
under the same generic query, with no franchise/series-identity distinguishing field on either
`RawReleaseNumbering` or `NumberingCandidate`, means numbering alone cannot separate these two
distinct series — franchise disambiguation is entirely a media-identity-resolution concern
upstream of numbering, consistent with the README's framing of this fixture as an AC-M2 case
rather than a numbering case per se. Recorded here because a hypothetical scorer working from
numbering alone would see two internally-consistent-looking S01E01, S02E13, etc. candidate shapes
that in fact belong to two unrelated shows.

## 5. `bleach-tvsearch.xml` (90 items)

Query: `t=tvsearch&q=Bleach`. This is the flagship arc-numbering fixture. Deduplicated by episode:
90 raw items reduce to **61 distinct (scene-season, scene-episode) pairs plus 2 season-pack items**
once near-identical quality/codec/release-group variants of the same episode are collapsed
(dedup rule: same scene season + scene episode + episode title fragment counted once, regardless
of resolution, HEVC/x264/AVC codec, WEB-DL/WEBRip/XviD source, or release group suffix — e.g. the
five `S17E44 THE PERFECT CRIMSON` variants at 1080p/720p/XviD from `MeGusta`, `playWEB`, and `AFG`
are one row below).

Three distinct title-pattern families were observed, all referring to the same TVDB-canonical
*Thousand-Year Blood War* content but rendered with three different scene-season conventions:

| Title-pattern family | Scene season observed | Distinct scene-episode range | Arc-title token present? | Count of distinct episodes |
|---|---|---|---|---|
| `Bleach S17E##...` (bare, no arc words) | 17 | E01-E45 (several, e.g. E06-E13, not present in this fixture at all — coverage is sparse/non-contiguous) | No (title carries only the numbers, e.g. `Bleach S17E21 1080p WEB h264-QUiNTESSENCE`) | ~24 episodes with no arc token |
| `Bleach S17E## Thousand-Year Blood War <episode name>...` | 17 | E17, E19, E29, E31-E36, E38-E45 | Yes — literal token `Thousand-Year Blood War` in title | ~15 episodes with arc token |
| `BLEACH Sennen Kessen hen S01E##...` / `Bleach Sennen Kessen Hen S01E##...` (mixed capitalization across releases) | 1 | E21-E40 (20 distinct episodes, contiguous) | Yes — token is `Sennen Kessen hen`, the Japanese-language rendering of "Thousand-Year Blood War" (not literally the same string as the English arc title used in the test fixture's arc map) | 20 episodes |
| `BLEACH Thousand-Year Blood War S03E##...` | 3 | E06, E08, E09, E10, E14 | Yes — literal token `Thousand-Year Blood War` | 5 episodes |
| `BLEACH Thousand-Year Blood War S01 JAPANESE...` (season pack, ×2, different bitrate) | 1 | none (pack) | Yes | pack, no per-episode data |

Worked reasoning for representative rows, hand-traced against `BleachArcRelativeNumberingTests`'s
`TybwBinding` (`ArcTitle: "Thousand-Year Blood War"`, `AlternateArcTitles: ["TYBW", "Thousand Year
Blood War"]`, `Season: 17`, `AbsoluteRangeStart: 367`, `AbsoluteRangeEnd: 402`):

| Title | Candidate numbering produced | Expected | Mismatch class |
|---|---|---|---|
| `Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB` | SceneSeason=17, SceneEpisode=45, Absolute=null, ArcTitleToken=null (no arc words in this particular release's title). With no `ArcTitleToken` and no `Absolute`, `ResolveBinding` returns null; since `sceneSeason (17) != 1`, the "carry through as-is" branch fires → `ArcRelative(Season:17, Episode:45, Absolute:null)`. | Real TYBW absolute range is 367-402 (per the test's own `TybwBinding`), so scene-relative episode 45 within season 17 is **out of the documented arc's 1-36 span** (episode 36 = absolute 402 is the arc's last episode per the flagship test). E45 falls past the end of the range the test fixture models — this is either a genuinely later-numbered TYBW episode not covered by the test's binding, or a sign the arc map used in tests is incomplete/truncated relative to the real broadcast run. | **correct scheme, no arc-title-token match to confirm it, and absolute number left unresolved (null)** — the candidate is plausible but weaker than the `Bleach S17E42 ... SON OF DARKNESS` (E42) row below because it lacks even the arc-token corroboration. |
| `Bleach S17E42 Thousand-Year Blood War SON OF DARKNESS 1080p DSNP WEB-DL AAC2 0 H 264-NTb` | SceneSeason=17, SceneEpisode=42, Absolute=null, ArcTitleToken="Thousand-Year Blood War" — this exact string matches `TybwBinding.ArcTitle` verbatim. `ResolveBinding` finds it by title token → binding resolves to TYBW (Season 17) → `Absolute` derived as `AbsoluteRangeStart + sceneEpisode - 1 = 367 + 42 - 1 = 408`, which is **past `AbsoluteRangeEnd` (402)** — the builder does not validate the derived absolute against the binding's own range end. | If TYBW's real absolute range genuinely ends at 402 (per the test fixture), E42's derived absolute of 408 is inconsistent with the binding's own stated range, since 402 - 367 + 1 = 36 total episodes in the arc, meaning scene-episode 42 shouldn't exist under this binding at all. | **arc-relative binding resolved via title-token match, but derived absolute falls outside the binding's own declared range — an internal consistency gap between scene-episode count and `AbsoluteRangeEnd`, not a scheme-confusion but a range-completeness issue** (observed directly from arithmetic against the test's own binding data, not fixture data). |
| `BLEACH Sennen Kessen hen S01E36 2024 1080p Baha WEB-DL x264 AAC-ADWeb` | SceneSeason=1, SceneEpisode=36, Absolute=null, ArcTitleToken="Sennen Kessen hen" — this token does **not** match `TybwBinding.ArcTitle` ("Thousand-Year Blood War") or any of its `AlternateArcTitles` (`"TYBW"`, `"Thousand Year Blood War"`) under `MatchesTitleToken`'s ordinal case-insensitive comparison, since "Sennen Kessen hen" is a different string entirely (Japanese-language arc name, not an English alternate spelling). `ResolveBinding` therefore fails the title-token path; with no `Absolute` to fall back on, `ResolveBinding` returns null. Since `sceneSeason == 1`, this hits the builder's explicit exclusion: **no `ArcRelative` candidate is generated at all** — this is exactly the AC-M1 regression shape the builder is designed to suppress. | This release *is* the real TYBW arc, episode 36 of that arc specifically — i.e., this is precisely the flagship `Bleach-17x36(402)` example from the test file, just captured from a different release group/source (Baha WEB-DL, Japanese-audio group `ADWeb`) that renders the season as `S01` with the Japanese arc name instead of `S17` with the English one. Per the test file's worked example, this should resolve to ArcRelative(Season:17, Episode:36, Absolute:402). | **arc-relative-vs-tvdb-seasonal confusion, compounded by an arc-title-token vocabulary gap**: the correct arc-relative interpretation is suppressed by the builder's AC-M1 season-1 exclusion rule specifically because this release's arc-title token uses a Japanese-language rendering not present in `TybwBinding.AlternateArcTitles`. The suppression is *correct behavior per AC-M1's intent* (never guess season-1-as-arc-relative), but the *underlying data gap* (missing alternate title) is what turns a resolvable case into a "produces nothing" case. This is the single clearest observed failure pattern in the whole Bleach fixture. |
| `BLEACH Thousand-Year Blood War S03E09 1080p WEB H264-KAWAII` | SceneSeason=3, SceneEpisode=9, Absolute=null, ArcTitleToken="Thousand-Year Blood War" (exact match to `TybwBinding.ArcTitle`). `ResolveBinding` finds the binding by title token regardless of the release's own claimed scene season (`ResolveBinding` never reads `raw.SceneSeason` — it only reads `ArcTitleToken` then `Absolute`) → binding resolves to TYBW (Season 17, overriding the release's own "S03" claim) → `Absolute` derived as `367 + 9 - 1 = 375`. | A third release-group convention (`KAWAII`) renders the same TYBW content as "S03" instead of "S17" or "S01". If this really is the same underlying episode content as one of the `S17E##`-family or `Sennen Kessen hen`-family episodes at a corresponding position, three different scene-season numbers (17, 1, 3) are being used across release groups for what should be one arc-relative episode number. | **ambiguous - multiple scene-season conventions collide on the same underlying arc**: because `ResolveBinding` trusts the arc-title token over the release's own claimed scene season, the *builder's* output here is arc-relative Season 17 (correct per its own logic) even though the release literally says "S03" — but nothing in this exercise's manual reasoning can confirm S03E09 and (e.g.) S17E?? actually refer to the same broadcast episode without an actual episode-title cross-check, which none of these three title families reliably provide in a machine-parseable way. |
| `Bleach S17E39 Thousand-Year Blood War The Visible Answer REPACK 1080p DSNP WEB-DL AAC2 0 H 264-NTb` | SceneSeason=17, SceneEpisode=39, Absolute=null, ArcTitleToken="Thousand-Year Blood War" → binding resolves to TYBW → derived absolute `367 + 39 - 1 = 405`, again past `AbsoluteRangeEnd` (402). | Same range-completeness concern as the E42 row above; additionally this title carries a `REPACK` tag, noted in the fixtures README as otherwise-unobserved across the corpus. | **correct arc-relative scheme resolution, same range-completeness gap as E42** — recorded to show the range issue is not a one-off but affects every `S17E##` episode above E36 that carries the arc token. |

Overall Bleach pattern summary (factual, no design implication): of the 61 distinct episodes,
the ones rendered as `S17E##` **without** the arc-title token in the title (~24 episodes) produce
a weaker, unconfirmed `ArcRelative(Season:17, Episode:N, Absolute:null)` candidate; the ones
rendered as `S17E##`/`S03E##` **with** the English arc-title token (~20 episodes) resolve
confidently via title-token match but many (E37-E45 under the season-17 numbering, i.e. episodes
whose derived absolute exceeds 402) fall outside the test fixture's own declared absolute range;
and the entire `Sennen Kessen hen` family (20 episodes, all Japanese-titled Baha/ADWeb releases)
produces **no candidate at all**, because both its scene season (1, triggering the AC-M1
exclusion) and its arc-title token (a Japanese string absent from `TybwBinding.AlternateArcTitles`)
independently defeat resolution.

---

---

## EXECUTED (harness: `IdentityAndAiGateFixtureHarnessTests.Run_FixtureFile_IdentityAndAiGate_ProcessesEveryItem`)

Everything above this heading is hand-traced/synthetic reasoning, as stated in the SYNTHETIC
banner at the top of this document. That banner's "nothing can be run" framing is only true for
the numbering side (`CandidateNumberingSetBuilder`, which has no title parser feeding it). It is
**not true** for the identity-context side (`AlternateTitleMatcher`, `FranchiseClassifier`) or the
AI adjudicator (`ReleaseClassifier`) — both run today against raw fixture titles, with no parser
required, using `SeriesIdentity` objects and a fake `IOllamaClient`. This section reports the
result of an actual xUnit run, not reasoning.

Harness: `tests/Arbitarr.Integration.Tests/IdentityAndAiGateFixtureHarnessTests.cs`. For each named
fixture it loads every `<item><title>`, then for each item runs:
- (a) `AlternateTitleMatcher.FindMatchingTitles`/`Matches` against 4 known `SeriesIdentity` objects:
  the three GitS identities reused verbatim from `GhostInTheShellFranchiseClassificationTests`
  (Arise, Stand Alone Complex, SAC_2045) plus a Bleach identity defined locally in the harness (no
  `SeriesIdentity` for Bleach exists anywhere in the codebase — `BleachArcRelativeNumberingTests`
  only has arc/season numbering bindings, not an identity object).
- (b) `FranchiseClassifier.Classify` pairwise over the three GitS identities (SAC vs SAC_2045, SAC
  vs Arise, SAC_2045 vs Arise).
- (c) `ReleaseClassifier.TryClassifyAsync` against two fake `IOllamaClient`s: one fixed to
  `accept`/1.0, one fixed to `reject`/0.0.

The harness asserts only `processedCount == fixture item count` per file — it makes zero
correctness assertions. Run (pre-M6-2, before the fix below): `dotnet test
tests/Arbitarr.Integration.Tests` under build-lock → **Test Run Successful. Total tests: 6. Passed:
6.** (5 fixture theory cases + 1 placeholder test.) Post-M6-2 (see below), the project gained one
more test elsewhere (unrelated to this change): **Total tests: 7. Passed: 7.**

### Per-fixture item counts (all asserted count == fixture item count)

| Fixture | Items processed |
|---|---|
| `ghost-in-the-shell-arise-alternative-architecture.xml` | 0 |
| `ac10-sweep-onepiece-zero-results.xml` | 0 |
| `ghost-in-the-shell-stand-alone-complex.xml` | 3 |
| `ghost-in-the-shell-generic.xml` | 27 |
| `bleach-tvsearch.xml` | 90 |

Both zero-item fixtures are **genuinely empty channels** (no `<item>` elements at all) — confirmed
by reading the raw XML, not a harness bug. Notably, `ghost-in-the-shell-arise-alternative-
architecture.xml` has zero items despite its filename, which was not expected going in; this
document's own item 1 above already recorded this as "no data — zero results" for the numbering
side, and the executed run confirms the same is true for the identity/AI side: there is nothing to
run identity matching or AI-gating against for this fixture at all, full stop.

### (a) `AlternateTitleMatcher` result — CONTRADICTS an implicit assumption in the hand-traced sections above

**Every single processed item across all three non-empty fixtures (120 items total: 3 + 27 + 90)
returned `(none)` — zero identity matches, for every fixture, including the GitS Stand Alone
Complex fixture matched against its own `StandAloneComplex` identity.** Representative row (from
`ghost-in-the-shell-stand-alone-complex.xml`):

| title | identity matches |
|---|---|
| `Ghost In The Shell Stand Alone Complex S02E01 Di Reactivation Reembody EAC3 5 1 1080p Bluray x265-iVy` | (none) |

Root cause, confirmed by reading `src/Arbitarr.Media/Identity/AlternateTitleMatcher.cs`:
`Matches`/`FindMatchingTitles` require **exact full-string equality** (trimmed, ordinal
case-insensitive) between the entire release title and the entire `PrimaryTitle` or one entire
`AlternateTitles` entry — there is no substring, token, or fuzzy match. A raw Torznab release
title (which always carries season/episode markers, resolution, codec, release group, etc. appended
to the series name) can never equal a bare canonical series title like `"Ghost in the Shell: Stand
Alone Complex"` under this comparison. This is a real, executable finding, not a hand-traced
inference: **`AlternateTitleMatcher` as it exists today cannot be used directly against raw release
titles at all** — it requires a title-extraction/normalization step upstream (presumably
`TitleNormalizer`, per M5) to first isolate the series-name portion of the release title before
comparison, and no such wiring exists yet anywhere in the codebase. None of the hand-traced
sections above claimed otherwise (they are entirely about the numbering side), but this is worth
recording explicitly since it is exactly the kind of "identity context still gets results wrong"
finding plan step 1 asks for, and it generalizes beyond the two franchise fixtures to every fixture
in the corpus.

### M6-2 update: `SeriesNameExtractor.Extract` wired ahead of `AlternateTitleMatcher.Matches` in the harness

Per the (a) root-cause finding above, the harness's step (a) has been changed from matching the
**raw** release title directly to first calling `SeriesNameExtractor.Extract(title)` (falling back
to the raw title only when `Extract` returns `null`), then matching the extracted series name. This
is a harness-only change (`tests/Arbitarr.Integration.Tests/IdentityAndAiGateFixtureHarnessTests.cs`
lines ~129-137) — no `Arbitarr.Media`/`Arbitarr.Core.Identity` production code changed, consistent
with `AlternateTitleMatcher`'s deliberately caller-side-composition design (see
`SeriesNameExtractorMatcherIntegrationTests.cs`).

**Before → after, re-running `dotnet test tests/Arbitarr.Integration.Tests
--logger "console;verbosity=detailed"`:**

| Fixture | Items | `(none)` before | `(none)` after | Delta |
|---|---|---|---|---|
| `ghost-in-the-shell-arise-alternative-architecture.xml` | 0 | 0 | 0 | — (empty fixture) |
| `ac10-sweep-onepiece-zero-results.xml` | 0 | 0 | 0 | — (empty fixture) |
| `ghost-in-the-shell-stand-alone-complex.xml` | 3 | 3 | 3 | no change |
| `ghost-in-the-shell-generic.xml` | 27 | 27 | 27 | no change |
| `bleach-tvsearch.xml` | 90 | 90 | 7 | **83 items now match `Bleach`** |
| **Total (non-empty fixtures)** | **120** | **120** | **37** | **83 gained, 0 regressed** |

83 of the 90 `bleach-tvsearch.xml` items now correctly resolve to identity `Bleach` (extracted
series names like `"Bleach"` and `"BLEACH"` now exact-match the `Bleach` identity's `PrimaryTitle`
post-extraction, where the raw release title never could). This is a real, positive, non-regressing
improvement: every item that matched `(none)` before still does now (37 residual `(none)` results
are a strict subset of the prior 120), and nothing that used to match now fails to.

**Residual `(none)` results (37 total) are a known, out-of-scope-for-M6 gap, not a regression** —
all trace to a colon-punctuation mismatch between `SeriesNameExtractor`'s output (which strips
punctuation it doesn't recognize as a title separator) and the declared `SeriesIdentity`
titles/alternates (which retain the colon):

- `ghost-in-the-shell-generic.xml` (27/27 still `(none)`): extracted series names such as `"Ghost In
  The Shell SAC 2045"`/`"...SAC2045"` (no colon) don't exact-match `Sac2045`'s
  `PrimaryTitle: "Ghost in the Shell: SAC_2045"` (has colon) or its empty `AlternateTitles`.
- `ghost-in-the-shell-stand-alone-complex.xml` (3/3 still `(none)`): extracted `"Ghost In The Shell
  Stand Alone Complex"` (no colon) doesn't exact-match `StandAloneComplex`'s
  `PrimaryTitle: "Ghost in the Shell: Stand Alone Complex"` (has colon).
- `bleach-tvsearch.xml` residual (7/90 still `(none)`): all extract to `"BLEACH Thousand-Year Blood
  War"` (no colon), which doesn't exact-match `Bleach`'s alternate
  `"Bleach: Thousand-Year Blood War"` (has colon).

Per explicit instruction, `SeriesNameExtractor` is **not** being tuned in this M6 pass to close this
gap — it is recorded here as evidence for a future pass, same as the pre-existing findings above.

### (b) `FranchiseClassifier.Classify` result — confirms the hand-trace's sibling framing, executed

All three pairwise GitS classifications returned `Sibling` for every processed item (the pairwise
result is identity-vs-identity, so it is constant per fixture, not per title):

- `Stand Alone Complex` vs `SAC_2045` → **Sibling**
- `Stand Alone Complex` vs `Arise` → **Sibling**
- `SAC_2045` vs `Arise` → **Sibling**

This matches this document's existing framing (item 4's "Cross-series note") that these are
distinct-but-related series requiring franchise disambiguation upstream of numbering — now
confirmed by actually running the classifier rather than assuming its behavior.

### (c) `ReleaseClassifier.TryClassifyAsync` result — both gate extremes behave as expected, executed

For every one of the 120 processed items, the accept-fixed fake client produced `accept/1` and the
reject-fixed fake client produced `reject/0` — i.e. `TryClassifyAsync`'s fail-open wrapper passes
the fake client's verdict straight through unmodified in both directions, with no items silently
dropped to null. This confirms the AI-adjudicator gate itself does not need a live Ollama instance
to exercise its plumbing end-to-end (construction → per-candidate call → verdict passthrough); only
the model's actual judgment is unavailable without a live instance, exactly as this document's
banner already stated.

### Mismatch-class table (executed, subset — full 120-row output reproducible via `dotnet test
tests/Arbitarr.Integration.Tests --logger "console;verbosity=detailed"`)

| title | identity-context result | AI-gate result | expected | mismatch class |
|---|---|---|---|---|
| `Ghost In The Shell Stand Alone Complex S02E01 Di Reactivation Reembody EAC3 5 1 1080p Bluray x265-iVy` | (none) — see (a) root cause above | accept/1 (accept-gate), reject/0 (reject-gate) — gate passthrough correct | Should positively identify as *Stand Alone Complex* via alternate-title matching | **identity-matcher input-shape gap**: exact-string matcher fed a raw release title it can never equal |
| `Ghost in the Shell SAC 2045 S01E01 720p WEB H264-CONFRONT` | (none) — same root cause | accept/1, reject/0 | Should positively identify as *SAC_2045* | Same as above |
| `BLEACH Sennen Kessen hen S01E36 2024 1080p Baha WEB-DL x264 AAC-ADWeb` | (none) — same root cause (also independently noted in this doc's item 5 hand-trace as failing arc-title-token matching for numbering, for an unrelated reason) | accept/1, reject/0 | Should positively identify as *Bleach* | Same identity-matcher gap, additionally already known to fail numbering for a separate reason (arc-title vocabulary gap, see item 5 above) |
| `Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB` | (none) — same root cause | accept/1, reject/0 | Should positively identify as *Bleach* | Same identity-matcher gap |

## Confirmed: no secrets or non-documentation-range IPs introduced

All titles/links quoted above are copied verbatim from already-redacted/rewritten fixture files
(apikeys already `REDACTED`, hosts already in `192.0.2.0/24` per the fixtures README) or are not
quoted from links/enclosures at all (only `<title>` text is used in the tables). No new IP
addresses, hostnames, or apikey values were introduced by this document.
