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
using Microsoft.Extensions.DependencyInjection;
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
            new SingleContextGateScopeFactory(context, clock, StartedAt),
            sink);

        return (gated, sink);
    }

    /// <summary>
    /// A real service provider over this fixture's database, wired the way Program.cs wires the
    /// gate: scoped stores, a SINGLETON scope factory over <see cref="IServiceScopeFactory"/>. Tests
    /// about concurrency need the real scoping rather than a hand-built object graph, because the
    /// scoping IS what is under test.
    /// </summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();

        services.AddDbContext<ArbitarrDbContext>(options => options.UseSqlite(_database.ConnectionString));
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        services.AddScoped(sp => new SourceApiHitCounter(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new SourceBackoffStore(
            sp.GetRequiredService<ArbitarrDbContext>(),
            sp.GetRequiredService<TimeProvider>(),
            StartedAt));
        services.AddSingleton<ISourceGateScopeFactory, SourceGateScopeFactory>();
        services.AddSingleton<IEventSink>(NullEventSink.Instance);
        services.AddScoped<BudgetedUpstreamSourceFactory>();

        return services.BuildServiceProvider();
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
    /// The skip IS evented, as its OWN kind naming the source, so the Activity surface can filter
    /// budget skips apart from real failures — an empty result with no explanation is the
    /// invisibility this bead removes.
    /// </summary>
    [Fact]
    public async Task A_skipped_search_is_evented_as_its_own_kind_naming_the_source()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(
            context,
            new FakeUpstreamSource("indexer"),
            CreateConfiguration(queryLimit: 0));

        await gated.SearchAsync(Query);

        using var reader = CreateContext();
        var skip = Assert.Single(await reader.Events.Where(e => e.Kind == EventKind.SourceSkipped).ToListAsync());

        // THE NAME IS POPULATED, unlike on a SourceFailed row written for a non-fault. It is the one
        // field that says WHICH source went quiet, and the suppression that used to depend on it
        // being null is now structural instead — see the kind assertion below.
        Assert.Equal("indexer", skip.SourceDisplayName);
    }

    /// <summary>
    /// A SKIP IS NOT A FAILURE, AND THE SEPARATION IS STRUCTURAL. Nothing writes a
    /// <see cref="EventKind.SourceFailed"/> row when a source is merely budgeted, so a skip cannot
    /// reach <c>NotificationDispatcher.Observe</c>'s SourceFailed arm — the one that arms the
    /// consecutive-failure counter and reports a source as DOWN. An earlier revision achieved this
    /// by writing a SourceFailed with a null name; asserting the ABSENCE of the failure kind is what
    /// makes the new arrangement's guarantee explicit rather than incidental.
    /// </summary>
    [Fact]
    public async Task A_skipped_search_writes_no_source_failure_row_at_all()
    {
        using var context = await CreateMigratedContextAsync();

        var (gated, _) = CreateGated(
            context,
            new FakeUpstreamSource("indexer"),
            CreateConfiguration(queryLimit: 0));

        await gated.SearchAsync(Query);

        using var reader = CreateContext();

        // The positive control: a skip WAS recorded, so the absence below is a real separation
        // rather than nothing having happened at all.
        Assert.NotEmpty(await reader.Events.Where(e => e.Kind == EventKind.SourceSkipped).ToListAsync());
        Assert.Empty(await reader.Events.Where(e => e.Kind == EventKind.SourceFailed).ToListAsync());
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
        var later = new FakeTimeProvider(Now.AddMinutes(10));
        var recovered = new BudgetedUpstreamSource(
            inner,
            CreateConfiguration(),
            new SingleContextGateScopeFactory(context, later, StartedAt),
            new RecordingEventSink(context, later));

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

        await using var provider = BuildProvider();
        var wrapped = Assert.Single(provider
            .GetRequiredService<BudgetedUpstreamSourceFactory>()
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

        await using var provider = BuildProvider();
        var wrapped = Assert.Single(provider
            .GetRequiredService<BudgetedUpstreamSourceFactory>()
            .WrapAll(new IUpstreamSource[] { inner }));

        Assert.IsType<BudgetedUpstreamSource>(wrapped);
    }

    // ---- Concurrency: the search fan-out is parallel --------------------------------------------

    /// <summary>
    /// THE REGRESSION TEST FOR THE SHARED-DbContext DEFECT. <c>UpstreamMergeStage</c> launches every
    /// source's search together under one <c>Task.WhenAll</c>, so two WRAPPED sources run their gate
    /// checks simultaneously. While the decorators held a scoped <c>ArbitarrDbContext</c> handed to
    /// them at construction, the second one threw EF's "a second operation was started on this
    /// context before a previous operation completed" — and <c>UpstreamMergeStage</c>'s catch-all
    /// turned that into an EMPTY result set, so a two-indexer deployment silently returned nothing
    /// from at least one source on every search.
    ///
    /// <para>This test FAILED before the per-operation scope landed and passes after it. It is
    /// deliberately driven through the real merge stage against a real service provider rather than
    /// through the decorator alone: the concurrency is the merge stage's, and a sequential test of
    /// two decorators cannot reproduce it.</para>
    ///
    /// <para>Both sources must ANSWER, which is the assertion that bites. An implementation that
    /// still shared a context returns one release or none, because the racing source's EF exception
    /// is swallowed by the merge stage's catch-all into an empty list.</para>
    ///
    /// <para><b>THE BARRIER IS WHAT MAKES THIS TEST REAL.</b> Each decorator awaits its own gate
    /// read before calling its inner source, so without forcing them to overlap the two fan-out
    /// tasks interleave cooperatively and a shared context is never touched twice at once — the test
    /// then passes against the very defect it exists to catch (verified: it did). The inner sources
    /// therefore rendezvous, so both gate reads are guaranteed in flight together.</para>
    /// </summary>
    [Fact]
    public async Task Two_wrapped_sources_searched_concurrently_both_answer_rather_than_racing_one_context()
    {
        using (var context = await CreateMigratedContextAsync())
        {
            context.Sources.Add(CreateConfiguration("first"));
            context.Sources.Add(CreateConfiguration("second"));
            await context.SaveChangesAsync();
        }

        await using var provider = BuildProvider();

        // Resolved from ONE request scope, exactly as the search path resolves them: this is what
        // made the old arrangement share a single context across the fan-out.
        await using var requestScope = provider.CreateAsyncScope();

        // Releases both parties only once BOTH have arrived, so the two gate reads that precede them
        // are necessarily concurrent.
        using var barrier = new Barrier(2);

        var wrapped = requestScope.ServiceProvider
            .GetRequiredService<BudgetedUpstreamSourceFactory>()
            .WrapAll(new IUpstreamSource[]
            {
                new FakeUpstreamSource("first", CreateRelease("From the first source")) { Rendezvous = barrier },
                new FakeUpstreamSource("second", CreateRelease("From the second source")) { Rendezvous = barrier },
            });

        Assert.All(wrapped, source => Assert.IsType<BudgetedUpstreamSource>(source));

        var merged = await new UpstreamMergeStage(wrapped).MergeAsync(Query);

        Assert.Equal(2, merged.Releases.Count);
        Assert.Contains(merged.Releases, r => r.Candidate.Title == "From the first source");
        Assert.Contains(merged.Releases, r => r.Candidate.Title == "From the second source");
    }

    // ---- A refusal Arbitarr itself issued is neither success nor failure ------------------------

    /// <summary>
    /// A BREAKER-OPEN REFUSAL IS NEITHER RECOVERY NOR FAULT. The wrapped source throws
    /// <see cref="SourceUnavailableException"/> when its own circuit breaker is open; an earlier
    /// revision classified that as a SUCCESS, and the success arm resets the level AND clears
    /// <c>IsPermanentlyDisabled</c> — so a source with a rejected key was silently re-enabled every
    /// time its breaker opened, which is the durable table's whole purpose defeated through a
    /// side door.
    ///
    /// <para>This is the assertion that kills both surviving mutants: classifying the exception as
    /// TransientFailure (which would escalate a source for Arbitarr's own refusal) and deleting the
    /// arm entirely. Both halves are asserted, because a mutant that cleared only one field would
    /// pass a check of the other.</para>
    /// </summary>
    [Fact]
    public async Task A_breaker_open_refusal_leaves_the_escalation_level_untouched()
    {
        using var context = await CreateMigratedContextAsync();

        var store = new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt);

        var inner = new FakeUpstreamSource("no-key")
        {
            SearchFailure = new SourceUnavailableException("The circuit breaker is open."),
        };

        var gated = new BudgetedUpstreamSource(
            inner,
            CreateConfiguration("no-key"),
            new SingleContextGateScopeFactory(context, new FakeTimeProvider(Now), StartedAt),
            new RecordingEventSink(context, new FakeTimeProvider(Now)));

        // The source is CALLABLE and carries an escalation level, so the call is genuinely made and
        // the exception under test is genuinely thrown. (A permanently disabled source is skipped by
        // the gate, so its inner source is never invoked — the flag's survival across a refusal is
        // asserted on the store directly in SourceBackoffStoreTests, which can reach that state.)
        var state = await store.RecordOutcomeAsync("no-key", SourceCallOutcome.TransientFailure);
        Assert.NotNull(state);
        Assert.Equal(1, state.DisabledLevel);

        // Past the hold-off, so the gate allows the call through.
        var later = new FakeTimeProvider(Now.AddMinutes(10));
        var callable = new BudgetedUpstreamSource(
            inner,
            CreateConfiguration("no-key"),
            new SingleContextGateScopeFactory(context, later, StartedAt),
            new RecordingEventSink(context, later));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => callable.SearchAsync(Query));

        // THE LEVEL IS UNCHANGED: not reset to zero (which the old Success mapping did, and which
        // would also have cleared a permanent disable), and not escalated to 2 (which classifying it
        // as a transient failure would do). Both mutants are killed by this one equality.
        var after = await store.GetAsync("no-key");
        Assert.NotNull(after);
        Assert.Equal(1, after.DisabledLevel);
        Assert.False(after.IsPermanentlyDisabled);
    }

    /// <summary>
    /// The same refusal does not ESCALATE a healthy source either — the other direction of the
    /// NotAttempted rule, without which the test above would be satisfied by classifying the
    /// exception as a transient failure.
    /// </summary>
    [Fact]
    public async Task A_breaker_open_refusal_does_not_escalate_a_healthy_source()
    {
        using var context = await CreateMigratedContextAsync();

        var inner = new FakeUpstreamSource("indexer")
        {
            SearchFailure = new SourceUnavailableException("The circuit breaker is open."),
        };

        var (gated, _) = CreateGated(context, inner, CreateConfiguration());

        await Assert.ThrowsAsync<SourceUnavailableException>(() => gated.SearchAsync(Query));

        var state = await new SourceBackoffStore(context, new FakeTimeProvider(Now), StartedAt)
            .GetAsync("indexer");

        // Nothing was written at all: no row, or a row still at level zero with no hold-off.
        Assert.True(state is null || (state.DisabledLevel == 0 && state.DisabledUntil is null));
    }

    /// <summary>
    /// An <see cref="ISourceGateScopeFactory"/> that hands out ONE context rather than opening a
    /// scope per operation — the shape the single-source tests above want, since they assert against
    /// that same context afterwards.
    ///
    /// <para>It is deliberately NOT what the concurrency test uses. That one goes through the real
    /// <see cref="SourceGateScopeFactory"/> over a real provider, because a fake that shares one
    /// context cannot reproduce the defect per-operation scoping exists to fix — it would pass
    /// either way, which is exactly the vacuous shape to avoid here.</para>
    /// </summary>
    private sealed class SingleContextGateScopeFactory : ISourceGateScopeFactory
    {
        private readonly SourceGate _gate;

        public SingleContextGateScopeFactory(
            ArbitarrDbContext context,
            TimeProvider timeProvider,
            DateTimeOffset startedAt)
            => _gate = new SourceGate(
                new SourceApiHitCounter(context, timeProvider),
                new SourceBackoffStore(context, timeProvider, startedAt));

        public Task<T> UseAsync<T>(
            Func<SourceGate, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
            => operation(_gate, cancellationToken);
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
                RecordedEventKind.SourceSkipped => EventKind.SourceSkipped,
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

        /// <summary>
        /// When set, the search blocks until every party has arrived — forcing two sources in one
        /// fan-out to be inside their calls simultaneously, so the gate writes that follow them
        /// genuinely overlap. Without it the fan-out interleaves cooperatively and never exercises
        /// concurrent database access at all.
        /// </summary>
        public Barrier? Rendezvous { get; set; }

        public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
            SearchQuery query,
            CancellationToken cancellationToken = default)
        {
            SearchCalls++;

            if (Rendezvous is { } barrier)
            {
                // Hold this source inside its search until its peer arrives. Both gate reads have
                // completed by now, but the SECOND gate read cannot have started before the first
                // source reached here — so the two decorators' database work is forced to overlap on
                // the next pass through the fan-out. Run off the calling thread so a synchronous
                // barrier wait cannot deadlock the awaiter.
                await Task.Run(() => barrier.SignalAndWait(TimeSpan.FromSeconds(30)), CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (SearchFailure is { } failure)
            {
                throw failure;
            }

            return _results;
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
