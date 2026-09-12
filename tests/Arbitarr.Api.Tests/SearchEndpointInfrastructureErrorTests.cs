using Arbitarr.Api.Search;
using Arbitarr.Core.Caching;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Arbitarr.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-bgg9: an INFRASTRUCTURE exception escaping the search pipeline must be answered with the
/// protocol's own <c>&lt;error&gt;</c> element and a 5xx, not with a bare 500 carrying no body.
///
/// <para><b>What this pins, and why it is a production defect rather than a test concern.</b> Before
/// this bead there was no exception guard anywhere between the torznab handler and the client, and
/// the host registers no exception-handling middleware (deliberately — CLAUDE.md §2's 400-vs-503
/// route-existence leak means a global handler needs its own analysis). So any escaping exception
/// became a bare 500 with no XML at all, and a real Sonarr reads that as an unreachable indexer
/// rather than as a failed search. arb-qjvg observed exactly that shape once and could not diagnose
/// it, because nothing had been captured.</para>
///
/// <para><b>The probe is <see cref="IQuerySnapshotStore.GetAsync"/> specifically.</b> That is one of
/// the unguarded SQLite reads arb-qjvg's investigation NAMED (PaginationSnapshotService ~:133) as a
/// realistic origin for a transient "database is locked" under test-host contention, so this drives
/// the mechanism the bead is actually about rather than a convenient stand-in.</para>
///
/// <para><b>Every absence assertion here has a positive control</b> (CLAUDE.md §4). The exception
/// message is a distinctive sentinel which is first shown to be findable, so
/// "the body does not contain it" cannot pass against a message that was never in play; and the
/// no-conversion case asserts the probe was actually reached rather than inferring it from an
/// absence of failure.</para>
/// </summary>
public sealed class SearchEndpointInfrastructureErrorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Deliberately distinctive and deliberately shaped like the things that must never be
    /// published: a config-directory path and a host, which is what a real SqliteException or
    /// HttpRequestException message carries. A generic "boom" would let the leak assertion pass
    /// while proving nothing about the values anyone actually cares about.
    /// </summary>
    private const string SentinelMessage =
        "arb-bgg9-sentinel: database is locked at /config/arbitarr.db via db.example.invalid:5076";

    private readonly SqliteTestDatabase _database = new("arbitarr-searchendpoint-infra-error-test");

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
        Guid = "arb-bgg9-guid-1",
        PubDate = Now,
        Size = 123,
        Link = new Uri("https://indexer.example.invalid/get/arb-bgg9"),
    };

    /// <summary>
    /// The logger is supplied by the CALLER rather than created here, so a test whose search THROWS
    /// can still inspect what was logged on the way out — a return value is unavailable on that
    /// path, and the cancellation tests are entirely about what did NOT get logged.
    /// </summary>
    private async Task<(IResult Result, CapturingLogger Logger)> RunSearchAsync(
        ArbitarrDbContext context,
        IQuerySnapshotStore snapshotStore,
        SearchProtocol protocol = SearchProtocol.Torznab,
        CancellationToken cancellationToken = default,
        CapturingLogger? logger = null)
    {
        var source = new SingleReleaseUpstreamSource(Candidate());
        var mergeStage = new UpstreamMergeStage(new[] { (IUpstreamSource)source });
        var time = new FakeTimeProvider(Now);
        var snapshotService = new PaginationSnapshotService(
            mergeStage,
            TestCacheStage.Create(time),
            snapshotStore,
            time);
        var filterStage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            time);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("indexer.example.invalid");

        logger ??= new CapturingLogger();

        var result = protocol == SearchProtocol.Torznab
            ? await SearchEndpoint.HandleTorznabAsync(
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
                logger: logger)
            : await SearchEndpoint.HandleNewznabAsync(
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
                logger: logger);

        return (result, logger);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL.</b> With a snapshot store that does not throw, the search answers
    /// normally and nothing is logged at Error. Without this, every assertion below could hold
    /// against an endpoint that had started answering 500 unconditionally.
    /// </summary>
    [Fact]
    public async Task A_healthy_snapshot_store_answers_normally_and_logs_no_error()
    {
        using var context = CreateContext();
        var store = new InMemoryQuerySnapshotStore();

        var (result, logger) = await RunSearchAsync(context, store);

        var (status, body) = await RenderAsync(result);

        Assert.Equal(StatusCodes.Status200OK, status);
        // The release the search found really was rendered, so this is a genuine success and not an
        // empty-but-200 answer that would make the comparison below meaningless.
        Assert.Contains(Candidate().Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain("<error", body, StringComparison.Ordinal);
        // NON-VACUITY: the probe really is on this path, so a throw from it below is reached.
        Assert.True(store.WasRead);
        Assert.Empty(logger.Errors);
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD (a).</b> A throwing snapshot store is answered with the Torznab
    /// error element carrying the documented code, rather than escaping the handler.
    /// </summary>
    [Fact]
    public async Task A_throwing_snapshot_store_renders_the_torznab_error_element()
    {
        using var context = CreateContext();
        var store = new ThrowingQuerySnapshotStore(new InvalidOperationException(SentinelMessage));

        var (result, _) = await RunSearchAsync(context, store);

        var (_, body) = await RenderAsync(result);

        // NON-VACUITY: the throw came from the path under test.
        Assert.True(store.WasRead);
        Assert.Contains(
            $"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\" description=\"{SearchEndpoint.InfrastructureErrorDescription}\" />",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The Newznab family renders the same shape in its own wrapper — the two protocols must not
    /// diverge on the error path, exactly as <c>ErrorXmlGoldenTests</c> pins for the other two codes.
    /// </summary>
    [Fact]
    public async Task A_throwing_snapshot_store_renders_the_newznab_error_element()
    {
        using var context = CreateContext();
        var store = new ThrowingQuerySnapshotStore(new InvalidOperationException(SentinelMessage));

        var (result, _) = await RunSearchAsync(context, store, SearchProtocol.Newznab);

        var (status, body) = await RenderAsync(result);

        Assert.True(store.WasRead);
        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Contains(
            $"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\" description=\"{SearchEndpoint.InfrastructureErrorDescription}\" />",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD (b).</b> The status is 5xx. This is the half an *arr can act on
    /// without parsing the body: a 200 would have it record "0 results" as a legitimate outcome and
    /// silently miss releases, which is a worse failure than a visible error.
    /// </summary>
    [Fact]
    public async Task A_throwing_snapshot_store_answers_with_a_5xx_status()
    {
        using var context = CreateContext();
        var store = new ThrowingQuerySnapshotStore(new InvalidOperationException(SentinelMessage));

        var (result, _) = await RunSearchAsync(context, store);

        var (status, _) = await RenderAsync(result);

        Assert.True(store.WasRead);
        Assert.InRange(status, 500, 599);
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD (c) — THE SECRETS ASSERTION.</b> The exception message never reaches
    /// the response body.
    ///
    /// <para>The positive control is explicit and comes FIRST: the sentinel is shown to be present in
    /// the thrown exception's own message, and its two dangerous fragments are named individually.
    /// Without that, <c>DoesNotContain</c> would pass just as happily against a message that was
    /// never in play at all (CLAUDE.md §4) — an empty set contains nothing.</para>
    /// </summary>
    [Fact]
    public async Task The_response_body_never_carries_the_exception_message()
    {
        using var context = CreateContext();
        var thrown = new InvalidOperationException(SentinelMessage);
        var store = new ThrowingQuerySnapshotStore(thrown);

        // DETECTABILITY, asserted before the absence: these values really are in play on this run,
        // so if the handler DID echo the message, the assertions below would catch it.
        Assert.Contains("/config/arbitarr.db", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("db.example.invalid", thrown.Message, StringComparison.Ordinal);

        var (result, logger) = await RunSearchAsync(context, store);

        var (_, body) = await RenderAsync(result);

        Assert.True(store.WasRead);
        Assert.DoesNotContain(SentinelMessage, body, StringComparison.Ordinal);
        // Named individually rather than only as the whole string: a partial echo of the path or the
        // host is the same leak, and asserting only the full message would miss it.
        Assert.DoesNotContain("/config/arbitarr.db", body, StringComparison.Ordinal);
        Assert.DoesNotContain("db.example.invalid", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);

        // The counterpart that makes the absence above a REDACTION rather than a loss: the operator
        // still gets the full detail, through the admin-gated log.
        var error = Assert.Single(logger.Errors);
        Assert.Same(thrown, error.Exception);
        Assert.Contains(SentinelMessage, error.Exception!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>THE POINT OF THE BEAD (d).</b> The exception is recorded at Error, which is what puts it in
    /// the store served at <c>/api/admin/logs</c> and makes the next arb-qjvg occurrence diagnosable.
    /// Error specifically, not Warning: this is a failed request, not a degraded one.
    /// </summary>
    [Fact]
    public async Task The_escaping_exception_is_logged_at_error_with_the_exception_attached()
    {
        using var context = CreateContext();
        var thrown = new InvalidOperationException(SentinelMessage);
        var store = new ThrowingQuerySnapshotStore(thrown);

        var (_, logger) = await RunSearchAsync(context, store);

        Assert.True(store.WasRead);
        var error = Assert.Single(logger.Errors);
        // The exception OBJECT is attached, not merely mentioned: that is what carries the stack into
        // the log store's Exception column, and the stack is the whole diagnostic value.
        Assert.Same(thrown, error.Exception);
        // The formatted message carries no payload of its own beyond the protocol name.
        Assert.DoesNotContain(SentinelMessage, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-api-key", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>NEGATIVE CONTROL.</b> An <see cref="OperationCanceledException"/> is NOT converted — it
    /// propagates.
    ///
    /// <para>This is the assertion that stops the guard from being too broad. A cancellation means
    /// the caller went away or the host is shutting down; converting it would write a response
    /// nobody is reading and would log a shutdown at Error on every in-flight search, burying real
    /// faults under restart noise in the very store this bead exists to make useful.</para>
    /// </summary>
    [Fact]
    public async Task A_cancellation_is_not_converted_into_an_error_element()
    {
        using var context = CreateContext();
        using var cts = new CancellationTokenSource();
        var store = new CancellingQuerySnapshotStore(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSearchAsync(context, store, SearchProtocol.Torznab, cts.Token));

        // NON-VACUITY: the cancellation came from the probe, not from somewhere earlier that never
        // reached the guard at all — without this the test would pass against a handler that threw
        // before the try block.
        Assert.True(store.WasRead);
    }

    /// <summary>
    /// The same negative control on the logging side: a cancellation must not be RECORDED as a
    /// fault either. Asserted separately because a guard could correctly rethrow while still having
    /// logged on the way past, which would produce exactly the restart noise described above.
    /// </summary>
    [Fact]
    public async Task A_cancellation_is_not_logged_as_an_error()
    {
        using var context = CreateContext();
        using var cts = new CancellationTokenSource();
        var store = new CancellingQuerySnapshotStore(cts);
        var logger = new CapturingLogger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunSearchAsync(context, store, SearchProtocol.Torznab, cts.Token, logger));

        Assert.True(store.WasRead);
        Assert.Empty(logger.Errors);
    }

    private static async Task<(int Status, string Body)> RenderAsync(IResult result)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        using var body = new MemoryStream();
        context.Response.Body = body;

        await result.ExecuteAsync(context);

        body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
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

    /// <summary>A working store, so the healthy path can be proven to read it.</summary>
    private sealed class InMemoryQuerySnapshotStore : IQuerySnapshotStore
    {
        private readonly Dictionary<string, string> _payloads = new(StringComparer.Ordinal);

        public bool WasRead { get; private set; }

        public Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            return Task.FromResult(_payloads.TryGetValue(snapshotToken, out var payload) ? payload : null);
        }

        public Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            _payloads[snapshotToken] = payloadJson;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stands in for the unguarded SQLite read arb-qjvg named: a locked or unreadable database file
    /// at the pagination-snapshot lookup.
    /// </summary>
    private sealed class ThrowingQuerySnapshotStore(Exception thrown) : IQuerySnapshotStore
    {
        public bool WasRead { get; private set; }

        public Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            throw thrown;
        }

        public Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Cancels at the same point, the way a disconnected caller or a shutdown would.</summary>
    private sealed class CancellingQuerySnapshotStore(CancellationTokenSource cts) : IQuerySnapshotStore
    {
        public bool WasRead { get; private set; }

        public Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default)
        {
            WasRead = true;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }

        public Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Captures the FORMATTED message alongside the exception object. The formatted text is what a
    /// real provider renders into the log store, so a structured argument carrying a secret would
    /// leak through exactly that rendering — asserting on it is what makes the no-payload checks
    /// above mean anything.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Errors { get; } = new();

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
            if (logLevel == LogLevel.Error)
            {
                Errors.Add((formatter(state, exception), exception));
            }
        }
    }
}
