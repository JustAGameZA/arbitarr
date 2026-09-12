using System.Net;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Events;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-x7w8.10: proves the budget/backoff gate as it actually runs — a decorator over a real source,
/// against a real SQLite event store, with the merge stage downstream of it. The headline property
/// is that a source at its budget is SKIPPED and the merge still answers; a test that only checked
/// the decorator in isolation would not show that.
/// </summary>
public sealed class BudgetedUpstreamSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The host start instant, set far enough before <see cref="Now"/> that
    /// <see cref="SourceBackoffPolicy.StartupGraceWindow"/> has elapsed — so no test here passes
    /// merely because escalation was suppressed.
    /// </summary>
    private static readonly DateTimeOffset StartedAt = Now - TimeSpan.FromHours(1);

    private static readonly SearchQuery Query =
        new("example", Array.Empty<int>(), Limit: 100, SearchProtocol.Newznab);

    private readonly SqliteTestDatabase _database = new("arr-searcher-budgeted-upstream-source-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    private async Task<ArbitarrDbContext> CreateMigratedContextAsync()
    {
        var context = CreateContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private static Source CreateConfiguration(
        string displayName = "indexer",
        int? queryLimit = null,
        int? grabLimit = null) => new()
        {
            Kind = "Newznab",
            DisplayName = displayName,
            BaseUrl = "http://indexer.example.invalid",
            QueryLimit = queryLimit,
            GrabLimit = grabLimit,
            LimitsUnit = "Day",
        };

    private static (BudgetedUpstreamSource Gated, RecordingEventSink Sink) CreateGated(
        ArbitarrDbContext context,
        IUpstreamSource inner,
        Source configuration)
    {
        var clock = new FakeTimeProvider(Now);
        var sink = new RecordingEventSink(context, clock);

        var gated = new BudgetedUpstreamSource(
            inner,
            configuration,
            new SourceApiHitCounter(context, clock),
            new SourceBackoffStore(context, clock, StartedAt),
            sink);

        return (gated, sink);
    }

    private static ReleaseCandidate CreateRelease(string title) => new()
    {
        Title = title,
        Guid = title,
        PubDate = Now.AddDays(-1),
        Link = new Uri("http://indexer.example.invalid/download"),
    };

    // ---- The budget skips rather than fails ----------------------------------------------------

    /// <summary>
    /// THE HEADLINE RULE, AND IT IS ASSERTED THROUGH THE MERGE. A source at its budget contributes
    /// nothing AND the merge still answers from the other source — which is what "skipped, not
    /// failed" means operationally. Asserting only that the budgeted source returned empty would not
    /// distinguish a skip from a failure that happened to be swallowed.
    /// </summary>
    [Fact]
    public async Task A_source_at_its_query_budget_is_skipped_and_the_merge_still_answers()
    {
        using var context = await CreateMigratedContextAsync();

        // Budgeted: one hit recorded against a limit of one.
        context.Events.Add(new EventEntry
        {
            Kind = EventKind.SourceQueryHit,
            OccurredAt = Now.AddMinutes(-5),
            Summary = "Queried an upstream source",
            SourceDisplayName = "budgeted",
        });
        await context.SaveChangesAsync();

        var budgetedInner = new FakeUpstreamSource("budgeted", CreateRelease("From the budgeted source"));
        var (budgeted, _) = CreateGated(context, budgetedInner, CreateConfiguration("budgeted", queryLimit: 1));

        var healthy = new FakeUpstreamSource("healthy", CreateRelease("From the healthy source"));

        var merged = await new UpstreamMergeStage(new IUpstreamSource[] { budgeted, healthy })
            .MergeAsync(Query);

        // The budgeted source was never called — the skip is a real skip, not an empty answer from
        // a call that happened.
        Assert.Equal(0, budgetedInner.SearchCalls);

        // And the merge still answered, from the source that had budget.
        var release = Assert.Single(merged.Releases);
        Assert.Equal("From the healthy source", release.Candidate.Title);

        // NOT reported as rate-limited: a budgeted source is not a faulting one, and surfacing it
        // as a rate limit would tell the client the indexer pushed back when Arbitarr chose not to
        // call it.
        Assert.Empty(merged.RateLimitedSources);
    }

    /// <summary>
    /// A skip does NOT spend budget: no hit event is written when no call is made, which is what
    /// keeps the count a record of API hits rather than of intentions.
    /// </summary>
    [Fact]
    public async Task A_skipped_search_does_not_record_a_hit()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(
            context,
            new FakeUpstreamSource("indexer"),
            CreateConfiguration(queryLimit: 0));

        await gated.SearchAsync(Query);

        using var reader = CreateContext();
        Assert.Empty(await reader.Events.Where(e => e.Kind == EventKind.SourceQueryHit).ToListAsync());
    }

    /// <summary>
    /// The skip IS evented, so the Activity surface can say why a source contributed nothing — an
    /// empty result with no explanation is the invisibility this bead removes.
    /// </summary>
    [Fact]
    public async Task A_skipped_search_is_evented_with_the_source_named_in_the_reason()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(
            context,
            new FakeUpstreamSource("indexer"),
            CreateConfiguration(queryLimit: 0));

        await gated.SearchAsync(Query);

        using var reader = CreateContext();
        var skip = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceFailed).ToListAsync());

        Assert.Contains("indexer", skip.Reason);

        // SourceDisplayName IS NULL ON PURPOSE. A populated name arms
        // NotificationPolicy.FoldSourceFailure's consecutive-failure counter, which would notify the
        // operator that a source is DOWN when it is merely budgeted — the exact conflation ADR 0020
        // forbids.
        Assert.Null(skip.SourceDisplayName);
    }

    /// <summary>
    /// NULL IS UNLIMITED, PROVED THROUGH THE DECORATOR. Half of the pair; read with the zero case
    /// below, which uses the identical fixture and differs only in the limit.
    /// </summary>
    [Fact]
    public async Task A_source_with_a_null_query_limit_is_never_skipped()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer", CreateRelease("A result"));
        var (gated, _) = CreateGated(context, inner, CreateConfiguration(queryLimit: null));

        var results = await gated.SearchAsync(Query);

        Assert.Equal(1, inner.SearchCalls);
        Assert.Single(results);
    }

    /// <summary>
    /// ZERO IS ALWAYS SKIPPED, on the same fixture as the null case above. The pair is the whole
    /// point of the nullable column: either case alone is satisfied by an implementation that got
    /// the other backwards.
    /// </summary>
    [Fact]
    public async Task A_source_with_a_zero_query_limit_is_always_skipped()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer", CreateRelease("A result"));
        var (gated, _) = CreateGated(context, inner, CreateConfiguration(queryLimit: 0));

        var results = await gated.SearchAsync(Query);

        Assert.Equal(0, inner.SearchCalls);
        Assert.Empty(results);
    }

    // ---- Hits are recorded at the outbound call, and fold --------------------------------------

    /// <summary>
    /// A search that is actually made records a hit against the calling source.
    /// </summary>
    [Fact]
    public async Task A_search_that_is_made_records_a_query_hit_naming_the_source()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(
            context,
            new FakeUpstreamSource("indexer"),
            CreateConfiguration());

        await gated.SearchAsync(Query);

        using var reader = CreateContext();
        var hit = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceQueryHit).ToListAsync());
        Assert.Equal("indexer", hit.SourceDisplayName);
    }

    /// <summary>
    /// REPEATED SEARCHES FOLD ONTO ONE ROW AND THE BUDGET STILL COUNTS THEM ALL. This is the
    /// decorator-level statement of the RepeatCount rule: three real searches, one row, and a source
    /// limited to three is then out of budget. A row-counting budget would let a fourth through.
    /// </summary>
    [Fact]
    public async Task Three_searches_fold_to_one_row_and_exhaust_a_limit_of_three()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer", CreateRelease("A result"));
        var (gated, _) = CreateGated(context, inner, CreateConfiguration(queryLimit: 3));

        for (var i = 0; i < 3; i++)
        {
            await gated.SearchAsync(Query);
        }

        Assert.Equal(3, inner.SearchCalls);

        // The positive control: these DID fold, so the count that follows cannot be coming from a
        // row count.
        using (var reader = CreateContext())
        {
            var row = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceQueryHit).ToListAsync());
            Assert.Equal(3, row.RepeatCount);
        }

        // The fourth is refused, which only happens if the folded row counted as three.
        await gated.SearchAsync(Query);
        Assert.Equal(3, inner.SearchCalls);
    }

    /// <summary>
    /// Caps are deliberately UNGATED: a caps fetch is configuration discovery, not a search, and the
    /// moment an operator most needs to see what a source supports is when it is backing off.
    /// </summary>
    [Fact]
    public async Task A_caps_fetch_is_not_gated_by_the_query_budget()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer");
        var (gated, _) = CreateGated(context, inner, CreateConfiguration(queryLimit: 0));

        await gated.GetCapsAsync(SearchProtocol.Newznab);

        Assert.Equal(1, inner.CapsCalls);
    }

    // ---- Grabs ----------------------------------------------------------------------------------

    /// <summary>
    /// A grab at its budget throws rather than returning an empty stream. Unlike a search, a
    /// download has exactly one source and no other way to succeed — an empty stream would hand the
    /// client a zero-byte NZB that looks valid.
    /// </summary>
    [Fact]
    public async Task A_grab_at_its_budget_is_refused_rather_than_returning_an_empty_stream()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer");
        var (gated, _) = CreateGated(context, inner, CreateConfiguration(grabLimit: 0));

        await Assert.ThrowsAsync<SourceUnavailableException>(
            () => gated.FetchDownloadAsync(CreateRelease("A release")));

        Assert.Equal(0, inner.FetchCalls);
    }

    /// <summary>A grab that is made records a grab hit — the call evented nowhere before this bead.</summary>
    [Fact]
    public async Task A_grab_that_is_made_records_a_grab_hit()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(context, new FakeUpstreamSource("indexer"), CreateConfiguration());

        using var stream = await gated.FetchDownloadAsync(CreateRelease("A release"));

        using var reader = CreateContext();
        var hit = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceGrabHit).ToListAsync());
        Assert.Equal("indexer", hit.SourceDisplayName);
    }

    /// <summary>
    /// The grab budget is separate from the query budget: exhausting queries must not stop downloads
    /// of results already served.
    /// </summary>
    [Fact]
    public async Task An_exhausted_query_budget_does_not_block_a_grab()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer");
        var (gated, _) = CreateGated(
            context,
            inner,
            CreateConfiguration(queryLimit: 0, grabLimit: null));

        using var stream = await gated.FetchDownloadAsync(CreateRelease("A release"));

        Assert.Equal(1, inner.FetchCalls);
    }

    // ---- Backoff -------------------------------------------------------------------------------

    /// <summary>
    /// A transient failure escalates, and the source is then held off — the decorator's outcome
    /// classification reaching the durable store.
    /// </summary>
    [Fact]
    public async Task A_transient_failure_escalates_and_the_next_search_is_skipped()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer") { SearchFailure = new HttpRequestException("Timed out") };
        var (gated, _) = CreateGated(context, inner, CreateConfiguration());

        await Assert.ThrowsAsync<HttpRequestException>(() => gated.SearchAsync(Query));

        var state = await new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt)
            .GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(1, state.DisabledLevel);

        // Held off: the next search does not reach the source.
        await gated.SearchAsync(Query);
        Assert.Equal(1, inner.SearchCalls);
    }

    /// <summary>
    /// A 401 disables permanently rather than escalating. Asserted on both fields, because an
    /// implementation that set the flag AND escalated would pass a check of either alone.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_authentication_failure_disables_the_source_permanently(HttpStatusCode status)
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer")
        {
            SearchFailure = new HttpRequestException("Rejected", inner: null, statusCode: status),
        };

        var (gated, _) = CreateGated(context, inner, CreateConfiguration());

        await Assert.ThrowsAsync<HttpRequestException>(() => gated.SearchAsync(Query));

        var state = await new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt)
            .GetAsync("indexer");

        Assert.NotNull(state);
        Assert.True(state.IsPermanentlyDisabled);
        Assert.Equal(0, state.DisabledLevel);
    }

    /// <summary>A successful search after a failure resets the level to zero.</summary>
    [Fact]
    public async Task A_successful_search_resets_the_escalation_level_to_zero()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer") { SearchFailure = new HttpRequestException("Timed out") };
        var (gated, _) = CreateGated(context, inner, CreateConfiguration());

        await Assert.ThrowsAsync<HttpRequestException>(() => gated.SearchAsync(Query));

        // Recovered, and past the hold-off.
        inner.SearchFailure = null;
        var recovered = new BudgetedUpstreamSource(
            inner,
            CreateConfiguration(),
            new SourceApiHitCounter(context, new FakeTimeProvider(Now.AddMinutes(10))),
            new SourceBackoffStore(context, new FakeTimeProvider(Now.AddMinutes(10)), StartedAt),
            new RecordingEventSink(context, new FakeTimeProvider(Now.AddMinutes(10))));

        await recovered.SearchAsync(Query);

        var state = await new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt)
            .GetAsync("indexer");

        Assert.NotNull(state);
        Assert.Equal(0, state.DisabledLevel);
        Assert.Null(state.DisabledUntil);
    }

    /// <summary>
    /// A permanently disabled source is skipped and the merge still answers — the permanent case of
    /// the headline rule, asserted separately because it takes a different branch.
    /// </summary>
    [Fact]
    public async Task A_permanently_disabled_source_is_skipped_and_the_merge_still_answers()
    {
        using var context = await CreateMigratedContextAsync();

        await new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt)
            .RecordOutcomeAsync("no-key", SourceCallOutcome.AuthenticationFailure);

        var disabledInner = new FakeUpstreamSource("no-key", CreateRelease("Never returned"));
        var (disabled, _) = CreateGated(context, disabledInner, CreateConfiguration("no-key"));

        var healthy = new FakeUpstreamSource("healthy", CreateRelease("From the healthy source"));

        var merged = await new UpstreamMergeStage(new IUpstreamSource[] { disabled, healthy })
            .MergeAsync(Query);

        Assert.Equal(0, disabledInner.SearchCalls);
        Assert.Equal("From the healthy source", Assert.Single(merged.Releases).Candidate.Title);
    }

    /// <summary>
    /// A source with NO configuration row is passed through unwrapped by the factory, rather than
    /// blocked — a missing row is not an outage.
    /// </summary>
    [Fact]
    public async Task The_factory_passes_through_a_source_that_has_no_configuration_row()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("unconfigured");

        var wrapped = Assert.Single(new BudgetedUpstreamSourceFactory(
                context,
                new SourceApiHitCounter(context, new FakeTimeProvider(Now)),
                new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt),
                new RecordingEventSink(context, new FakeTimeProvider(Now)))
            .WrapAll(new IUpstreamSource[] { inner }));

        Assert.Same(inner, wrapped);
    }

    /// <summary>
    /// A source WITH a configuration row is wrapped — the other half of the factory's rule, without
    /// which the pass-through case above would be satisfied by a factory that wrapped nothing.
    /// </summary>
    [Fact]
    public async Task The_factory_wraps_a_source_that_has_a_configuration_row()
    {
        using var context = await CreateMigratedContextAsync();
        context.Sources.Add(CreateConfiguration("indexer"));
        await context.SaveChangesAsync();

        var inner = new FakeUpstreamSource("indexer");

        var wrapped = Assert.Single(new BudgetedUpstreamSourceFactory(
                context,
                new SourceApiHitCounter(context, new FakeTimeProvider(Now)),
                new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt),
                new RecordingEventSink(context, new FakeTimeProvider(Now)))
            .WrapAll(new IUpstreamSource[] { inner }));

        Assert.IsType<BudgetedUpstreamSource>(wrapped);
    }

    /// <summary>
    /// An <see cref="IEventSink"/> writing through the REAL <see cref="EventRepository"/>, so the
    /// folding these tests depend on is production folding rather than a double's imitation of it.
    /// </summary>
    private sealed class RecordingEventSink : IEventSink
    {
        private readonly EventRepository _repository;

        public RecordingEventSink(ArbitarrDbContext context, TimeProvider timeProvider)
            => _repository = new EventRepository(context, timeProvider);

        public async ValueTask RecordAsync(
            RecordedEventKind kind,
            string summary,
            string? reason = null,
            string? sourceDisplayName = null,
            string? detail = null,
            CancellationToken cancellationToken = default,
            bool? shadowMode = null)
        {
            var entityKind = kind switch
            {
                RecordedEventKind.SourceQueryHit => EventKind.SourceQueryHit,
                RecordedEventKind.SourceGrabHit => EventKind.SourceGrabHit,
                RecordedEventKind.SourceFailed => EventKind.SourceFailed,
                _ => EventKind.WorkerCycle,
            };

            await _repository.AddAsync(
                entityKind, summary, reason, sourceDisplayName, detail, cancellationToken, shadowMode);
        }
    }

    private sealed class FakeUpstreamSource : IUpstreamSource
    {
        private readonly ReleaseCandidate[] _results;

        public FakeUpstreamSource(string name, params ReleaseCandidate[] results)
        {
            Name = name;
            _results = results;
        }

        public string Name { get; }

        public int SearchCalls { get; private set; }

        public int CapsCalls { get; private set; }

        public int FetchCalls { get; private set; }

        public Exception? SearchFailure { get; set; }

        public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;

            return SearchFailure is { } failure
                ? Task.FromException<IReadOnlyList<ReleaseCandidate>>(failure)
                : Task.FromResult<IReadOnlyList<ReleaseCandidate>>(_results);
        }

        public Task<SourceCaps> GetCapsAsync(
            SearchProtocol protocol,
            CancellationToken cancellationToken = default)
        {
            CapsCalls++;
            return Task.FromResult(new SourceCaps(
                Array.Empty<int>(),
                SupportsTvSearch: true,
                SupportsMovieSearch: true,
                MaxPageSize: 100));
        }

        public Task<Stream> FetchDownloadAsync(
            ReleaseCandidate release,
            CancellationToken cancellationToken = default)
        {
            FetchCalls++;
            return Task.FromResult<Stream>(new MemoryStream(new byte[] { 1, 2, 3 }));
        }
    }
}
