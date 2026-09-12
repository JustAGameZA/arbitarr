using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Arbitarr.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-zwk: a release-lookup STORE failure must not fail the SEARCH.
///
/// <para><b>Why degrading is the right posture here, and why it needs a test.</b> The durable store
/// write was added by arb-tps so a download link survives a restart. It is a durability improvement
/// layered under a path that already worked without it: the in-memory tier still resolves the link
/// for its 30 minutes, and past that the download path already degrades a store miss to a 404. So a
/// store fault costs durability, not correctness — but before this bead an exception from it escaped
/// through <c>HandleTorznabAsync</c> and turned every search into a 500, which is an *arr seeing no
/// results at all. That is a strictly worse outcome than links that merely stop outliving a restart,
/// and this file pins the difference.</para>
///
/// <para><b>The positive control is the non-throwing case</b> (CLAUDE.md §4). A test that only
/// asserted "a throwing store still returns results" would pass just as happily against an endpoint
/// that had stopped calling the store at all — the absence of a crash proves nothing on its own. So
/// the same harness is first run with a RECORDING store and asserts the batch actually arrived,
/// establishing that this path does write; only then does the throwing variant mean anything.</para>
/// </summary>
public sealed class SearchEndpointStoreDegradationTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new("arbitarr-searchendpoint-store-degradation-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Some.Show.S01E01.1080p.WEB-DL",
        Guid = "arb-zwk-guid-1",
        PubDate = Now,
        Size = 123,
        Link = new Uri("https://indexer.example.invalid/get/arb-zwk"),
    };

    private async Task<(IResult Result, CapturingLogger Logger)> RunSearchAsync(
        ArbitarrDbContext context,
        IReleaseLookupStore store,
        CancellationToken cancellationToken = default)
    {
        var source = new SingleReleaseUpstreamSource(Candidate());
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(new[] { (IUpstreamSource)source }));
        var time = new FakeTimeProvider(Now);
        var snapshotService = new PaginationSnapshotService(
            mergeStage,
            TestCacheStage.Create(time),
            new QuerySnapshotStore(context),
            time);
        var filterStage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            time);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("indexer.example.invalid");

        var logger = new CapturingLogger();

        var result = await SearchEndpoint.HandleTorznabAsync(
            "tvsearch",
            "some show",
            Array.Empty<int>(),
            100,
            0,
            "caller-api-key",
            snapshotService,
            filterStage,
            new InMemoryReleaseLookup(),
            new RecentSearchLog(),
            NullEventSink.Instance,
            httpContext.Request,
            cancellationToken,
            releaseLookupStore: store,
            logger: logger);

        return (result, logger);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL.</b> With a store that does not throw, the batch really does reach it.
    /// Without this, the degradation test below could pass against an endpoint that had simply
    /// stopped writing — an absence of failure is not evidence of a call.
    /// </summary>
    [Fact]
    public async Task A_healthy_store_receives_the_post_filter_batch()
    {
        using var context = CreateContext();
        var store = new RecordingReleaseLookupStore();

        var (result, logger) = await RunSearchAsync(context, store);

        Assert.NotNull(result);
        // The call happened, and carried the release the search rendered.
        var recorded = Assert.Single(store.Received);
        Assert.Equal(Candidate().Guid, recorded.Candidate.Guid);
        // Nothing was degraded, so nothing was warned about.
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD.</b> A throwing store leaves the search answering normally, and the
    /// failure is recorded rather than swallowed silently.
    /// </summary>
    [Fact]
    public async Task A_throwing_store_still_lets_the_search_answer_and_logs_a_warning()
    {
        using var context = CreateContext();
        var store = new ThrowingReleaseLookupStore();

        var (result, logger) = await RunSearchAsync(context, store);

        // The search answered rather than throwing out of the endpoint.
        Assert.NotNull(result);
        // NON-VACUITY: the store really was called — the throw came from the path under test, not
        // from a store that was never reached.
        Assert.True(store.WasCalled);
        // The degradation is visible to an operator rather than silent.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Release lookup store write failed", warning.Message, StringComparison.Ordinal);
        Assert.Same(store.Thrown, warning.Exception);
    }

    /// <summary>
    /// The log line carries a COUNT and nothing else from the releases. The persistent log store is
    /// served at <c>/api/admin/logs</c> (CLAUDE.md §1), and a release payload carries the source URL
    /// — the one value that must never be written there.
    ///
    /// <para>Positive control: the link is first shown findable in the candidate the search actually
    /// rendered, so the absence assertion below cannot pass against a release that never carried
    /// one.</para>
    /// </summary>
    [Fact]
    public async Task The_warning_carries_no_release_payload_or_link()
    {
        using var context = CreateContext();
        var link = Candidate().Link!.ToString();

        // Detectability: the value really is in play on this search's release.
        Assert.Contains("indexer.example.invalid", link, StringComparison.Ordinal);

        var (_, logger) = await RunSearchAsync(context, new ThrowingReleaseLookupStore());

        var warning = Assert.Single(logger.Warnings);
        Assert.DoesNotContain(link, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("indexer.example.invalid", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Candidate().Title, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Candidate().Guid, warning.Message, StringComparison.Ordinal);
        // It still says enough to act on: how many rows were lost.
        Assert.Contains("1 releases", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CANCELLED request is not a store fault. Swallowing <see cref="OperationCanceledException"/>
    /// here would report a successful search to a caller that has gone away, and would hide a
    /// shutdown mid-write behind a warning that reads like a database problem.
    /// </summary>
    [Fact]
    public async Task A_cancellation_is_not_swallowed_as_a_store_failure()
    {
        using var context = CreateContext();
        using var cts = new CancellationTokenSource();
        var store = new CancellingReleaseLookupStore(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSearchAsync(context, store, cts.Token));

        // NON-VACUITY: the cancellation came from the store write, not from somewhere earlier.
        Assert.True(store.WasCalled);
    }

    private sealed class SingleReleaseUpstreamSource(ReleaseCandidate candidate) : IUpstreamSource
    {
        public string Name => "TestSource";

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ReleaseCandidate>>(new[] { candidate });

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceCaps(Array.Empty<int>(), false, false, null));

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    /// <summary>Records what it was handed, so the healthy path can be proven to write.</summary>
    private sealed class RecordingReleaseLookupStore : IReleaseLookupStore
    {
        public List<StoredRelease> Received { get; } = new();

        public Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default)
        {
            Received.AddRange(releases);
            return Task.CompletedTask;
        }

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredRelease?>(null);
    }

    /// <summary>Stands in for a store that cannot write — a locked or corrupt SQLite file.</summary>
    private sealed class ThrowingReleaseLookupStore : IReleaseLookupStore
    {
        public bool WasCalled { get; private set; }

        public InvalidOperationException Thrown { get; } = new("release lookup store unavailable");

        public Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw Thrown;
        }

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredRelease?>(null);
    }

    /// <summary>Cancels mid-write, the way a shutdown or a disconnected caller would.</summary>
    private sealed class CancellingReleaseLookupStore(CancellationTokenSource cts) : IReleaseLookupStore
    {
        public bool WasCalled { get; private set; }

        public Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default) =>
            Task.FromResult<StoredRelease?>(null);
    }

    /// <summary>
    /// Captures the FORMATTED message, which is what a real provider renders into the log store —
    /// a structured argument carrying a URL would leak through exactly that rendering, so asserting
    /// on the formatted text is what makes the no-payload test above meaningful.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add((formatter(state, exception), exception));
            }
        }
    }
}
