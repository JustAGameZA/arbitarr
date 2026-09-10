# 0012. Optional operator login: default-on LAN passthrough

- **Status:** Proposed (owner review required — reverses a prior owner ruling)
- **Date:** 2026-09-10

## Context

The operator asked for a login bypass for local-network access, enabled by default, and separately
for the 12-character password minimum to be dropped. This ADR records the first; the password floor
is a straightforward constant change noted under Consequences.

Arbitarr already has a local-network bypass, from #43/#44, and it is deliberately narrow. Its rule,
stated in `TrustedNetwork` and `ISessionAuthenticator` and quoted as constraint 1 of the owner
ruling on #44, is: **the LAN bypass skips the API key only, never the login.** Being on the LAN
makes a fresh install *administrable* while no credential exists; it does not make a caller a
logged-in operator. Both bootstrap paths (#43's unkeyed-admin bypass, #44's first-account setup)
stop applying permanently the moment the thing they bootstrap exists. `TrustedNetwork`'s own doc
says in as many words: "Do not add a third caller that treats a local address as a logged-in
operator — that is precisely what the owner ruling on #44 forbids."

This decision adds exactly that third caller, on purpose.

## Decision

When LAN passthrough is enabled, `AdminApiKeyFilter` admits any request whose **socket peer** is a
trusted local address (loopback, RFC1918, RFC4193 — the existing `TrustedNetwork` predicate, which
ignores all headers) to admin-mutating routes as a full-scope operator, **without** a session cookie
or an API key. It runs only after the key and the session have both declined, so a presented
credential is still judged on its own merits first. `GetSessionAsync` reports such a caller as
authenticated (with no username) so the SPA does not route them to the login screen. First-run
setup, when no account exists yet, still takes precedence.

It is **on by default**, disabled only by setting `ARBITARR_LAN_PASSTHROUGH` to `false`/`0`/`no`/`off`
at startup. The default is a code constant (`LanPassthroughOptions`), not a Settings row, so an
upgrade cannot silently hand an operator a different value.

## Alternatives considered, and why they were rejected in favour of this

- **Opt-in, off by default.** The safe form, and the one the codebase's posture points to. Rejected
  by the operator, who asked specifically for default-on.
- **Auto-login only in Development.** Convenience without weakening the deployed image. Rejected: the
  operator wants it on the real unraid deployment, which runs the Production image.
- **Keep the login; treat LAN + no account as the only bypass (status quo).** This is what #43/#44
  already do. Rejected because it does not satisfy the request — it still requires an account and a
  login once one exists.

## Consequences

- **This reverses owner-ruling constraint 1.** On a plain-HTTP LAN deployment, the human login and
  the admin key both become optional for anyone who can open a socket from a trusted address. That
  is the intended effect, not a side effect. It requires owner sign-off precisely because it undoes a
  decision a previous owner ruling made; a lead merge is not sufficient.
- **Reverse proxies make "local" mean the proxy.** Trust is the socket peer, and `X-Forwarded-For` is
  ignored by design. Behind nginx terminating TLS, every request arrives from the proxy's address, so
  passthrough trusts *everyone the proxy forwards* if that address is RFC1918, or *no one* if it is
  not. It is only meaningful as intended on a direct-to-app LAN, or with `ForwardedHeaders` configured
  against a known-proxy allow-list (which this app still does not do).
- **`SessionAndKeyAuthorizeIdenticallyTests` and the "third caller" prohibition are no longer true as
  written.** The prohibition in `TrustedNetwork`'s doc is now superseded by this ADR and must be
  re-worded rather than left to read as current; the single-model invariant (#44/#58) is unaffected,
  since passthrough still resolves to the same `Authorized`/`Admin` shape and adds no second outcome
  type.
- **The password floor drops from 12 to 1.** `UserRepository.MinPasswordLength` becomes 1 (any
  non-empty password). This removes the only credential-quality control the product had, on a setup
  screen any LAN caller can now reach. Empty passwords stay rejected because they cannot authenticate
  anyway. Recorded here so the two changes are reviewed as one decision.
- **Existing deployments change behaviour on upgrade.** With the default on, an instance that had a
  working login stops requiring it for LAN callers the moment this ships.
