# 0014. Refuse the whole 3xx range on upstream downloads

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

When Arbitarr fetches a download (NZB or torrent) from an upstream source, the upstream — in this case NZBHydra2 — is configured with two options for how it delivers content:

1. **Proxy mode** (`NZB access type = Proxy`): NZBHydra2 reads the NZB from the indexer and streams it back to the caller.
2. **Redirect mode** (`NZB access type = Redirect to indexer`): NZBHydra2 responds with an HTTP redirect (3xx) pointing the caller directly to the indexer's download URL.

A misconfigured upstream can cause every download attempt to fail. On 2026-09-08, an operator inadvertently set the NZBHydra2 instance to redirect mode, causing Arbitarr to refuse every download request (71 `SourceFailed` events in six minutes; 113 × 502 Bad Gateway responses in a single day). The operator had no visibility into the problem: the event reason text was the only mention of the setting name, and the dashboard reported NZBHydra2 as healthy despite zero successful downloads.

## Decision

**Arbitarr refuses the entire 3xx HTTP status range on download requests (including 304 Not Modified), with no allowance for following the redirect or retrying. The refusal is recorded as an unnamed `SourceFailed` activity event so operators can see it on the Activity feed, but it does not trigger notifications or alter the source's health status.**

SEC-M1 is implemented as `AllowAutoRedirect = false` across seven typed client registrations in the composition root, combined with origin-pinning re-validation at fetch time. This ensures redirects reach the application code as response objects rather than being silently followed by the framework, and any redirect to a different host is rejected by the fetch-time guard. When a redirect is detected on the download path, it is refused with a typed `UpstreamRedirectRefusedException` and recorded as a `SourceFailed` activity event (with `sourceDisplayName: null`) carrying a human-readable summary.

## Alternatives rejected

### Follow the redirect

Rejected: A redirect on the download path targets the indexer's own download URL, which is **off-origin** relative to the configured NZBHydra2 endpoint. Following it violates SEC-M1: the origin-pinning guard (`NzbHydraSource.TryValidateOriginPinnedLink`, lines 154–163) re-validates the link's scheme, host, and port against the configured upstream at fetch time. The origin-pinning mechanism is already in place to refuse exactly this case; enabling redirect-following would require weakening that guard.

### Follow only same-origin redirects

Rejected: This alternative appears to offer a middle path, but it is already a refusal by construction. The origin-pinned fetch guard (lines 154–163) would reject any redirect to a different host regardless. Enabling this "safe" subset would mean loosening the origin pin to allow same-origin redirects — which would be a real weakening of SEC-M1. The bug this PR addresses (redirect-to-indexer misconfiguration) would never match this alternative because the indexer is off-origin by definition.

## Consequences

### Operator must configure Proxy mode

The operator must set **NZBHydra2 Downloading > NZB access type = "Proxy"** (menu path as of NZBHydra2 v5) for any downloads to succeed. This is now documented in the README setup section and in this ADR. The setting applies to all Arbitarr instances pointing at that NZBHydra2 endpoint — it is not per-client.

### Unnamed event is recorded; source remains healthy

A refused redirect is recorded as a `SourceFailed` activity event with the source unnamed (deliberately: a named event would cause `NotificationPolicy.FoldSourceFailure` to announce a healthy source as down after three Sonarr retries). The event lands on the Activity feed with a human-readable summary (`"Download refused: NZBHydra2 redirected instead of serving the file"`), but operators who are not actively monitoring the dashboard will not be alerted. A separate persistent health item (arb-ln0 PR 2) will make the condition visible on the dashboard status block; persistence across restarts is tracked as bead arb-v3w.

### No outbound credential leak

The download request includes a query string carrying the NZBHydra2 API key. If the redirect were followed, the `Location` header would include that key. The HttpClientFactory logging handler logs request URIs but collapses query strings to `?*` by default (a .NET framework behavior pinned by `DisableUriRedactionSwitchTests` in CLAUDE.md §1); of the seven `AllowAutoRedirect = false` registrations, only the Ollama one is test-pinned (`ProgramOllamaHttpClientTests`). Following the redirect would require a second outbound request — a second call to `ReadApiKeyForUpstreamRequestAsync`, which violates the one-caller rule (CLAUDE.md §1) and would be a second site where credentials could leak. The `LogMessageCleanser` scrubs credentials in query strings but not in URL paths; a key in a redirect path segment (indexer-dependent) is covered by neither the redaction nor the cleanser.

### The whole 3xx range is refused, including 304 Not Modified

The download request sends no conditional headers (e.g., `If-None-Match`), so a 304 cannot legitimately arise on this path. It is refused like any other non-payload answer; treating it specially would add a special case for no real benefit.

### Circuit breaker records a success, not a failure

The catch block handling `UpstreamRedirectRefusedException` calls `RecordSuccessAsync` (line 201), not `RecordFailureAsync`. This is load-bearing: the breaker's `HalfOpen` state is only left by a successful or failed probe. A redirect probe that recorded neither outcome would strand the breaker `HalfOpen`, refusing every downstream caller — search included — until another call on that source recorded an outcome. Recording success reflects the true state: the upstream answered promptly and correctly, and the refusal is a configuration problem, not a source fault.
