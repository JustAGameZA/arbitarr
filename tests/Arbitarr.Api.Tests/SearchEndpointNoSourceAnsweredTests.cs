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
/// arb-nus0: when NO source answered and nothing came back, the search is an INFRASTRUCTURE ERROR
/// (code 900, HTTP 5xx), not a 200 carrying an empty result set.
///
/// <para><b>The rule being applied.</b> CONTEXT.md: "An empty result set is not an error at all —
/// just a results element with no items. An <b>infrastructure error</b> means the pipeline failed to
/// produce an answer at all: code 900, HTTP 5xx." The distinction turns on whether the sources
/// ANSWERED. Six indexers agreeing there is nothing is an empty answer; six indexers that timed out
/// or refused the connection is no answer, and before this bead the two were indistinguishable on
/// the wire — an *arr recorded "0 results" for a wholly unreachable install and moved on.</para>
///
/// <para><b>Why the endpoint and not the merge stage.</b> <see cref="UpstreamMergeStage"/>'s header
/// states that its non-escalation is the STAGE's invariant: it never throws on a per-source failure
/// precisely so the classification arrives here as data, with every source's outcome in hand. These
/// tests pin the endpoint exercising that choice; the stage is deliberately untouched.</para>
///
/// <para><b>Every absence assertion here has a positive control</b> (CLAUDE.md §4): the healthy case
/// and the partial-degradation case both prove that a 200 with releases is still reachable, so the
/// 5xx assertions cannot pass against an endpoint that had started failing everything.</para>
/// </summary>
public sealed class SearchEndpointNoSourceAnsweredTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _database = new("arbitarr-searchendpoint-no-source-answered-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static ReleaseCandidate Candidate(string guid = "arb-nus0-guid-1") => new()
    {
        Title = "Some.Show.S01E01.1080p.WEB-DL",
        Guid = guid,
        PubDate = Now,
        Size = 123,
        Link = new Uri($"https://indexer.example.invalid/get/{guid}"),
    };

    /// <summary>
    /// A source whose OWN budget fired. <see cref="UpstreamMergeStage"/> classifies an
    /// <see cref="OperationCanceledException"/> as a timeout only when the caller's token is not
    /// cancelled AND the exception's token is not the caller's, so this carries a foreign token —
    /// the shape a linked <c>HttpClient.Timeout</c> produces. A plain <c>new OperationCanceledException()</c>
    /// would carry <c>CancellationToken.None</c> and be classified as a generic failure, quietly
    /// testing a different list than the test name claims.
    /// </summary>
    private static IUpstreamSource TimedOut(string name) =>
        new StubUpstreamSource(name, thrown: new OperationCanceledException(new CancellationTokenSource().Token));

    private static IUpstreamSource Failed(string name) =>
        new StubUpstreamSource(name, thrown: new HttpRequestException("connection refused"));

    private static IUpstreamSource RateLimited(string name) =>
        new StubUpstreamSource(name, thrown: new RequestLimitReachedException(name));

    private static IUpstreamSource Healthy(string name, params ReleaseCandidate[] candidates) =>
        new StubUpstreamSource(name, results: candidates);

    private async Task<(int Status, string Body, CapturingLogger Logger)> RunSearchAsync(
        ArbitarrDbContext context,
        IReadOnlyList<IUpstreamSource> sources,
        SearchProtocol protocol = SearchProtocol.Torznab)
    {
        var mergeStage = new UpstreamMergeStage(new StaticSourceRegistry(sources));
        var time = new FakeTimeProvider(Now);
        var snapshotService = new PaginationSnapshotService(
            mergeStage,
            TestCacheStage.Create(time),
            new InMemoryQuerySnapshotStore(),
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
                CancellationToken.None,
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
                CancellationToken.None,
                logger: logger);

        var (status, body) = await RenderAsync(result);
        return (status, body, logger);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL.</b> Every source healthy: 200, the release is rendered, no error element.
    /// Without this the 5xx assertions below would hold just as well against an endpoint that had
    /// started answering 500 unconditionally.
    /// </summary>
    [Fact]
    public async Task Every_source_healthy_answers_200_with_the_releases()
    {
        using var context = CreateContext();

        var (status, body, logger) = await RunSearchAsync(context, new[] { Healthy("eztv", Candidate()) });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(Candidate().Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain("<error", body, StringComparison.Ordinal);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// <b>THE POINT OF arb-nus0 (a).</b> Every source timed out and nothing came back: code 900 at
    /// HTTP 5xx, not a 200 empty set.
    ///
    /// <para><b>"Timed out" here means every population of TimedOutSources, not only the one that
    /// existed when this was written.</b> The assertion is on the LIST, so it covers a source that
    /// exceeded its OWN budget and — since arb-4cso added the whole-fan-out pass ceiling — a source
    /// that was merely slower than that ceiling. A fan-out in which every source is simply too slow
    /// therefore escalates to 900 as well. That is intended rather than incidental: the client got no
    /// answer from anyone, which is exactly CONTEXT.md's infrastructure-error shape, and a 200 would
    /// have an *arr record "0 results" for an install that was only overloaded.</para>
    /// </summary>
    [Fact]
    public async Task Every_source_timed_out_answers_the_infrastructure_error_with_a_5xx()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { TimedOut("eztv"), TimedOut("nzbgeek") });

        Assert.InRange(status, 500, 599);
        Assert.Contains(
            $"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\" description=\"{SearchEndpoint.InfrastructureErrorDescription}\" />",
            body,
            StringComparison.Ordinal);
    }

    /// <summary><b>THE POINT OF arb-nus0 (b).</b> The same for transport/protocol/parse failures.</summary>
    [Fact]
    public async Task Every_source_failed_answers_the_infrastructure_error_with_a_5xx()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { Failed("eztv"), Failed("nzbgeek") });

        Assert.InRange(status, 500, 599);
        Assert.Contains(
            $"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\"",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>A mixture of the two non-answering kinds is still no answer at all.</summary>
    [Fact]
    public async Task A_mixture_of_timed_out_and_failed_sources_answers_the_infrastructure_error()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { TimedOut("eztv"), Failed("nzbgeek") });

        Assert.InRange(status, 500, 599);
        Assert.Contains($"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\"", body, StringComparison.Ordinal);
    }

    /// <summary>The Newznab family renders the same shape in its own wrapper; the two must not diverge.</summary>
    [Fact]
    public async Task Every_source_timed_out_renders_the_newznab_error_element_too()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { TimedOut("eztv") }, SearchProtocol.Newznab);

        Assert.InRange(status, 500, 599);
        Assert.Contains(
            $"<error code=\"{SearchEndpoint.InfrastructureErrorCode}\" description=\"{SearchEndpoint.InfrastructureErrorDescription}\" />",
            body,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL AND THE SCOPE LIMIT.</b> One source timed out, another returned releases:
    /// 200 with those releases. A partial degradation must NOT escalate — an *arr is better served by
    /// the releases that exist than by a 5xx that discards them. This is what keeps the fix from
    /// becoming "any failure anywhere is an outage".
    /// </summary>
    [Fact]
    public async Task One_timed_out_source_alongside_one_that_returned_releases_answers_200_with_them()
    {
        using var context = CreateContext();

        var (status, body, logger) = await RunSearchAsync(
            context,
            new[] { TimedOut("eztv"), Healthy("nzbgeek", Candidate()) });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains(Candidate().Title, body, StringComparison.Ordinal);
        Assert.DoesNotContain("<error", body, StringComparison.Ordinal);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// <b>NEGATIVE CONTROL — THE RATE-LIMIT PATH IS UNCHANGED.</b> A fully rate-limited merge still
    /// renders code 500 at HTTP 200, exactly as it did before this bead.
    ///
    /// <para>This is the assertion that stops the new arm from being too broad. A rate limit is a
    /// protocol ANSWER: the source was reached and said "not now", which is a thing that happened
    /// rather than a failure to find out. Escalating it to 900/5xx would change a response shape
    /// that M1-9 pins and that *arr clients already understand.</para>
    /// </summary>
    [Fact]
    public async Task A_fully_rate_limited_merge_still_answers_the_rate_limit_element_at_http_200()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { RateLimited("eztv") });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains($"<error code=\"{SearchEndpoint.RateLimitErrorCode}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"code=\"{SearchEndpoint.InfrastructureErrorCode}\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ORDERING. A merge that is both fully rate-limited AND partly timed out keeps rendering the
    /// rate-limit element it rendered before this bead. Asserted explicitly because the two arms are
    /// both satisfiable here and only their ORDER decides the answer — swapping them would change a
    /// pre-existing response shape, which this bead does not do.
    /// </summary>
    [Fact]
    public async Task A_merge_that_is_both_rate_limited_and_timed_out_keeps_rendering_the_rate_limit_element()
    {
        using var context = CreateContext();

        var (status, body, _) = await RunSearchAsync(context, new[] { RateLimited("eztv"), TimedOut("nzbgeek") });

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains($"<error code=\"{SearchEndpoint.RateLimitErrorCode}\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"code=\"{SearchEndpoint.InfrastructureErrorCode}\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The operator gets a diagnosable record, at WARNING rather than Error: this is a diagnosed,
    /// expected condition with a known remediation (the sources are down), and logging every
    /// in-flight search at Error during an upstream outage would bury real faults in the store
    /// served at <c>/api/admin/logs</c>.
    ///
    /// <para>The positive control comes first: the caller key and a source name are shown to be in
    /// play on this run, so "the message does not contain them" cannot pass vacuously.</para>
    /// </summary>
    [Fact]
    public async Task The_no_source_answered_case_is_logged_at_warning_without_the_caller_key_or_source_names()
    {
        using var context = CreateContext();

        var (status, _, logger) = await RunSearchAsync(context, new[] { TimedOut("eztv"), Failed("nzbgeek") });

        // DETECTABILITY: this run really did carry a caller key and these two source names, so the
        // absence assertions below would catch an implementation that echoed them.
        Assert.InRange(status, 500, 599);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(nameof(SearchProtocol.Torznab), warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("caller-api-key", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("eztv", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nzbgeek", warning.Message, StringComparison.Ordinal);
        // No exception is attached because there is none: nothing threw, the sources simply did not
        // answer. A synthesised one would log a stack describing only its own throw site.
        Assert.Null(warning.Exception);
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

    /// <summary>One source with a fixed outcome: either a result set or a single thrown exception.</summary>
    private sealed class StubUpstreamSource(string name, IReadOnlyList<ReleaseCandidate>? results = null, Exception? thrown = null)
        : IUpstreamSource
    {
        public string Name => name;

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            if (thrown is not null)
            {
                throw thrown;
            }

            return Task.FromResult(results ?? Array.Empty<ReleaseCandidate>());
        }

        public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceCaps(Array.Empty<int>(), false, false, null));

        public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    /// <summary>A working snapshot store, so nothing on this path fails for an unrelated reason.</summary>
    private sealed class InMemoryQuerySnapshotStore : IQuerySnapshotStore
    {
        private readonly Dictionary<string, string> _payloads = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
            Task.FromResult(_payloads.TryGetValue(snapshotToken, out var payload) ? payload : null);

        public Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default)
        {
            _payloads[snapshotToken] = payloadJson;
            return Task.CompletedTask;
        }
    }

    private sealed record LogEntry(string Message, Exception? Exception);

    /// <summary>Captures Warning and Error separately, since the level is part of what is asserted.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Warnings { get; } = new();

        public List<LogEntry> Errors { get; } = new();

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
            var entry = new LogEntry(formatter(state, exception), exception);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(entry);
            }
            else if (logLevel >= LogLevel.Error)
            {
                Errors.Add(entry);
            }
        }
    }
}
