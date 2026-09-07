# 0004. Make the admin key write-only, reachable through a local-network bootstrap bypass

- **Status:** Accepted
- **Date:** 2026-09-06

## Context

Admin-mutating routes are gated by an admin key. That gate created a deadlock: the key lives in the
`Settings` table, the only route that can write it is itself admin-mutating, and an absolute
fail-closed rule therefore made a fresh install permanently unreachable. No operator, on any
deployment, could set a first key. (See issue #43.)

The obvious repair — add `AdminApiKey` to `SettingsCatalog` so the existing settings route can
write it — is wrong in a way that is easy to miss: the catalog feeds **both** that route's PUT
allow-list **and** its GET projection. Making the key writable there also publishes it on the read
surface.

## Decision

Two halves, neither sufficient alone:

1. **A dedicated write-only route**, `PUT /api/admin/security/admin-key`
   (`AdminSecurityEndpoints`), outside the catalog. There is no GET and no projection of the key
   anywhere; once set it can be replaced but never read back. A `204` carries no body, so not even
   an echo of the accepted value leaves the process. `PUT /api/admin/settings/AdminApiKey` stays a
   404 and `GET /api/admin/settings` never carries the value.

2. **A bootstrap bypass** (`AdminApiKeyFilter`): while no key is configured, admin-mutating routes
   are permitted from loopback and RFC 1918 addresses and refused with `503` elsewhere. The moment
   a key is stored the branch stops running and the gate is absolute again for every source
   address. It logs at **Warning**, not Information, for as long as it stays open.

Trust is decided **only** from `ConnectionInfo.RemoteIpAddress` — the peer on the actual socket.
`X-Forwarded-For` and every other header is ignored, because headers are attacker-controlled unless
ForwardedHeaders middleware runs with a known-proxy allow-list, and it does not. A null
`RemoteIpAddress` is untrusted: unknown is not local.

## Alternatives rejected

- **Add `AdminApiKey` to `SettingsCatalog`.** Rejected: publishes the secret on the GET projection.
  `SettingsCatalog` carries a load-bearing comment saying the exclusion is deliberate.
- **An environment variable for the key.** Rejected: it splits the source of truth between config
  and database, and leaves the "change it at runtime" path still deadlocked.
- **Ship a default or generated key.** Rejected: a default key is no key, and a generated one has
  to be surfaced somewhere — which reintroduces a read path.
- **Trust `X-Forwarded-For` for the bypass.** Rejected: without a proxy allow-list any remote
  caller can claim to be loopback, which converts the bootstrap window into an open door.

## Consequences

- A fresh install is LAN-open until the operator sets a key. This is a real exposure window, chosen
  over permanent unreachability, and it is why the log line is Warning and repeats.
- The key cannot be recovered, only replaced. Losing it means writing the `Settings` row directly.
- Validation stays in `SettingsValidator.ValidateAdminApiKey` (16-character floor), reached through
  the repository's own switch — one floor, in one place, not duplicated in the endpoint.
- The route binds its body **optionally** (`EmptyBodyBehavior.Allow`); see
  [docs/standards/architecture.md](../standards/architecture.md) for why a required body would leak
  route existence to unauthenticated callers.
