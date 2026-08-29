# Arbitarr

An identity-aware search broker that sits between [Sonarr](https://sonarr.tv)/[Radarr](https://radarr.video) and [NZBHydra2](https://github.com/theotherp/nzbhydra2), speaking Torznab/Newznab on both sides. It exists to answer one question better than fuzzy title matching ever can: **is this release actually the episode you asked for?**

Arbitarr resolves what a release *is* — which series, which numbering scheme, which episode — before handing results downstream, and uses a local LLM (via [Ollama](https://ollama.com)) to arbitrate the matches that deterministic rules can't settle.

> ⚠️ **Early development.** Nothing here is ready to run in your stack yet. The API surface, configuration format, and deployment story are all still being built.

## Why

Anime and long-running franchises break the assumptions the *arr ecosystem's title matching relies on:

- **Numbering schemes collide.** A release named `Bleach - 402` might be absolute episode 402, or arc-relative episode 36 of the Thousand-Year Blood War — and on [TheXEM](https://thexem.info) those can map to *different* episodes. When a mapping is genuinely ambiguous, Arbitarr admits no match and says why, rather than grabbing the wrong one.
- **Franchises get merged.** *Ghost in the Shell: Stand Alone Complex*, *Arise*, and the original film are three different identities that fuzzy matching happily conflates. Arbitarr classifies them as siblings — related, never merged, never silently dropped.
- **Failure is invisible.** When a metadata source is down or has no coverage, most tooling degrades silently. Every match Arbitarr produces carries provenance: which source resolved the identity, what evidence supported it, and flags for anything degraded along the way (`ambiguous-mapping`, `source-unreachable`, `no-xem-coverage`, ...).

## How it works

```
Sonarr/Radarr ──Torznab──▶ Arbitarr ──Torznab──▶ NZBHydra2 ──▶ indexers
                              │
                              ├─ identity resolution: *arr API → TheXEM → Anime-Lists
                              ├─ candidate numbering sets + ambiguity policy
                              ├─ snapshot-versioned metadata cache (SQLite)
                              └─ LLM arbitration (local Ollama) for the hard cases
```

Identity metadata is resolved through a fixed provider order — the *arr instance's own API (authoritative), then TheXEM's mapping endpoints, then the Anime-Lists dataset (fetched at runtime, rate-limited, never vendored) — and cached with source-snapshot versioning so upstream edits invalidate stale entries.

## Repository layout

| Project | Role |
|---|---|
| `src/Arbitarr.Core` | Domain primitives and shared contracts |
| `src/Arbitarr.Core.Identity` | Identity contracts: provenance, numbering candidates, match results |
| `src/Arbitarr.Media` | Identity resolution: providers, numbering/ambiguity policy, franchise classification, metadata cache |
| `src/Arbitarr.Sources.NzbHydra` | Torznab client for the NZBHydra2 upstream |
| `src/Arbitarr.Ai` | LLM arbitration (kept strictly independent of `Media` — enforced by architecture tests) |
| `src/Arbitarr.Data` | SQLite persistence (EF Core) |
| `src/Arbitarr.Api` | Torznab/Newznab endpoint surface |
| `src/Arbitarr.Host` | Composition root / entry point |
| `tests/` | xUnit test projects per component, plus architecture and integration tests |
| `docs/` | Design notes, measurements, and captured (fully redacted) upstream fixtures |

The solution still uses the working name `Arbitarr` internally; a rename to `Arbitarr` is planned.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
```

```bash
dotnet test
```

## Privacy and secrets

No credentials, API keys, or real network addresses are committed to this repository. Captured fixtures are scrubbed (`apikey=REDACTED`, hosts rewritten to RFC 5737 documentation addresses), and the repo enforces this with a pre-commit guard plus GitHub secret scanning with push protection.

## Status

Foundation work is in progress: the Torznab pipeline, SQLite caching layer, and the media-identity context (providers, ambiguity policy, provenance) are built and under test. Ranking/scoring, the LLM arbitration loop, and Docker packaging are next.

## License

Arbitarr is licensed under the [GNU General Public License v3.0](LICENSE).
