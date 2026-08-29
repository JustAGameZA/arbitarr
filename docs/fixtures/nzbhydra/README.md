# NZBHydra2 upstream fixture captures

Real Torznab/RSS responses captured from a live NZBHydra2 instance for use as test corpora by
Step 3a (media-identity resolution) and Step 6 (AI classification / AC10 corpus). This is a
one-time manual capture, not an automated test — do not wire these into a scheduled re-capture
without re-reading the credential-handling constraints below.

- **Capture date:** 2026-08-27
- **Upstream:** a live NZBHydra2 instance (base URL and API key intentionally not recorded here or
  anywhere else in this repo — see `docs/step0-measurements.md` for the base URL used during
  Step 0 measurement work). Credentials were read from a session-local scratchpad `infra.env` file
  that is never committed and is outside this repository.
- **Endpoint:** `/torznab/api` (Torznab-compatible search), plus one `/torznab/api?t=caps` request
  used only to confirm connectivity and to inspect advertised categories/searching params (that
  caps response is not saved as a fixture here — it belongs to the caps-aggregation worker's scope,
  not this data-capture task).
- **Redaction:** every fixture's `apikey` query-string value has been replaced with the literal
  string `REDACTED`, both in the request-describing text and in the per-item `enclosure`
  download URLs embedded in the response bodies (NZBHydra2 embeds the apikey in each release's
  download link — this was discovered during capture and scrubbed before any file was written
  under this directory). Additionally, all internal LAN host addresses embedded in URLs (download
  links, `hydraIndexerHost` attributes) have been rewritten to documentation-range addresses
  (RFC 5737 `192.0.2.0/24`) so no real network topology is recorded in this repository. Ports and
  the final octet of each address were preserved, so distinct hosts remain distinguishable. All
  other field values are byte-identical to what upstream returned.

## Fixtures

| File | Query (`t`, `q` params; apikey redacted) | Items returned |
|---|---|---|
| `bleach-tvsearch.xml` | `t=tvsearch&q=Bleach&apikey=REDACTED` | 90 |
| `ghost-in-the-shell-arise-alternative-architecture.xml` | `t=search&q=Ghost in the Shell Arise Alternative Architecture&apikey=REDACTED` | 0 (see Notes) |
| `ghost-in-the-shell-stand-alone-complex.xml` | `t=search&q=Ghost in the Shell Stand Alone Complex&apikey=REDACTED` | 3 |
| `ghost-in-the-shell-generic.xml` | `t=search&q=The Ghost in the Shell&apikey=REDACTED` | 27 |
| `ac10-sweep-anime.xml` | `t=search&q=anime&limit=100&apikey=REDACTED` | 57 |
| `ac10-sweep-movie.xml` | `t=movie&q=Dune&limit=100&apikey=REDACTED` | 78 |
| `ac10-sweep-general-1080p.xml` | `t=search&q=1080p&limit=100&apikey=REDACTED` | 100 |
| `ac10-sweep-onepiece-zero-results.xml` | `t=search&q=One Piece&limit=100&apikey=REDACTED` (also tried as `t=tvsearch`, same result) | 0 (see Notes) |

Total non-zero items captured across the AC10-eligible fixtures (Bleach + all GitS + AC10 sweeps,
excluding the two genuine zero-result queries): **355 items**, which clears the plan's stated
AC10 corpus floor of ≥300 releases. Whoever builds the actual AC10 corpus in Step 6 should treat
this as raw material to select/curate from, not as the finished corpus — see Notes below on gaps.

## Which step(s) each fixture supports

- `bleach-tvsearch.xml` — Step 3a (media-identity resolution test data) and Step 6 AC10 corpus raw
  material.
- `ghost-in-the-shell-*.xml` (all three) — Step 3a's franchise-disambiguation tests (AC-M2): these
  three queries are the plan's specified trio (*Arise – Alternative Architecture*, *Stand Alone
  Complex* (2002), and a generic *The Ghost in the Shell* entry) that must resolve to three
  **distinct** series identities, not be merged by fuzzy title matching.
- `ac10-sweep-*.xml` — Step 6's AC10 (and AC26, which reuses/extends the AC10 corpus) fixture
  corpus raw material.

## Notes and honest gaps (read before relying on this data)

- **`ghost-in-the-shell-arise-alternative-architecture.xml` returned zero items.** Tried both the
  full plan-specified query text (`Ghost in the Shell Arise Alternative Architecture`) and a
  shorter variant (`Ghost in the Shell Arise`) — both returned zero results from this instance's
  currently configured indexers at capture time. This is reported honestly rather than
  substituted with a different query to force a non-empty result. Step 3a will need to either
  accept this as a true negative case or re-capture later against different indexer configuration.
- **`ac10-sweep-onepiece-zero-results.xml` returned zero items** for both `t=tvsearch` and
  `t=search` against `q=One Piece`. Kept as a fixture anyway since a genuine zero-result response
  is itself useful signal, but it does not contribute to the AC10 item count above.
- **Every item captured across all eight fixtures is a torrent release** (`enclosure
  type="application/x-bittorrent"`). No Usenet (`application/x-nzb`) items were returned by any
  query. This means, as captured, **this data alone cannot satisfy AC10's ≥40% Usenet / ≥40%
  torrent composition requirement** — the live instance's currently active/responding indexers
  appear to be torrent-only at capture time. Whoever assembles the final AC10 corpus in Step 6
  will need either a re-capture against an instance with working Usenet indexers, or a
  supplementary Usenet-specific capture.
- **No obfuscated (32-hex-character) Usenet release names were observed** in any fixture, which
  follows directly from the torrent-only result above (obfuscation is a Usenet-indexer
  convention). AC10's ≥30 obfuscated-Usenet-name requirement is **not met by this capture** and
  needs a separate source.
- **No `PROPER`/`REPACK` tags were observed**; `REMUX` and multi-audio-style tags (e.g. `DTS-HD
  MA2 0`) do appear in `ac10-sweep-anime.xml`, so that stratum is partially represented.
- Titles observed are all English/Romanized-English release-scene naming; no clearly non-English
  script titles were spotted in a quick scan, though this was not exhaustively verified per file.
- The upstream instance's `t=caps` response (checked but not saved here) advertises
  `book-search available="yes"`. This is the caps-aggregation worker's concern (the plan requires
  `book` to never be advertised downstream regardless of upstream), not something addressed by
  this fixture-capture task — flagged here only so it isn't missed.

## Credential handling confirmation

No apikey, base URL, or other credential value appears in any file in this directory (or anywhere
else in this repository) — verified by grepping this directory for the literal apikey string
immediately before and after writing these files. The credential lived only in the session
scratchpad's `infra.env`, which is outside the repository and was never copied into it.
