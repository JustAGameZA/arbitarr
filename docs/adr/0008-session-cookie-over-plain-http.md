# 0008. Carry the session in a Lax cookie over plain HTTP, with a custom header as the CSRF control

- **Status:** Accepted
- **Date:** 2026-09-08

## Context

#44 gave Arbitarr human sign-in, which means a session credential now travels between the browser
and the API on every request. The credential has to survive a page reload, must not be readable by
script, and must work on **the deployment that actually exists**: a container on a home LAN,
reached over `http://` at a private address, with no TLS terminator in front of it.

That last constraint is the whole difficulty, and it is easy to miss because every published
recommendation assumes HTTPS. A browser does not warn when a `Secure` cookie is dropped on an
insecure origin — it discards it silently. Login would appear to succeed, the response would carry
a `Set-Cookie`, and the session would simply never arrive on the next request. The operator sees a
login form that "does nothing", with nothing in any log to explain it.

The cookie also has to be defended against CSRF, because a cookie is *ambient*: the browser
attaches it to a cross-site request without the page asking. The admin key has never needed this —
it is not ambient, so an attacker's page cannot make a browser send one.

## Decision

The session token travels in a cookie named `arbitarr_session` with:

- **`HttpOnly`** — script cannot read the token, so an XSS bug in the SPA cannot exfiltrate the
  session. This is why the token is *not* handed to the frontend to hold the way the admin key is:
  the admin key must be readable by script because script has to put it in a header, and a session
  token has no such requirement, so it gets the stronger protection.
- **`SameSite=Lax`** — blocks the cross-site POST that CSRF depends on, while still sending the
  cookie on a top-level navigation, so a bookmark into Arbitarr arrives signed in.
- **`Secure` set from `HttpContext.Request.IsHttps`, not hardcoded** — present exactly when it can
  be honoured. A TLS-terminating reverse proxy gets a `Secure` cookie; plain HTTP gets a working
  one.
- **No `Expires`/`Max-Age`** — a session cookie. Expiry is enforced server-side from the sessions
  table regardless, so a client-side lifetime would be decoration an attacker holding the token
  would ignore.

Alongside `SameSite`, a **second and independent** CSRF control: a cookie is honoured by the admin
gate only when the request also carries the custom header `X-Arbitarr-Session`. A cross-origin page
cannot set a custom header on a form or image request at all — doing so forces a CORS preflight,
and this application defines no CORS policy, so the preflight is refused and the real request is
never sent. The frontend attaches the header to every request rather than mirroring the gate's
path rules, because the cookie is ambient on every same-origin path and a per-path rule here would
have to stay in sync with which routes read it.

## Alternatives rejected

- **Hardcode `Secure = true`.** Rejected: it silently breaks login on the only deployment that
  exists. This is the single most likely "correction" a reviewer will propose, which is why the
  flag carries its reasoning at the call site as well as here.
- **The `__Host-` cookie prefix.** Rejected for the same reason: the prefix *requires* the `Secure`
  attribute, so a `__Host-` cookie is rejected outright by the browser on plain HTTP.
- **`SameSite=Strict`.** Rejected: it would drop the cookie on top-level navigation, so following a
  bookmark or a link into Arbitarr would land on a login form despite a live session. It buys
  little here because the custom-header requirement is already a second, stronger control.
- **A token in `localStorage`/`sessionStorage` with an `Authorization` header.** Rejected: it must
  be script-readable to be sent, which forfeits `HttpOnly` and makes any XSS a session compromise.
  It also contradicts this project's standing rule that no credential goes in browser storage.
- **A synchroniser/double-submit CSRF token.** Rejected as redundant: it defends by requiring a
  value the attacker cannot read, which is exactly what the custom header already achieves given
  no CORS policy — at the cost of a token to mint, store, rotate and expire.
- **Require TLS and document it.** Rejected: it makes the documented deployment non-functional
  rather than making it safe, and the residual exposure of a token on a LAN is the same exposure
  the admin key already has there. That is a property of running HTTP, not of this cookie.

## Consequences

- On a plain-HTTP LAN the session token is on the wire in cleartext, exactly as the admin key
  already is. TLS at a reverse proxy upgrades both automatically — `Secure` then appears without a
  code change, which is the point of deriving it from `IsHttps`.
- The custom header is required on every cookie-authenticated request, so any future non-browser
  client that wants to use a session (rather than a key) must send it. Machine callers should use a
  key and are unaffected.
- `SessionCookie` is the one place the flags are written, for setting and clearing alike — a
  browser deletes a cookie only when the attributes agree, so a second copy of these flags would be
  a logout that silently fails.
- Enforced by `AuthSessionEndpointsTests`, which asserts `HttpOnly` and `SameSite=Lax` are present and
  `Secure` is **absent** over plain HTTP, and by a test that a cookie presented without
  `X-Arbitarr-Session` does not authorise. That second test only bites on an instance with an admin
  key configured — otherwise ADR 0004's bootstrap bypass admits the caller regardless.

Companion to [ADR 0004](0004-admin-key-write-only-with-bootstrap-bypass.md), which governs the
other inbound credential the same gate accepts.
