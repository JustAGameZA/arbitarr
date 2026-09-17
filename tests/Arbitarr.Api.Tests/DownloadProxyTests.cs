using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Exercises <see cref="DownloadProxyEndpoint"/>: resolves a known proxy guid back to its
/// upstream source and streams the payload; unknown guids 404; a source's
/// <see cref="RequestLimitReachedException"/> surfaces as 429, never a 5xx; a missing/incorrect
/// apikey is rejected with a bare 401 before any lookup/streaming happens (SEC-L1 amendment).
/// </summary>
public class DownloadProxyTests
{
    private const string ValidApiKey = "secret-api-key";

    private static IClientApiKeyResolver Resolver() => new SingleKeyResolver(ValidApiKey);

    [Fact]
    public async Task Known_proxy_guid_streams_the_upstream_payload()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "torrent-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream(payload));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        Assert.IsAssignableFrom<IResult>(result);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Unknown_proxy_guid_returns_not_found()
    {
        var lookup = new InMemoryReleaseLookup();
        var sources = Array.Empty<IUpstreamSource>();

        var result = await DownloadProxyEndpoint.HandleAsync("does-not-exist", ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Source_not_found_for_recorded_release_returns_not_found()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        // No sources registered for "eztv" at all.
        var sources = Array.Empty<IUpstreamSource>();

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task RequestLimitReachedException_surfaces_as_429_not_5xx()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new RequestLimitReachedException("eztv"));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status429TooManyRequests, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Missing_apikey_returns_bare_401_before_lookup()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, apikey: null, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Wrong_apikey_returns_bare_401_before_lookup()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, "wrong-key", Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, statusCodeResult.StatusCode);
    }

    /// <summary>
    /// SEC-L3: an unbounded fake upstream stream (every read reports the whole requested buffer as
    /// filled, without allocating/copying real payload bytes) so tests can exercise multi-MiB
    /// boundary behavior cheaply.
    /// </summary>
    private sealed class FakeUnboundedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(buffer.Length);

        public override int Read(byte[] buffer, int offset, int count) => count;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A fake stream that yields exactly <paramref name="totalBytes"/> bytes then EOF.</summary>
    private sealed class FakeFixedLengthStream(long totalBytes) : Stream
    {
        private long _remaining = totalBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            _remaining -= toRead;
            return ValueTask.FromResult(toRead);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Upstream_payload_over_max_length_returns_502_with_no_body_forwarded()
    {
        // SEC-L3: 10 MiB + 1 byte exceeds MaxLengthStream.MaxBytes, so the endpoint must reject
        // with a clean 502 BEFORE any response write (Results.Bytes, not Results.Stream) — no
        // partial body should ever be forwarded to the caller.
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new FakeFixedLengthStream(MaxLengthStream.MaxBytes + 1));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status502BadGateway, statusCodeResult.StatusCode);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);
    }

    [Fact]
    public async Task Upstream_payload_exactly_at_max_length_returns_200()
    {
        // Exactly MaxBytes must NOT trip the cap (the guard is "> MaxBytes", not ">=").
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new FakeFixedLengthStream(MaxLengthStream.MaxBytes));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var bytesResult = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);
        Assert.Equal(MaxLengthStream.MaxBytes, bytesResult.FileContents.Length);
    }

    // SEC-M1: FetchDownloadAsync's fetch-time origin re-validation (or an upstream 302 redirect,
    // treated as a failure) surfaces as HttpRequestException from the source; the proxy must map
    // that to a clean 502, never an unhandled 5xx.
    [Fact]
    public async Task Source_fetch_time_origin_mismatch_returns_502()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new HttpRequestException("origin mismatch"));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status502BadGateway, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task Upstream_302_redirect_returns_502_and_records_the_setting_to_change()
    {
        // Mirrors NzbHydraSource.FetchDownloadAsync with AllowAutoRedirect=false: a 302 is refused
        // as UpstreamRedirectRefusedException, which the proxy maps to 502 rather than following
        // the redirect. It is also recorded as a SourceFailed event carrying the exception's
        // message, so the operator can see which NZBHydra2 setting to change instead of only a
        // bare 502 in Sonarr's log. The event must NOT name the source in SourceDisplayName:
        // NotificationPolicy.FoldSourceFailure counts named SourceFailed events per source and
        // would report this healthy source as down after three retries. The name goes in the
        // summary, where the fold does not look.
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new UpstreamRedirectRefusedException("eztv", 302));
        var sources = new IUpstreamSource[] { source };
        var sink = new RecordingEventSink();

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), sink, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status502BadGateway, statusCodeResult.StatusCode);

        var recorded = Assert.Single(sink.Events);
        Assert.Equal(Arbitarr.Core.Diagnostics.RecordedEventKind.SourceFailed, recorded.Kind);
        Assert.Null(recorded.SourceDisplayName);
        Assert.Contains("eztv", recorded.Summary);
        Assert.Contains("NZB access type", recorded.Reason);
    }

    [Fact]
    public async Task Upstream_302_redirect_raises_a_sticky_refusal_health_item_for_that_source()
    {
        // arb-ln0: the activity event above scrolls away while the misconfiguration is still in
        // force, so the same refusal must also land on the process-lifetime tracker the dashboard
        // reads. The reason text is built from the configured source name and the status code only
        // — /api/status is PublicRead, so no upstream-supplied text (a Location header) may reach it.
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new UpstreamRedirectRefusedException("eztv", 302));
        var sources = new IUpstreamSource[] { source };
        var tracker = new DownloadRefusalTracker();
        var at = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);

        await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, new FixedTimeProvider(at));

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("eztv", refusal.SourceName);
        Assert.Contains("302", refusal.Reason);
        Assert.Equal(at, refusal.ObservedSinceUtc);
    }

    [Fact]
    public async Task A_successful_download_clears_that_source_s_refusal_health_item()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("eztv", "refused", DateTimeOffset.UnixEpoch);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, TimeProvider.System);

        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task A_download_that_trips_the_size_cap_does_not_clear_the_refusal_health_item()
    {
        // The grab is recorded AFTER the body is fully read, not when the fetch starts. A fetch that
        // begins and then exceeds MaxLengthStream.MaxBytes is not a successful grab, and clearing on
        // it would retire the banner while the download path is still broken.
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("eztv", "refused", DateTimeOffset.UnixEpoch);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new FakeFixedLengthStream(MaxLengthStream.MaxBytes + 1));
        var sources = new IUpstreamSource[] { source };

        await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, TimeProvider.System);

        Assert.Single(tracker.Snapshot());
    }

    [Fact]
    public async Task A_successful_download_from_one_source_leaves_another_source_s_refusal_standing()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var tracker = new DownloadRefusalTracker();
        tracker.RecordRefusal("nzbhydra2", "refused", DateTimeOffset.UnixEpoch);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, TimeProvider.System);

        var refusal = Assert.Single(tracker.Snapshot());
        Assert.Equal("nzbhydra2", refusal.SourceName);
    }

    /// <summary>A TimeProvider pinned to one instant, so a recorded timestamp can be asserted exactly.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// arb-v3w: an <see cref="IDownloadRefusalStore"/> whose every call fails, so the proxy can be
    /// driven with a broken durable tier. <see cref="Calls"/> is the positive control — it proves
    /// the store was genuinely reached, without which "the response was unchanged" would pass just
    /// as happily on a tracker that never attempted a write.
    /// </summary>
    private sealed class FailingRefusalStore : IDownloadRefusalStore
    {
        public int Calls { get; private set; }

        public Task UpsertAsync(DownloadRefusal refusal, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException(new InvalidOperationException("database is locked"));
        }

        public Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException(new InvalidOperationException("database is locked"));
        }

        public Task<IReadOnlyList<DownloadRefusal>> LoadAllAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<IReadOnlyList<DownloadRefusal>>(new InvalidOperationException("database is locked"));
        }

        public Task<int> PruneUnknownSourcesAsync(
            IReadOnlyCollection<string> knownSourceNames,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<int>(new InvalidOperationException("database is locked"));
        }
    }

    private static PersistentDownloadRefusalTracker PersistingTracker(IDownloadRefusalStore store) =>
        new(new DownloadRefusalTracker(), async (operation, token) => await operation(store, token));

    /// <summary>
    /// arb-v3w: persistence is a durability concern, never a correctness one for THIS request. A
    /// store that cannot be written must not turn the refusal's 502 into a 500 — the proxy's job at
    /// this point is to report that something else is broken, and failing to record that would hide
    /// it twice over.
    /// </summary>
    [Fact]
    public async Task A_failed_refusal_store_write_does_not_change_the_proxy_response()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new UpstreamRedirectRefusedException("eztv", 302));
        var sources = new IUpstreamSource[] { source };
        var store = new FailingRefusalStore();
        var tracker = PersistingTracker(store);

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, TimeProvider.System);

        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

        // Positive control: the broken store was actually consulted, so the unchanged 502 above is
        // the swallow working rather than the write path never having run.
        Assert.Equal(1, store.Calls);

        // And the item is still tracked in memory — the pre-arb-v3w behaviour, which is the correct
        // degradation: the operator still sees the banner, it just would not survive a restart.
        Assert.Single(tracker.Snapshot());
    }

    [Fact]
    public async Task A_failed_refusal_store_delete_does_not_change_a_successful_download_response()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("torrent-bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };
        var store = new FailingRefusalStore();
        var tracker = PersistingTracker(store);

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None,
            tracker, TimeProvider.System);

        // The payload still comes back: a bookkeeping failure must never cost the caller the file it
        // successfully fetched.
        var bytes = Assert.IsAssignableFrom<IResult>(result);
        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(bytes);
        Assert.Equal(1, store.Calls);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public async Task Open_circuit_breaker_returns_503_not_an_unhandled_500()
    {
        // The source refuses to call upstream while its breaker rests. That is a retryable
        // condition Sonarr/Radarr already back off on; before it was typed it escaped the proxy as
        // an unhandled InvalidOperationException, i.e. a 500 on every retry.
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadException: new SourceUnavailableException("eztv"));
        var sources = new IUpstreamSource[] { source };
        var sink = new RecordingEventSink();

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), sink, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable, statusCodeResult.StatusCode);
        // A resting breaker is routine, not an operator-actionable failure: nothing is recorded.
        Assert.Empty(sink.Events);
    }

    // ---------------------------------------------------------------------
    // arb-x7w8.13: PER-SOURCE RESOLUTION AT N=3.
    // ---------------------------------------------------------------------

    /// <summary>
    /// THE test this bead exists for. With three indexers configured, a download must be fetched
    /// from the ONE that produced the release — and the other two must not be asked at all.
    ///
    /// <para><b>Why N=3 rather than N=2.</b> At N=2 "the right source" and "not the first source"
    /// are the same assertion for half the cases, so an implementation that picked the LAST source
    /// would pass a two-source test written against the first. Three sources with the target in the
    /// MIDDLE distinguishes first-wins, last-wins and correct-by-name, which are the three ways this
    /// resolution can be wrong.</para>
    ///
    /// <para><b>Asserting the other two were never ASKED is the security half, not a tidiness
    /// check.</b> Each <c>FetchDownloadAsync</c> sends that source's own indexer key upstream. A
    /// fan-out that fetched from all three and returned the first non-empty answer would produce the
    /// right bytes — and would have handed two indexers a request for a release they never listed,
    /// spending their rate limit and their key on it. The payload assertion alone cannot see that;
    /// the per-source call counts can.</para>
    /// </summary>
    [Fact]
    public async Task With_three_sources_the_download_is_fetched_from_the_source_that_produced_the_release()
    {
        var release = TestReleases.Torrent(sourceName: "middle-indexer", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        // Distinct payloads, so "the right bytes" cannot be satisfied by any other source's answer.
        var first = new FakeUpstreamSource("first-indexer", downloadFactory: () => new MemoryStream("first-payload"u8.ToArray()));
        var middle = new FakeUpstreamSource("middle-indexer", downloadFactory: () => new MemoryStream("middle-payload"u8.ToArray()));
        var last = new FakeUpstreamSource("last-indexer", downloadFactory: () => new MemoryStream("last-payload"u8.ToArray()));
        var sources = new IUpstreamSource[] { first, middle, last };

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var bytes = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);
        Assert.Equal("middle-payload"u8.ToArray(), bytes.FileContents);

        // Per source, not "some source was asked once": a count over the set would pass against an
        // implementation that asked one source three times.
        Assert.Empty(first.DownloadRequests);
        Assert.Empty(last.DownloadRequests);
        var asked = Assert.Single(middle.DownloadRequests);

        // And it was asked for THIS release. Without this the test would pass against an
        // implementation that fetched the right source with the wrong candidate.
        Assert.Equal(release.Candidate.Guid, asked.Guid);
    }

    /// <summary>
    /// The same N=3 set, with the release attributed to a source the operator has since removed or
    /// disabled: the answer is 404 and NO source is fetched from. A resolution that fell back to
    /// "any configured source" would send one indexer's key upstream for a release it never listed,
    /// and would serve the caller a file from a source it did not ask for.
    /// </summary>
    [Fact]
    public async Task With_three_sources_a_release_from_a_since_removed_source_fetches_from_none_of_them()
    {
        var release = TestReleases.Torrent(sourceName: "removed-indexer", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var first = new FakeUpstreamSource("first-indexer", downloadFactory: () => new MemoryStream("first-payload"u8.ToArray()));
        var middle = new FakeUpstreamSource("middle-indexer", downloadFactory: () => new MemoryStream("middle-payload"u8.ToArray()));
        var last = new FakeUpstreamSource("last-indexer", downloadFactory: () => new MemoryStream("last-payload"u8.ToArray()));
        var sources = new IUpstreamSource[] { first, middle, last };

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
        Assert.Empty(first.DownloadRequests);
        Assert.Empty(middle.DownloadRequests);
        Assert.Empty(last.DownloadRequests);
    }

    /// <summary>
    /// SEC-L1: the route FAILS CLOSED when no client keys are configured at all. The proxy guid on
    /// its own is not an authorization credential, so an install that has not yet been given a
    /// client key must refuse every download rather than serve them unauthenticated — which is what
    /// a resolver written as "no keys configured means nothing to check" would do.
    ///
    /// <para>Asserted with the source present and answering, and with its call count checked: a 401
    /// produced after the fetch would still have sent the indexer's key upstream on behalf of an
    /// unauthenticated caller.</para>
    /// </summary>
    [Fact]
    public async Task With_no_client_keys_configured_the_route_fails_closed_and_fetches_nothing()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream("bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        // No key configured at all — the resolver has nothing to match against.
        var noKeysResolver = new SingleKeyResolver(null);

        // Even the key that WOULD be valid in every other test on this class is refused, because
        // there is no configured key for it to be valid against.
        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, noKeysResolver, lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized, statusCodeResult.StatusCode);
        Assert.Empty(source.DownloadRequests);
    }

    /// <summary>
    /// Proxy mode serves BYTES and never a Location header — the property the whole bead rests on.
    /// <c>FileContentHttpResult</c> carries a byte body and has no redirect affordance at all, and
    /// the RESPONSE is additionally asserted to carry no 3xx and no <c>Location</c>, so a change to
    /// redirect mode fails here rather than passing silently. Redirect mode is arb-x7w8.14's bead,
    /// not this one's.
    ///
    /// <para>arb-j4hq: the "never a redirect" half is asserted on the response rather than by ruling
    /// out the framework's redirect TYPE. The endpoint no longer returns that type — it writes the
    /// 302 itself, because the framework's version logs its destination — and a type-based assertion
    /// would now pass against any other result that set a Location header. See
    /// <c>RedirectResponseAssertions</c>.</para>
    /// </summary>
    [Fact]
    public async Task Proxy_mode_serves_bytes_and_never_a_redirect_result()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "nzb-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream(payload));
        var sources = new IUpstreamSource[] { source };

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources), NullEventSink.Instance, CancellationToken.None);

        var bytes = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);
        Assert.Equal(payload, bytes.FileContents);
        await RedirectResponseAssertions.AssertIsNotARedirectAsync(result);
    }

    /// <summary>
    /// arb-x7w8.14 — THE SIBLING OF THE TEST ABOVE, asserting the inverse for redirect mode. The pair
    /// is the point: proxy mode must never redirect and redirect mode must never serve bytes. The
    /// bytes half is stated on the RESULT TYPE (<c>FileContentHttpResult</c> has no redirect
    /// affordance) and the redirect half on the RESPONSE the result writes — a 302 with the link in
    /// <c>Location</c> — so a mode branch wired the wrong way round fails here instead of passing
    /// silently behind a plausible-looking 200 or 302.
    ///
    /// <para>arb-j4hq: the redirect half no longer names the framework's redirect type, because the
    /// endpoint no longer returns it — it writes the 302 itself, the framework's version having
    /// logged its destination. Asserting the response is what keeps this test meaning the same thing
    /// across that change. See <c>RedirectResponseAssertions</c>.</para>
    ///
    /// <para><b>The Location is the release's own link, passed through UNCHANGED.</b> No URL is built
    /// and no key is appended, because the indexer already put its key into that link when it
    /// generated the search result — that disclosure is the whole of what this mode costs, and it is
    /// what NZBHydra2's and Prowlarr's equivalents do. Asserted against the exact original string so a
    /// later "normalisation" of the link cannot quietly change where the caller is sent.</para>
    ///
    /// <para><b>The adapter is never invoked, and that is the structural half of the claim.</b>
    /// arb-ywcj's ruling is that the mode branches AT THE ROUTE, before the adapter is called, which
    /// is why <c>IUpstreamSource.FetchDownloadAsync</c> was not widened into a result type. If a
    /// future change moved the branch inside the adapter, THIS assertion is what notices — the
    /// result-type one would still pass.</para>
    /// </summary>
    [Fact]
    public async Task Redirect_mode_answers_a_redirect_to_the_link_and_never_calls_the_adapter()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource(
            "eztv",
            downloadFactory: () => new MemoryStream("nzb-bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var registry = new StaticSourceRegistry(
            sources,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["eztv"] = "Redirect" });

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);

        await RedirectResponseAssertions.AssertRedirectsToAsync(result, release.Candidate.Link.OriginalString);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);

        // Nothing was fetched, so the indexer was never asked for the payload.
        Assert.Empty(source.DownloadRequests);
    }

    /// <summary>
    /// A mode that is not EXACTLY <c>"Redirect"</c> serves bytes. This is the route's fail-closed rule
    /// stated as a test: the match is ordinal and exact, so a mis-cased spelling, a padded one, a
    /// numeric form or an empty string all proxy rather than handing the indexer key to the caller.
    /// <c>"Proxy"</c> heads the list to pin that the default is unaffected by redirect mode existing.
    ///
    /// <para><b>None of these can be reached through <c>SourceRepository</c></b>, which rejects every
    /// one at the write boundary (<c>SourceRepositoryTests</c>). They are asserted HERE because the
    /// route must not rely on that for its own safety: a value from a row written by an older version,
    /// or by some future second writer, must still fail closed. Defence at the read boundary and
    /// defence at the write boundary are not the same guarantee, and CLAUDE.md §3's rule is about the
    /// value that selects the posture, wherever it is read.</para>
    /// </summary>
    [Theory]
    [InlineData("Proxy")]
    [InlineData("redirect")]
    [InlineData("REDIRECT")]
    [InlineData(" Redirect ")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Any_mode_that_is_not_exactly_Redirect_serves_bytes(string accessMode)
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var payload = "nzb-bytes"u8.ToArray();
        var source = new FakeUpstreamSource("eztv", downloadFactory: () => new MemoryStream(payload));
        var sources = new IUpstreamSource[] { source };

        var registry = new StaticSourceRegistry(
            sources,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["eztv"] = accessMode });

        var result = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, registry, NullEventSink.Instance, CancellationToken.None);

        var bytes = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result);
        Assert.Equal(payload, bytes.FileContents);
        await RedirectResponseAssertions.AssertIsNotARedirectAsync(result);
    }

    /// <summary>
    /// A redirect does NOT clear a source's sticky refusal health item, and a proxied payload does —
    /// the two asserted together because the contrast IS the ruling, and either half alone would let
    /// the other drift.
    ///
    /// <para><b>Why a 302 does not count as a successful grab.</b> The success path's own comment
    /// gives the rule: the item clears when a payload actually came back from this source, recorded
    /// after the body is fully read rather than on the fetch call, because "a fetch that starts and
    /// then trips the size cap is not a successful grab". Arbitarr observes no payload at all in
    /// redirect mode — it emits a header and the caller goes off to the indexer alone — so a redirect
    /// is STRICTLY WEAKER evidence than the truncated fetch that comment already declines to count.
    /// Clearing on it would hide a genuinely broken download path behind a source the operator had
    /// merely switched modes on, which is the exact defect (arb-ln0/arb-v3w) the sticky item exists to
    /// stop.</para>
    ///
    /// <para>The refusal is planted through the tracker's own API rather than by driving a refusal
    /// through the endpoint, so this test is about the clearing rule and does not also depend on the
    /// ADR 0014 catch block that sets it.</para>
    /// </summary>
    [Fact]
    public async Task A_redirect_does_not_clear_a_sticky_refusal_but_a_proxied_payload_does()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "123");
        var lookup = new InMemoryReleaseLookup();
        lookup.Record(release);

        var source = new FakeUpstreamSource(
            "eztv",
            downloadFactory: () => new MemoryStream("nzb-bytes"u8.ToArray()));
        var sources = new IUpstreamSource[] { source };

        var tracker = new Arbitarr.Core.Diagnostics.DownloadRefusalTracker();
        tracker.RecordRefusal("eztv", "Refused HTTP 302: the source redirected instead of serving the file.", DateTimeOffset.UnixEpoch);

        // The item is really there, so the assertion below is about it surviving rather than about it
        // never having existed.
        Assert.Single(tracker.Snapshot());

        var redirectRegistry = new StaticSourceRegistry(
            sources,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["eztv"] = "Redirect" });

        var redirectResult = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, redirectRegistry, NullEventSink.Instance,
            CancellationToken.None, tracker);

        await RedirectResponseAssertions.AssertRedirectsToAsync(
            redirectResult, release.Candidate.Link.OriginalString);
        Assert.Single(tracker.Snapshot());

        // THE CONTRAST: the same source, same tracker, proxy mode — a payload really does come back,
        // and that DOES clear the item. Without this half, an implementation that never cleared at all
        // would pass the assertion above.
        var proxyResult = await DownloadProxyEndpoint.HandleAsync(
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, new StaticSourceRegistry(sources),
            NullEventSink.Instance, CancellationToken.None, tracker);

        Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(proxyResult);
        Assert.Empty(tracker.Snapshot());
    }

    /// <summary>Captures every event the endpoint records, so a test can assert on kind, source and reason.</summary>
    private sealed class RecordingEventSink : Arbitarr.Core.Diagnostics.IEventSink
    {
        public List<Arbitarr.Core.Diagnostics.RecordedEvent> Events { get; } = new();

        public ValueTask RecordAsync(
            Arbitarr.Core.Diagnostics.RecordedEventKind kind,
            string summary,
            string? reason = null,
            string? sourceDisplayName = null,
            string? detail = null,
            CancellationToken cancellationToken = default,
            bool? shadowMode = null)
        {
            Events.Add(new Arbitarr.Core.Diagnostics.RecordedEvent(kind, summary, reason, sourceDisplayName, detail, shadowMode));
            return ValueTask.CompletedTask;
        }
    }
}
