using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Sources;
using Microsoft.AspNetCore.Http;

namespace Arbitarr.Api.Search;

/// <summary>
/// Fetches an upstream release's download payload (torrent or NZB) back to the caller, resolving
/// the proxy guid emitted by <see cref="SearchEndpoint"/> back to its originating
/// <see cref="IUpstreamSource"/> via <see cref="IReleaseLookup"/>. The success path performs zero
/// database writes and invokes no AI logic; the one write is the activity event a refused
/// redirect records (see the catch below).
///
/// SEC-L3: the upstream body is read into memory through <see cref="MaxLengthStream"/> (bounded
/// to <see cref="MaxLengthStream.MaxBytes"/>) BEFORE any response write, so a payload that exceeds
/// the cap is rejected with a clean 502 rather than a partially-written response. An earlier
/// version of this endpoint used <c>Results.Stream</c>, which commits response headers/status
/// before the body is fully read — a <see cref="DownloadTooLargeException"/> thrown mid-stream
/// could not produce a clean pre-write 502. Buffering (bounded by the same 10 MiB cap) closes that
/// gap; the memory cost is bounded by design, not unbounded buffering.
///
/// SEC-L1/amendment: the proxy guid alone only prevents enumeration of releases — it is not an
/// authorization credential. The caller must also present the client apikey it was originally
/// issued (embedded by <see cref="SearchEndpoint"/> in the rendered download link) and it is
/// re-validated here via <see cref="IClientApiKeyResolver"/>. Missing/invalid keys receive a bare
/// 401 (no XML body — this is not a Torznab/Newznab protocol response), and the endpoint fails
/// closed if no client keys are configured at all.
///
/// #97: since the resolver became DB-backed, "the client apikey" means an environment key OR a key
/// minted in Settings > API keys, of either scope — this route is PublicRead and requires no admin
/// scope. A revoked minted key is refused here on the next request, with the same bare 401.
///
/// <para><b>THREE WAYS THIS ROUTE ANSWERS, AND THE CLIENT-KEY CHECK IS COMMON TO ALL THREE.</b>
/// Sonarr/Radarr always call with ARBITARR's client key and it is always re-validated first; only
/// the response differs. In order of the branches below:
/// <list type="number">
/// <item><b>Magnet</b> (arb-x7w8.15) — a 302 at the magnet URI, under EITHER access mode, because a
/// magnet carries no fetchable body. No indexer credential is involved.</item>
/// <item><b>Redirect access mode</b> (arb-x7w8.14) — a 302 at the indexer's own URL for a source the
/// operator opted in. That URL carries the INDEXER's API key to the caller; that is the mode's whole
/// purpose and its documented cost. OFF by default. See ADR 0023.</item>
/// <item><b>Proxy access mode</b> — the default: the payload is fetched with the indexer key and the
/// bytes are served, so the key never leaves the process (arb-x7w8.13).</item>
/// </list>
/// The mode is read from the source ROW via <c>ISourceRegistry.ResolveNzbAccessModeAsync</c>, from
/// the same resolution that produced the adapter — not from <see cref="IUpstreamSource"/>, which
/// exposes only a name, and not from a second repository read keyed on that editable name.</para>
///
/// <para><b>The redirect Arbitarr EMITS in mode 2 is the opposite of the redirect it REFUSES in the
/// <see cref="UpstreamRedirectRefusedException"/> catch below (ADR 0014).</b> Both live in this file
/// and both say "redirect"; a tidy-up that unified them would either start following upstream 3xx
/// with the indexer key attached, or stop honouring an operator's explicit setting. Neither ADR
/// supersedes the other — they are about opposite directions.</para>
/// </summary>
public static class DownloadProxyEndpoint
{
    public static async Task<IResult> HandleAsync(
        string proxyGuid,
        string? apikey,
        IClientApiKeyResolver apiKeyResolver,
        IReleaseLookup releaseLookup,
        ISourceRegistry registry,
        IEventSink eventSink,
        CancellationToken cancellationToken,
        IDownloadRefusalTracker? refusalTracker = null,
        TimeProvider? timeProvider = null)
    {
        refusalTracker ??= NullDownloadRefusalTracker.Instance;
        timeProvider ??= TimeProvider.System;

        if (await apiKeyResolver.ResolveAsync(apikey, cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.StatusCode(StatusCodes.Status401Unauthorized);
        }

        var release = await releaseLookup.FindAsync(proxyGuid, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return Results.NotFound();
        }

        // arb-x7w8.4: resolved per request. A download must be served by the source that produced the
        // release, and that source may have been added after this process started — so the lookup
        // runs against the live set rather than one fixed at startup. A source the operator has since
        // disabled correctly resolves to nothing here and answers 404: it is no longer one Arbitarr
        // is configured to send its key to.
        var sources = await registry.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var source = sources.FirstOrDefault(s => s.Name == release.SourceName);
        if (source is null)
        {
            return Results.NotFound();
        }

        // arb-x7w8.15: the magnet arm. A magnet URI names content by info hash and carries no
        // fetchable body, so there is nothing to proxy: the caller is redirected to it and Arbitarr
        // issues no upstream request at all.
        //
        // THIS IS NOT THE ADR 0014 REDIRECT REFUSAL in the catch block below, and the two must never
        // be unified. That one REFUSES a redirect an upstream sent Arbitarr, because following it
        // would fetch from an unpinned target with the indexer key attached. This one EMITS a
        // redirect downstream to the caller, for text the feed supplied as the release's own
        // identity. Opposite direction, opposite subject, opposite answer.
        //
        // Detected on the LINK's scheme, never on release.Candidate.Protocol. TorznabFeedParser
        // defaults a protocol-silent item to Usenet, so branching on the declared protocol would
        // send a magnet down the proxy path and make Arbitarr attempt an HTTP request against a
        // magnet: URI. The link is the stronger signal and decides alone.
        //
        // The branch sits AFTER source resolution deliberately: a release from a source the operator
        // has since disabled still answers 404 above, exactly as an NZB does. The 404 is about what
        // Arbitarr is configured to serve, not about whether a credential is needed — a magnet needing
        // no key is not a reason to let it bypass that gate.
        //
        // NO grab hit and NO health item are recorded here, and both follow from where this sits.
        // Nothing was fetched from the indexer, so ADR 0020's grab allowance is not consumed (the
        // counter lives inside BudgetedUpstreamSource.FetchDownloadAsync, which is never reached),
        // and the success path's own comment below gives the rule this obeys: the sticky refusal
        // clears only when a payload actually came back from this source. None did.
        //
        // The magnet reaches the client REGARDLESS of the source's NzbAccessMode, because it cannot
        // be proxied. An operator who chose Proxy mode to keep downloads server-side does not get
        // that for magnets. That is a stated property, not a defect to fix — and no key is involved:
        // a magnet carries an info hash and trackers, never an indexer credential, so unlike a
        // redirect to an indexer URL this passthrough leaks nothing confidential.
        //
        // The magnet is upstream-supplied text, so it goes in exactly ONE place: this Location
        // header. It must never reach an event summary, a health item, /api/activity or /api/status —
        // the same rule the two comments below state for a Location header and a refusal reason.
        if (release.Candidate.Link.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Redirect(release.Candidate.Link.OriginalString);
        }

        // arb-x7w8.14: the REDIRECT access mode. The operator has opted this source into answering
        // downloads with a 302 at the indexer's own URL instead of Arbitarr fetching the payload —
        // which saves Arbitarr the bandwidth and NECESSARILY hands the indexer's API key to the
        // caller, because the indexer put that key in the link when it generated the search result.
        // That trade-off is the operator's to make, it is OFF by default (the column defaults to
        // Proxy and SourceRepository only accepts the exact ordinal string "Redirect"), and the
        // Settings UI states the consequence at the point of choosing. ADR 0023 carries the full
        // security reasoning.
        //
        // THIS IS NOT THE ADR 0014 REDIRECT REFUSAL in the catch block below, and the two must never
        // be unified — they are one file apart, both say "redirect", and they mean opposite things.
        // ADR 0014 REFUSES a 3xx an upstream sent ARBITARR, because following it would fetch from an
        // unpinned target with the indexer key attached. This EMITS a 302 DOWNSTREAM to the caller,
        // at the operator's explicit instruction. Opposite direction, opposite subject, opposite
        // answer, and ADR 0023 says so in its Context for the same reason this comment does. A
        // source in Redirect mode never reaches that catch at all: nothing is fetched, so no upstream
        // 3xx can arrive to be refused.
        //
        // It sits AFTER the magnet arm above because a magnet is answered by redirect under EITHER
        // mode — it carries no fetchable body, so Proxy has nothing to proxy — and that arm needs no
        // mode lookup to decide. It sits BEFORE the adapter call below because arb-ywcj's ruling is
        // that redirect mode branches AT THE ROUTE: IUpstreamSource.FetchDownloadAsync returns
        // Task<Stream> and has no affordance for a URL, and widening it into a result type would add
        // a redirect field no proxy-mode call could ever populate. The adapter is simply never asked.
        //
        // NO LOG LINE IS WRITTEN HERE, AND THAT ABSENCE IS THE SECURITY MECHANISM — not an omission
        // to be helpfully filled in later. NZBHydra2's FileHandler logs "Redirecting to {}", i.e. it
        // logs the key; this deliberately does not. A READER CHECKING TODAY'S PIPELINE WILL FIND
        // NOTHING THAT WOULD LOG A Location HEADER and may conclude the guarding test is pointless:
        // it is not, and neither existing layer makes it so. DisableUriRedaction collapses the query
        // of an OUTBOUND IHttpClientFactory request URI, and in redirect mode there is no outbound
        // request at all. LogMessageCleanser's shared arms scrub credential-shaped values by pattern
        // wherever they appear — so they DO catch an apikey= parameter inside a logged Location, but
        // they carry no arm for the rest of that URL, and none at all for a credential in some other
        // shape. Relying on them here would be relying on the leak happening to wear a shape the
        // denylist already knows. RedirectAccessModeKeyNeverReachesLogsTests is the ratchet, and it
        // asserts on the link's PATH for exactly that reason (mutation proved the key-only form of
        // the assertion passed while the Location was being logged). It fails if anyone adds
        // app.UseHttpLogging() with ResponseHeaders, or writes an ILogger line on this arm.
        //
        // NO SUCCESSFUL-GRAB CLEAR AND NO HEALTH ITEM, for the reason the success path's own comment
        // below gives: the sticky refusal clears only when a payload actually came back from this
        // source. Arbitarr observed no payload here — emitting a 302 proves nothing about whether the
        // indexer will serve the file, which is WEAKER evidence than the truncated fetch that comment
        // already declines to count. ADR 0020's grab allowance is untouched for the same reason the
        // magnet arm leaves it alone: the counter lives inside BudgetedUpstreamSource.FetchDownloadAsync,
        // which is never reached.
        //
        // The link is upstream-supplied text carrying a credential, so it goes in exactly ONE place:
        // this Location header. It must never reach an event summary, a health item, /api/activity or
        // /api/status — the same rule the magnet comment above and the two comments below state, and
        // the reason the catch block below builds its message from the configured source name and an
        // int status code and never from upstream text such as the Location header.
        var accessMode = await registry
            .ResolveNzbAccessModeAsync(release.SourceName, cancellationToken)
            .ConfigureAwait(false);

        // Ordinal exact match, per CLAUDE.md §3 — the same posture SourceRepository.ValidateNzbAccessMode
        // takes at the write boundary. Anything that is not exactly "Redirect" proxies, so the
        // fail-closed answer is the one a malformed, empty or unrecognised value lands on.
        if (string.Equals(accessMode, SourceRepository.RedirectAccessMode, StringComparison.Ordinal))
        {
            return Results.Redirect(release.Candidate.Link.OriginalString);
        }

        try
        {
            var stream = await source.FetchDownloadAsync(release.Candidate, cancellationToken).ConfigureAwait(false);
            await using var bounded = new MaxLengthStream(stream);
            using var buffer = new MemoryStream();
            await bounded.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            // The ONE event that clears a sticky refusal health item: a payload actually came back
            // from this source. Recorded here, after the body is fully read, rather than on the
            // FetchDownloadAsync call — a fetch that starts and then trips the size cap is not a
            // successful grab, and clearing on it would hide a still-broken download path.
            //
            // arb-v3w: awaited, because clearing now also deletes the persisted row. A failed store
            // write is swallowed inside the tracker (it logs at Warning and the item stays cleared in
            // memory), so this cannot turn a successful download into an error response.
            await refusalTracker.RecordSuccessfulGrabAsync(release.SourceName, CancellationToken.None).ConfigureAwait(false);

            // arb-ywcj: derived HERE, at the route, from the protocol the endpoint already holds —
            // not by widening IUpstreamSource.FetchDownloadAsync into a result type. A result type
            // carrying content-type/filename/redirect would need a redirect field that no proxy-mode
            // call could ever populate, because the magnet and redirect decisions are both made
            // above, BEFORE the adapter is invoked.
            //
            // A .torrent payload otherwise proxies identically to an NZB: same bounded stream, same
            // buffering, same successful-grab clear. The content type is the ONLY difference, and it
            // is advisory — Sonarr/Radarr sniff the payload — so Unknown is answered with the
            // octet-stream default rather than guessed at.
            var contentType = release.Candidate.Protocol == ProtocolKind.Torrent
                ? "application/x-bittorrent"
                : "application/octet-stream";

            return Results.Bytes(buffer.ToArray(), contentType);
        }
        catch (RequestLimitReachedException)
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        catch (SourceUnavailableException)
        {
            // The source's circuit breaker is open: it is resting after earlier failures, not
            // broken for this request. 503 is the retryable answer Sonarr/Radarr already back off
            // on; the bare InvalidOperationException this used to be escaped as an unhandled 500
            // on every retry for as long as the breaker stayed open.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch (UpstreamRedirectRefusedException ex)
        {
            // A redirect is a configuration answer from a healthy upstream (NZBHydra2's "NZB
            // access type: Redirect to indexer"), and it will repeat on every download until the
            // operator changes that setting. It is recorded as an event so the fix is visible on
            // the dashboard rather than only in a swallowed exception; the source deliberately
            // does not count it against the breaker (see the exception's remarks).
            //
            // sourceDisplayName is deliberately null. A SourceFailed event that names a source
            // feeds NotificationPolicy.FoldSourceFailure, whose consecutive-failure counter would
            // announce this healthy source as down after three of Sonarr's retries and only clear
            // on an unrelated worker cycle — the same defect as the breaker one, moved to the
            // notifier. A nameless SourceFailed is skipped by that fold and still lands on the
            // Activity feed; the source is named in the summary instead.
            //
            // ex.Message is safe on the un-gated /api/activity surface only because the exception
            // constructs it from the configured source name and an int status code — never from
            // upstream-supplied text such as the Location header. Keep it that way, or route it
            // through SanitizedErrorDescription as RefreshWorker does.
            //
            // CancellationToken.None: the event describes a request that has already failed and
            // must outlive a client that disconnects mid-write; the sink rethrows a cancellation.
            await eventSink.RecordAsync(
                RecordedEventKind.SourceFailed,
                summary: $"Download refused: {release.SourceName} redirected instead of serving the file",
                reason: ex.Message,
                sourceDisplayName: null,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // arb-ln0: the event above scrolls off the Activity feed while the misconfiguration is
            // still in force, so the same refusal also raises a STICKY per-source health item on
            // /api/status. It clears only on a successful grab from this source (see the success
            // path above) — never on time, a worker cycle, or a successful search.
            //
            // The reason text obeys the SAME rule as ex.Message two comments up, and for the same
            // reason: /api/status is PublicRead and un-gated, so this string is built from the
            // CONFIGURED source name and the int status code only, never from upstream-supplied
            // text such as the Location header.
            //
            // arb-v3w: the item is now PERSISTED, so it survives a restart — the misconfiguration
            // does, and a fresh process showing a clean dashboard was the original defect. The write
            // is awaited on this path (like the event above) rather than detached, so ordering is
            // deterministic; a failed store write is swallowed inside the tracker and never changes
            // the 502 below. The reason text obeying the rule two comments up is what makes
            // persisting it safe: nothing upstream-supplied can reach the table or the public
            // endpoint that reads it.
            //
            // CancellationToken.None for the same reason the event uses it: this describes a request
            // that has already failed and must outlive a client that disconnects mid-write.
            await refusalTracker.RecordRefusalAsync(
                release.SourceName,
                $"Refused HTTP {ex.StatusCode}: the source redirected instead of serving the file.",
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (DownloadTooLargeException)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException)
        {
            // SEC-M1: covers both a genuinely failed upstream request and the origin-mismatch
            // guard the source adapters' FetchDownloadAsync throws when the resolved link's
            // scheme/host/port no longer matches the configured upstream origin at fetch time.
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }
}
