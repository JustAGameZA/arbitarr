# 0010. A secret is never readable, and omission never clears it — clearing means deleting the owning thing

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

#92's architectural review flagged what looked like an inconsistency: `Notifications.tsx`'s
webhook settings offer an explicit two-step "Clear it" affordance, while Sources
(`AdminSourceEndpoints.cs`) and the admin key (ADR 0004) offer no clear route at all — a `PUT`
that omits the key just leaves the stored one in force. The reviewer's first pass read this as
Notifications being the odd one out and asked whether Sources and the admin key should grow a
matching clear button.

A second review pass inverted the question, and a third reviewer plus the repo owner ratified the
inversion: **Notifications is not the outlier.** The shared rule across every secret in this
codebase is already "never readable, and omission never clears" — `UpdateSourceRequest.ApiKey`
being `null` means "leave alone" by design (`AdminSourceEndpoints.cs`'s `UpdateSourceAsync`, and
the `.omc/plans/issues/053` hand-off it implements says omission must **never** mean clear), and
ADR 0004 gives the admin key that same replace-never-read posture with, deliberately, no clear
route whatsoever.

What actually differs is not the rule but **what owns the secret**. A source's key belongs to a
source row; the admin key belongs to nothing — it is a bare `Settings` entry with no parent to
delete. The webhook URL sits in between: it is Notifications' one configured secret, but
Notifications itself has no "row" a caller could delete to take it away, so its own explicit
`DELETE` is not a second mechanism — it is what "delete the owning thing" looks like when the
owning thing is a singleton rather than a collection member.

## Decision

One rule, applied uniformly: **a secret is never surfaced on any read path, and it is cleared
only by deleting the thing that owns it — never by omitting it on a write.**

- **Sources**: no clear route beyond `DELETE /api/admin/sources/{id}`, which removes the source
  row and its `source:{id}:api_key` `Settings` row in the same transaction
  (`SourceRepository.DeleteAsync`). A Sonarr-kind source configured with a key is the worked
  example: there is no `PUT .../sources/{id}` shape that clears the key while keeping the source,
  by design — the only way to stop the key existing is to delete the source that key belongs to.
  Wanting to keep the source but drop the key (a keyed-to-keyless operator scenario) is a real but
  currently hypothetical need; adding a dedicated clear affordance for that case is **deferred**
  until such a scenario is reported, rather than spent on a design correction nobody has asked for
  yet.
- **The webhook URL** (`AdminNotificationEndpoints.cs`'s `DELETE` on the notifications webhook
  route) is the reference shape for a secret with no owning row of its own: since the URL has no
  parent entity to delete, the bodiless `DELETE` on its own route **is** "delete the owning thing"
  — the owning thing and the secret coincide. This is why its two-step UI confirmation looked like
  a special affordance but is not a different mechanism from the Sources case.
- **The admin key never gets a clear route, under any UI or API shape.** It is the one secret with
  no owning row to delete short of deleting the whole instance's configuration. Clearing it would
  set the resolved credential state back to "unconfigured", which reopens ADR 0004's LAN bootstrap
  bypass — the very door that bypass exists to close once an operator has locked it. The current
  credential resolver, `CredentialResolution` (`src/Arbitarr.Core/Security/CredentialResolution.cs`,
  renamed from `AdminKeyResolution` by arb-rh7 once #44 gave it session outcomes as well as key
  outcomes to carry) reports a `NotConfigured` outcome only when no key has ever been stored; there
  is deliberately no operation anywhere in the admin surface that can produce that outcome again
  once a key exists.

Every clear operation, present or future, must be a `DELETE` bound with no body — the same
required-body trap `docs/standards/architecture.md` documents for every other admin route applies
identically here: a clear route is exactly as mutating and exactly as gate-sensitive as any other
admin write.

## Alternatives rejected

- **Give Sources and the admin key an explicit "Clear it" step matching Notifications.** Rejected:
  this was the original #92 framing, and it turned out to be solving a problem that does not
  exist — Sources already has a clear mechanism (delete the source), and the admin key must
  never have one. Adding a redundant clear-in-place route to Sources would create a second way to
  reach the same end state as delete, doubling the surface that has to stay correct for no gain.
- **Remove Notifications' "Clear it" affordance to make all three secrets symmetric by having
  none clear in place.** Rejected once the ownership distinction was seen: Notifications has
  nothing else to delete that would take the webhook URL down with it, so removing its clear route
  would leave webhook URLs with no way to be cleared at all, which is a real regression the other
  two cases don't share.
- **A generic "clear this secret" endpoint parameterised by secret name.** Rejected: it would have
  to special-case the admin key to always refuse, which is exactly the kind of runtime check this
  design avoids everywhere else in favour of a route simply not existing. A door that is closed by
  a conditional inside a shared handler is one refactor away from being open; a door that was never
  built cannot be walked through by accident.

## Consequences

- Sources currently has no way to keep a source configured while dropping its key. This is a
  known, accepted gap rather than an oversight — CONTEXT.md and this ADR record it as deferred, so
  a future contributor does not "fix" it without first checking whether the deferral still holds.
- The admin key can only be replaced, never returned to `NotConfigured`. An operator who loses the
  key has no in-app recovery; ADR 0004 already documents that this is intentional and the only
  recovery is a direct `Settings` table edit.
- `SourceRepository.ReadApiKeyForUpstreamRequestAsync` remains the sole reader of a source's key
  value (docs/standards/architecture.md's second secrets-mechanism bullet); this ADR adds no new
  reader anywhere.
- Enforced by `Deleting_a_source_also_removes_its_stored_key`
  (`tests/Arbitarr.Integration.Tests/AdminSourceEndpointsTests.cs`), which reads the `Settings` row
  directly after the delete and asserts it is gone, and by the companion positive control added
  alongside this ADR, `Updating_a_source_with_apiKey_omitted_preserves_the_stored_key_value`, which
  reads the stored row's actual value (not just the `HasApiKey` boolean) before and after an
  omitted-key update to prove omission truly preserves the value rather than merely preserving
  presence.

Companion to [ADR 0004](0004-admin-key-write-only-with-bootstrap-bypass.md), which this ADR's
admin-key rule extends without restating.
