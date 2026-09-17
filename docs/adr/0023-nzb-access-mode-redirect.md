# 0023. NZB access mode Redirect: hand the indexer's key to the client, per indexer, off by default

- **Status:** Accepted
- **Date:** 2026-09-17

## Context

Arbitarr serves a download by resolving the proxy guid it issued at search time back to the source that produced the release, fetching the payload from that indexer with the indexer's own API key, and streaming the bytes to Sonarr/Radarr. That is **Proxy** mode, and it is the reason the indexer key never leaves the server (arb-x7w8.13).

Operators asked for the alternative that NZBHydra2 and Prowlarr both offer per indexer: answer the download with a `302` whose `Location` is the indexer's own download URL, so the client fetches the file itself and Arbitarr carries none of the bandwidth.

**That URL necessarily carries the indexer's API key.** Not because Arbitarr appends one — it does not, and no URL is built — but because the indexer put its key into the link when it generated the search result. NZBHydra2's `FileHandler.handleRedirect` passes the result link through unchanged for exactly this reason; Prowlarr's `Redirect` setting on Newznab/Torznab indexers does the same. Redirecting to that link therefore discloses the key to Sonarr/Radarr and to anything else that can read the response.

This is a deliberate, documented weakening of the [CLAUDE.md §1](../../CLAUDE.md#1-secrets-three-mechanisms-that-must-survive-refactoring) posture and it was owner-ratified on 2026-09-12 rather than decided by an implementer.

### The direction distinction, which is the single most confusable thing here

**[ADR 0014](0014-refuse-upstream-download-redirects.md) refuses a redirect that an upstream sends *to Arbitarr*. This ADR describes a redirect that Arbitarr *emits downstream to the caller*. They are opposite directions, opposite subjects and opposite answers, and neither supersedes the other.**

The confusion is not hypothetical, and three separate things make it likely:

- ADR 0014's own Context section describes NZBHydra2's two settings using the words **"Proxy mode"** and **"Redirect mode"** — the same two words this ADR uses for Arbitarr's own new per-source setting, in the opposite direction.
- `README.md`'s **"NZBHydra2 required setting: Proxy mode"** section tells operators to set *NZBHydra2's* NZB access type to Proxy or every download fails with 502. After this ADR, *Arbitarr* also has a per-source setting named `NzbAccessMode` with the values `Proxy` and `Redirect`. That section now says explicitly which side it is about.
- Both the refusal and the emission live in one file, `DownloadProxyEndpoint.cs`, within about a hundred lines of each other, and both say "redirect".

A future tidy-up that "unified" them would either start following upstream 3xx with the indexer key attached — the exact leak ADR 0014 exists to prevent — or stop honouring an operator's explicit per-indexer setting. The code comment on the redirect arm says so at the site, adjacent to the `UpstreamRedirectRefusedException` catch that means the opposite.

A source in Redirect mode never reaches that catch at all: nothing is fetched, so no upstream 3xx can arrive to be refused.

### Magnet links are answered by redirect under *either* mode

A magnet URI names content by info hash and carries no fetchable body, so Proxy mode has nothing to proxy: the caller is redirected to it regardless of the source's `NzbAccessMode` (arb-x7w8.15). An operator who chose Proxy to keep downloads server-side does not get that for magnets. This is a stated property rather than a defect.

It is also **not a disclosure**: a magnet carries an info hash and tracker URLs, never an indexer credential. That is the whole difference between the magnet passthrough and this ADR's redirect, and it is why the magnet arm needs no opt-in, no warning and no mode lookup. `CONTEXT.md`'s *Access mode* entry and its *Magnet passthrough* term carry the same distinction in the project vocabulary.

## Decision

**Arbitarr supports a per-source `NzbAccessMode` of `Redirect`, which answers the download route with a `302` at the release's own link, unchanged. It ships OFF: `Proxy` remains the default for new and migrated rows alike, the opt-in is per indexer, and the Settings UI states the key-exposure consequence at the point of choosing. The `Location` header, and the key inside it, must never reach a log row, an event, a health item, `/api/activity` or `/api/status`.**

Four things make that a mechanism rather than a note:

1. **`Proxy` stays the column default.** `Source.NzbAccessMode`'s initialiser and the EF `HasDefaultValue("Proxy")` are both unchanged, so an existing row and a row created without an explicit mode are both Proxy. No migration was needed: the column is already `nvarchar(16)` and only the *accepted set* widened.
2. **The value is matched by exact ordinal name**, never parsed. `SourceRepository.ValidateNzbAccessMode` checks membership of `KnownNzbAccessModes` ordinally, so `"redirect"`, `"REDIRECT"`, `" Redirect "`, `"1"`, `" 1 "` and `"+1"` are all 400s. Per [CLAUDE.md §3](../../CLAUDE.md#3-parsing-user-supplied-enums), `NzbAccessMode` must not become an enum: `Enum.TryParse` accepts the numeric form, and `"1"` is exactly how a lenient reader would mint the second member of a two-value set — here, the key-exposing one. The download route repeats the same ordinal match at the read boundary, so anything that is not exactly `"Redirect"` proxies and the failure mode is closed.
3. **The UI warning shipped in the same commit as the array entry.** `Redirect` was deliberately absent from `KnownNzbAccessModes` until it did, so that no code path could produce a key-exposing source before an operator could be told what that meant. Splitting them apart again is what the omission existed to prevent.
4. **No log line is written on the redirect arm**, and a test holds that true (below).

### The route branches before the adapter, and `FetchDownloadAsync` was not widened

`IUpstreamSource.FetchDownloadAsync` returns `Task<Stream>` and has no affordance for returning a URL. Redirect mode is therefore expressed at the route, in `DownloadProxyEndpoint.HandleAsync`, after the client-key check and after source resolution, returning a redirect result without calling the adapter at all (arb-ywcj).

Widening the adapter contract into a result type carrying content-type/filename/redirect was rejected: the magnet and redirect decisions are both made *before* the adapter is invoked, so such a type would have a redirect field that no proxy-mode call could ever populate.

The client-key authentication is **unchanged and common to every branch**. This is the same `PublicRead` download route with the same client-key re-validation; Sonarr calls it with Arbitarr's client key in both modes and only the response differs.

### How the route learns the mode

`ISourceRegistry` gained a second method, `ResolveNzbAccessModeAsync(sourceName)`, answered from the same resolution that produced the adapter. `SourceRegistry` carries the row's mode alongside each adapter in `ResolvedSource`; `BudgetedSourceRegistry` forwards the call; `StaticSourceRegistry` answers `"Proxy"` for any name it was not given an override for.

**The cost is real and is recorded here rather than hidden:** `ISourceRegistry`'s own doc asks the interface to stay narrow, because every method on it is one every decorator must forward correctly and a forwarding mistake is invisible at the call site. Three alternatives were weighed:

- **A property on `IUpstreamSource`** — rejected. That contract is deliberately minimal, and an access mode is a property of the *row*, not of the adapter.
- **A second `SourceRepository` read at the download route, keyed by display name** — rejected. arb-kfe9 records display-name keying as the wrong key: the display name is the one field an operator can edit, so a rename could make the mode and the adapter come from different rows. It would also be a second read of the table that could disagree with the first.
- **Widening `FetchDownloadAsync`** — rejected, as above.

What remains is the registry, which is already the authority pairing an adapter with the row it was built from, by row id. Answering from that same resolution is what makes it impossible for a source's mode and its adapter to come from two different reads.

### A 302 is not a successful grab

`RecordSuccessfulGrabAsync` is **not** called on the redirect arm, so a redirect does not clear a source's sticky download-refusal health item.

The success path's own rule is that the item clears when a payload actually came back from this source, recorded after the body is fully read rather than on the fetch call, because "a fetch that starts and then trips the size cap is not a successful grab". In redirect mode Arbitarr observes no payload at all — it emits a header and the caller goes to the indexer alone — so a 302 is *strictly weaker* evidence than the truncated fetch that rule already declines to count. Clearing on it would hide a genuinely broken download path behind a source the operator had merely switched modes on.

ADR 0020's grab allowance is likewise untouched, for the same reason the magnet arm leaves it alone: the counter lives inside `BudgetedUpstreamSource.FetchDownloadAsync`, which is never reached.

## Alternatives rejected

### Ship Redirect on by default, or as a global setting

Rejected on both counts. A default of Redirect would leak a key on the first download after an operator did nothing but add a source. A global setting would be the wrong home: NZBHydra2's and Prowlarr's equivalents are per indexer, and an operator may reasonably trust one indexer's exposure and not another's — which is why `NzbAccessMode` is a column on the source row rather than a `SettingsCatalog` entry. `SettingsCatalog`'s *discipline* still applies, though: an operator is never shown a bare, unexplained value, which is what the UI warning discharges.

### Strip the key from the link before redirecting

Rejected: it does not work and it would break the feature. The key is what authorises the download at the indexer; a link without it fetches nothing. There is no version of this mode that both saves the bandwidth and withholds the credential — that trade-off is the mode, and the honest answer is to state it and let the operator choose.

### Rely on `LogMessageCleanser` or the `DisableUriRedaction` collapse to protect the `Location`

Rejected, and the reason is partly directional and partly about what a denylist is. The `DisableUriRedaction` switch governs `IHttpClientFactory`'s collapse of an **outbound** request URI's query to `?*`; in redirect mode Arbitarr issues no outbound fetch at all, so that path does not execute. `LogMessageCleanser`'s shared `CredentialPatterns` arms scrub credential-shaped values **by pattern, wherever they appear** — so they *would* catch an `apikey=` parameter inside a logged `Location`. That is exactly why relying on them here is the wrong call: it makes the defence contingent on the leak wearing a shape the denylist already knows. The cleanser carries no arm for the rest of the URL, none for an arbitrary key in a URL *path*, and none for a credential in some other shape, and its own doc is explicit that it is defence in depth rather than the control. The control is that the line is never written.

`.RemoveAllLoggers()` is likewise the wrong instrument here. Its one real call site in this repository is the webhook notification client, whose token rides in a URL *path*; every other mention is a deliberate *NO-RemoveAllLoggers* comment explaining why it is not needed. A response header never passes through `IHttpClientFactory`'s logging handler in the first place. Notably there is **no** `.RemoveAllLoggers()` on the Newznab/NzbHydra source clients: in proxy mode the `/download` fetch URI carries the indexer key in its query string and relies solely on .NET's `?*` collapse. Different direction, different mechanism, and neither existing layer reaches the emitted header.

**The mechanism is that no line is written.** NZBHydra2 logs `"Redirecting to {}"` — that is, it logs the key. Arbitarr deliberately does not.

## Consequences

### The operator has chosen to disclose the indexer key, and only to the client

In Redirect mode the indexer's API key is visible to Sonarr/Radarr and to anything that can read that download response. That is the point of the mode and the operator's call. What it does **not** license is the key reaching an operator-facing surface nobody opted into — the log store, the events table behind the un-gated `/api/activity`, or the health items on the un-gated `/api/status`.

### The Location-never-logged test is a ratchet against a future change, not a check on today's code

`RedirectAccessModeKeyNeverReachesLogsTests` asserts, per mode and per log row, that neither the key nor the link reaches a log row, an event or a health item.

**A reader checking today's pipeline will find nothing that would log a `Location` header** — `UseHttpLogging`/`AddHttpLogging` is registered nowhere in this application, and the redirect arm writes no line of its own — and may conclude the test is pointless. It is not. The risk it defends against is future: someone adds `app.UseHttpLogging()` with `HttpLoggingFields.ResponseHeaders`, or writes an `ILogger` line on the redirect arm the way NZBHydra2 does. The test is what turns either into a red build.

### Mutation testing changed the test, and the finding is worth recording

The assertions were mutation-tested per [CLAUDE.md §4](../../CLAUDE.md#4-tests), in a throwaway copy outside the repository holding an implementation that logs the `Location` at Information on the redirect arm.

**The first version of the test passed against that mutation.** Searching the log rows for the *key* was satisfied by `LogMessageCleanser` alone: its `apikey=` arm scrubbed that parameter out of the logged `Location` on the way into the store, so "the key is in no log row" held true while the whole Location was being logged on every download. That is a true statement about the cleanser and says nothing whatever about the redirect arm — precisely the vacuous shape §4 describes, and the fourth time it has been caught in this repository.

The assertions now also search for the link's **path segment**, which matches no `CredentialPatterns` arm and is therefore stored verbatim if anything logs the Location. With that added, the test fails against the mutation and passes on the real implementation. The positive control asserts the marker *is* detectable in a row written through the real logger, so the absence assertions are known to be capable of finding it.

The lesson generalises: where a defence-in-depth layer happens to cover the mutation you chose, a passing test proves the layer works and not the property you meant to assert.

### Two new terms in the project vocabulary

`CONTEXT.md`'s *Access mode* entry now records that a magnet is answered by redirect under either mode, and a *Magnet passthrough* term was added beside it. Without the first, the entry's definition of Proxy — "Arbitarr fetches the file upstream and streams the bytes" — is untrue for magnets.
