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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

        Assert.IsAssignableFrom<IResult>(result);
        Assert.IsNotType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
    }

    [Fact]
    public async Task Unknown_proxy_guid_returns_not_found()
    {
        var lookup = new InMemoryReleaseLookup();
        var sources = Array.Empty<IUpstreamSource>();

        var result = await DownloadProxyEndpoint.HandleAsync("does-not-exist", ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, apikey: null, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, "wrong-key", Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None);

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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, sink, CancellationToken.None);

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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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
            release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, NullEventSink.Instance, CancellationToken.None,
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

        var result = await DownloadProxyEndpoint.HandleAsync(release.ProxyGuid, ValidApiKey, Resolver(), lookup, sources, sink, CancellationToken.None);

        var statusCodeResult = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable, statusCodeResult.StatusCode);
        // A resting breaker is routine, not an operator-actionable failure: nothing is recorded.
        Assert.Empty(sink.Events);
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
