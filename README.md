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

### Torznab caps aggregation

When Arbitarr fronts more than one upstream source, the Torznab `caps` response it advertises is a merge of each source's own caps, not a simple passthrough:

- **Categories** are unioned across all sources, so a category is offered if any source supports it. **Anime search is included in that union** — `SupportsAnimeSearch` is advertised as `true` if *any* contributing source reports anime support, even if the others don't, so anime stays selectable rather than being narrowed to the intersection.
- **Book categories are always excluded**, unconditionally, regardless of what any individual upstream advertises.
- **Search params** (e.g. `season`, `ep`, `imdbid`) are intersected — a param is only advertised if every source supports it.
- If fetching a source's caps fails, Arbitarr falls back to that source's last-known-good cached caps rather than letting a single dead upstream shrink the merged result.

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
| `src/Arbitarr.Web` | Admin UI (React + TypeScript, Vite), served under `/admin/` |
| `tests/` | xUnit test projects per component, plus architecture and integration tests |
| `design-system/` | UI patterns and component contracts (the palette itself lives in `theme.css`) |
| `docs/adr/` | Architecture decision records |
| `docs/standards/` | Checkable rules — architecture, data, process |
| `docs/` | Design notes, measurements, and captured (fully redacted) upstream fixtures |

## Running with Docker

> All addresses below are RFC 5737/2606 documentation placeholders — replace them with your own hosts and keys.

Quick start with the reference [`docker-compose.yml`](docker-compose.yml):

```bash
git clone https://github.com/JustAGameZA/arbitarr.git
cd arbitarr
# Edit docker-compose.yml: set your NZBHydra2 endpoint and keys first.
docker compose up -d --build
```

Arbitarr listens on port `8080`. The Torznab endpoint your *arr apps point at is
`http://arbitarr.example.invalid:8080/torznab/api` with the client `apikey` you
configured; the admin UI is served at `http://arbitarr.example.invalid:8080/admin/`.

> **Keep a restart policy on the service.** The reference compose file sets
> `restart: unless-stopped`, and that is load-bearing rather than a convenience:
> restoring a backup from the UI **stops** Arbitarr so the restored database and
> release-GUID secret are actually loaded, and it comes back only if your
> deployment restarts it. Without a restart policy a restore leaves Arbitarr
> stopped until you start it again by hand. See
> [ADR 0007](docs/adr/0007-restart-rather-than-reload-after-restore.md).

### Environment variables

| Variable | Purpose |
|---|---|
| `ARBITARR__SOURCES__NZBHYDRA__BASEURL` | Upstream NZBHydra2 instance Arbitarr queries (e.g. `http://nzbhydra2.example.invalid:5076`) |
| `ARBITARR__SOURCES__NZBHYDRA__APIKEY` | NZBHydra2's own API key (outbound credential) |
| `ARBITARR__APIKEY` | Legacy single inbound Torznab/Newznab client key; remains accepted after upgrading |
| `ARBITARR__CLIENTAPIKEYS__<n>__NAME` / `...__KEY` | Legacy named inbound client keys; remain accepted after upgrading |
| `ARBITARR_CONFIG_DIR` | Runtime state directory (defaults to `/config`; mount a volume there) |

Three distinct keys exist, deliberately: the **NZBHydra2 key** (Arbitarr calling out), the
**client key(s)** (*arr apps calling in), and the **admin key** (below, gating mutating admin
endpoints only).

For new search clients, create a named `ReadOnly` key in **Settings > API keys** and configure it
in Sonarr or Radarr. Minted keys work on Torznab, Newznab, and download links, can be revoked one
at a time, and record their last use. Existing `ARBITARR__APIKEY` and
`ARBITARR__CLIENTAPIKEYS__...` values continue to work unchanged after an upgrade, but are not
stored in the database or individually revocable.

### Admin key setup

Mutating admin endpoints (settings, filter rules) require an `X-Admin-Api-Key` header. Set the
key over the API, from a machine on the local network:

```bash
curl -X PUT http://arbitarr.example.invalid:8080/api/admin/security/admin-key \
  -H 'Content-Type: application/json' \
  -d '{"value":"REDACTED"}'
```

Generate the value yourself (e.g. `openssl rand -hex 32`); it must be at least 16 characters.
Once a key is set, that same route requires it like every other admin-mutating route — so
replacing the key later means sending the current one in an `X-Admin-Api-Key` header.

> **Use `https://` unless you are on loopback.** The bypass below accepts calls from anywhere on
> your LAN, and `http://` puts the key on the wire in cleartext for anything sniffing that
> segment — terminate TLS at a reverse proxy, or run this over a VPN. Passing the key with `-d`
> also leaves it in your shell history; `-d @keyfile.json` avoids that.

**Bootstrap bypass.** While no key is configured, admin-mutating routes are permitted from
loopback and RFC 1918 addresses, and refused with `503` from anywhere else. That is what makes
the call above possible on a fresh install without a chicken-and-egg deadlock. It is logged at
Warning for as long as it stays open: until you set a key, your admin surface is open to
everything on your LAN. Set one as a first-run step.

The key is **write-only** — there is no route that reads it back, it is deliberately absent from
the settings catalog, and `GET /api/admin/settings` never carries it. Read-only admin pages stay
ungated by design; only mutating routes check the key.

### Signing in, and what to do when you cannot

The admin UI has a login: a username and a password, created once on first run. Visit the instance
from a machine on your local network and it offers account creation; after that first account
exists, the setup screen is closed permanently and everyone signs in.

The admin key above is **not** replaced by the login. It gates mutating admin routes for scripted
administration; humans log in. Sonarr, Radarr, and other search clients use a named client key from
**Settings > API keys** on Torznab, Newznab, and download routes instead.

When you are signed in, open **Settings** and use the **Account** section to change your password.
You must enter your current password and a new, non-empty password (there is no length minimum). A successful
change keeps the browser you used signed in and signs every other browser out, so changing it is the
right response when you think another session may be compromised. The admin key is not involved and
continues to work for machines.

> **There is no password reset. This is deliberate, not an omission.** A homelab appliance has no
> mail transport to send one, and a reset path reachable from the LAN would be a second way in,
> weaker than the first. Nothing will e-mail you a link.

If you are locked out of the login, you are not locked out of the instance. In rough order of
effort:

1. **Wait, if you are being rate-limited.** Repeated failures answer `429` for up to 15 minutes.
   Your account is *not* disabled — no number of failures can disable it — so the limit simply
   drains and you can try again.
2. **Machine access is unaffected.** The admin key still works on admin-mutating routes, so
   anything scripted keeps running while you sort the login out.
3. **Clear the accounts table** through the config bind mount and set the account up again. With
   the container stopped, in your config directory:

   ```bash
   sqlite3 arbitarr.db 'DELETE FROM Users; DELETE FROM Sessions;'
   ```

   On the next start the instance has no accounts, so it offers first-run setup again — to a caller
   on the local network only. Deleting the sessions too signs out any browser still holding a live
   cookie.
4. **Restore the configuration database from a backup**, if you would rather not edit it in place.

Note that `arbitarr.db` is the configuration database; `arbitarr-logs.db` beside it holds only log
entries and has nothing to do with sign-in.

> **Behind a reverse proxy, the per-address login limit becomes one shared counter.** Login
> failures are rate-limited per username *and* per source address, but the source address is taken
> from the socket peer — never from `X-Forwarded-For`, which any caller can set. If you terminate
> TLS at a proxy, every request arrives from the proxy's address, so the per-address budget (20
> failures per 15 minutes) is shared by everyone: one attacker can exhaust it and other operators
> then see `429` until the window drains. The per-username limit is unaffected and still bounds
> guessing against any individual account, and no account is ever locked out, so this is a
> temporary nuisance rather than a lockout.
>
> If you need to close it, register ASP.NET Core's `ForwardedHeaders` middleware with
> `KnownProxies` (or `KnownNetworks`) populated with your proxy's address, so forwarded headers are
> honoured *only* from that host. Do not enable forwarded headers without that allow-list: an
> unconditionally trusted `X-Forwarded-For` lets any caller claim any address, which defeats the
> per-address limit entirely and — more seriously — would let a remote caller present itself as
> local to the first-run setup and bootstrap-bypass checks.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test -m:1
```

The suite is run sequentially (`-m:1`): it is not reliably parallel-safe across assemblies, and a
parallel run both under-counts and invents failures.

The admin UI is built separately, from `src/Arbitarr.Web`:

```bash
npm ci
npm run typecheck && npm test && npm run lint
```

See [`src/Arbitarr.Web/README.md`](src/Arbitarr.Web/README.md) for the frontend's own rules, and
[CONTRIBUTING.md](CONTRIBUTING.md) for architecture boundaries, the secrets policy, and test
expectations. [CONTEXT.md](CONTEXT.md) defines the project's vocabulary.

## Continuous integration

Every PR must pass two required checks: `Build & test` (restore/build/test plus a test-count
floor and a tree-wide secret/topology guard) and `Deploy review environment` (builds the
container image and confirms it answers `GET /health`). A green `Deploy review environment`
check means the image builds and `/health` answers — **it does not mean anything was deployed**.
No review environment exists yet; CI never reaches the Unraid deployment target. See
`.github/workflows/deploy-review.yml` for the full explanation.

## Privacy and secrets

No credentials, API keys, or real network addresses are committed to this repository. Captured fixtures are scrubbed (`apikey=REDACTED`, hosts rewritten to RFC 5737 documentation addresses), and the repo enforces this with a pre-commit guard plus GitHub secret scanning with push protection.

## Status

Built and under test: the Torznab pipeline, the SQLite caching layer, the media-identity context,
the LLM arbitration loop (shadow mode by default), Docker packaging, and the admin UI — the
governed settings surface, the Activity/history view over the shared event store, the persistent
log store with its admin API and tabbed System page, and the AI verdict review queue.

In review: the numbering scorer and release ranking layer.

Next: authentication beyond the single admin key (real accounts and a session; per-client API
keys), moving indexer and source configuration out of environment variables into the UI, backup
and restore, notifications on suppression and source-failure events, and deployment hardening.
No review environment exists yet — see the CI note above.

## License

Arbitarr is licensed under the [GNU General Public License v3.0](LICENSE).
