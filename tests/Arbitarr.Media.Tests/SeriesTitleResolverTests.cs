using System.Diagnostics;
using System.Net;
using System.Text;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Media;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Media;
using Arbitarr.Media.Providers;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// arb-u1c: <see cref="SeriesTitleResolver"/> turns the tvdbid on Sonarr's anime search into the
/// series title the upstream query needs, or admits nothing.
///
/// <para>
/// The cases that matter here are the ones where it must admit NOTHING, because each is a way a
/// plausible implementation sends a confidently wrong query upstream — chiefly echoing back the bare
/// episode number it was given as if it were a title, in any spelling of that number.
/// </para>
///
/// <para>
/// arb-5uw: ADR 0002's AnimeLists fallback tier IS exercised here now that it is wired as a REQUIRED
/// dependency and reachable from the real container (<c>IdentityResolverWiringTests</c> is what pins
/// that half; these facts are about behaviour). They cover what the tier may admit — several
/// distinct names admit nothing and flag the ambiguity, exactly one name resolves — and the two
/// ways it must cost nothing: an *arr answer means it is never consulted, and an unconfigured
/// source URL means it performs no I/O at all.
/// </para>
///
/// <para>
/// Both "cost nothing" facts assert that a request-COUNTING handler saw zero requests, which is
/// vacuous on its own: a handler wired to nothing also sees zero. Each is therefore preceded by a
/// positive control proving the same handler DOES record a request when the tier really runs — see
/// <see cref="Positive_control_the_anime_lists_handler_records_a_request_when_the_tier_actually_runs"/>.
/// </para>
/// </summary>
public sealed class SeriesTitleResolverTests : IDisposable
{
    private const int TvdbId = 81797;
    private const string BaseUrl = "http://192.0.2.21:8989/";
    private const string ApiKey = "placeholder-sonarr-key-0123456789";

    private readonly SqliteTestDatabase _database = new("arbitarr-resolver");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    /// <summary>One episode row as Sonarr's <c>/api/v3/episode</c> renders it, series object included.</summary>
    private static string EpisodeFeed(string? seriesTitle) =>
        seriesTitle is null
            ? """[{"seasonNumber":21,"episodeNumber":4,"absoluteEpisodeNumber":92}]"""
            : "[{\"seasonNumber\":21,\"episodeNumber\":4,\"absoluteEpisodeNumber\":92,\"series\":{\"title\":"
                + System.Text.Json.JsonSerializer.Serialize(seriesTitle)
                + "}}]";

    private static IMemoryCache NewMemo() => new MemoryCache(new MemoryCacheOptions());

    private static SeriesTitleResolver Build(
        ArbitarrDbContext context,
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        IMemoryCache? memo = null) =>
        BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }),
            memo);

    private static SeriesTitleResolver BuildWithHandler(
        ArbitarrDbContext context,
        HttpMessageHandler handler,
        IMemoryCache? memo = null,
        TimeSpan? lookupBudget = null,
        IArrInstanceEpoch? epoch = null,
        AnimeListsProvider? animeLists = null) =>
        new(
            new SonarrCredentialProvider(new ArrInstanceRepository(context)),
            new StubHttpClientFactory(new HttpClient(handler)),
            new StubCircuitBreaker(),
            memo ?? NewMemo(),
            epoch ?? new ArrInstanceEpoch(),
            animeLists ?? UnconfiguredAnimeLists(),
            lookupBudget);

    private static IdentityResolutionHints Hints(int? tvdbId = TvdbId) => new(tvdbId, TmdbId: null, Year: null);

    // ---- arb-5uw: ADR 0002's AnimeLists fallback tier ----

    /// <summary>
    /// The example URL every test here configures. A documentation-only <c>.example</c> host, never a
    /// real one: the repository names no anime-lists upstream anywhere, because choosing one is the
    /// operator's decision (see <see cref="AnimeListsProviderOptions.SourceUrl"/>).
    /// </summary>
    private const string AnimeListsSourceUrl = "https://anime-lists.example/anime-list-full.xml";

    /// <summary>An anime-lists document mapping <see cref="TvdbId"/> to the given names.</summary>
    private static string AnimeListsXml(params string[] names) =>
        "<anime-list><anime anidbid=\"69\" tvdbid=\"" + TvdbId + "\">"
        + string.Concat(names.Select(n => "<name>" + System.Security.SecurityElement.Escape(n) + "</name>"))
        + "</anime></anime-list>";

    /// <summary>
    /// The STOCK state: registered but with no source URL, so the provider reports NotConfigured and
    /// performs no I/O. Used by every test that is not about the tier itself, which is why the tier
    /// being wired changes none of their expectations.
    /// </summary>
    private static AnimeListsProvider UnconfiguredAnimeLists() =>
        new(
            new AnimeListsProviderOptions(SourceUrl: null, ConfigDirectory: CreateTempConfigDirectory()),
            new HttpClient(new CountingAnimeListsHandler("<anime-list />")));

    private static string CreateTempConfigDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "arbitarr-resolver-animelists", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// A handler that serves one anime-lists document and COUNTS the requests it was asked for, so
    /// "the tier never ran" can be asserted as zero rather than inferred.
    /// </summary>
    private sealed class CountingAnimeListsHandler : HttpMessageHandler
    {
        private readonly string _xml;

        public CountingAnimeListsHandler(string xml) => _xml = xml;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_xml, Encoding.UTF8, "application/xml"),
            });
        }
    }

    [Fact]
    public async Task Resolves_the_series_title_from_a_configured_sonarr()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, EpisodeFeed("One Piece"));

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
        Assert.Equal(TvdbId, identity.TvdbId);
    }

    /// <summary>
    /// The echo guard. <see cref="ArrApiProvider"/> falls back to the title it was GIVEN when the
    /// episode rows carry no series object — and here that argument is the caller's raw <c>q</c>, a
    /// bare episode number. Returning it would put "92 92" on the wire, which is worse than the
    /// id-only request this degrades to instead.
    /// </summary>
    [Fact]
    public async Task Admits_nothing_when_sonarr_returns_no_series_title_rather_than_echoing_the_episode_number()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, EpisodeFeed(seriesTitle: null));

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
    }

    /// <summary>
    /// The echo guard's other half, and the one a text comparison alone does not catch: the SAME
    /// episode number spelled differently. "092" echoed back beside a <c>q</c> of "92" would put
    /// "92 092" on the wire, which is the duplication the guard exists to prevent — the padding does
    /// not make it a title.
    /// </summary>
    [Theory]
    [InlineData("92")]
    [InlineData("092")]
    [InlineData("00092")]
    [InlineData(" 92 ")]
    public async Task Admits_nothing_when_the_resolved_title_is_the_episode_number_we_sent(string seriesTitle)
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, EpisodeFeed(seriesTitle));

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
    }

    /// <summary>
    /// The positive control for the guard above, and the reason it is scoped to the echoed number
    /// rather than to "any numeric title".
    /// </summary>
    /// <remarks>
    /// Rejecting every all-digit title was written first and is WRONG: <c>86</c> is a real series,
    /// and so are <c>91 Days</c> and <c>5</c>. Guarding against a number that is not the one we sent
    /// would make those unsearchable — a quieter bug than the one being prevented — and
    /// <c>ArrApiProvider</c> cannot produce that shape anyway: it returns either the title it was
    /// given (which the echo guard catches) or a genuine <c>series.title</c> from *arr.
    /// </remarks>
    [Theory]
    [InlineData("86")]
    [InlineData("7")]
    [InlineData("91 Days")]
    [InlineData("Mobile Suit Gundam 00")]
    public async Task Keeps_a_real_title_even_when_it_is_numeric(string seriesTitle)
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, EpisodeFeed(seriesTitle));

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(seriesTitle, identity.PrimaryTitle);
    }

    [Fact]
    public async Task Admits_nothing_when_no_sonarr_instance_is_configured()
    {
        await using var context = CreateContext();

        var resolver = Build(context, EpisodeFeed("One Piece"));

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
    }

    /// <summary>
    /// A base URL with no key is a half-configured instance. The lookup is not attempted at all:
    /// <c>/api/v3/episode</c> would answer 401, which the provider would record as a circuit-breaker
    /// failure against a Sonarr that is not actually broken.
    /// </summary>
    [Fact]
    public async Task Admits_nothing_and_makes_no_request_when_the_instance_has_a_url_but_no_key()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, apiKey: null, CancellationToken.None);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EpisodeFeed("One Piece"), Encoding.UTF8, "application/json"),
        });
        var resolver = BuildWithHandler(context, handler);

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Admits_nothing_when_the_request_carries_no_tvdb_id()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, EpisodeFeed("One Piece"));

        Assert.Null(await resolver.ResolveAsync("92", Hints(tvdbId: null), CancellationToken.None));
    }

    [Fact]
    public async Task Admits_nothing_when_sonarr_cannot_be_reached()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var resolver = Build(context, body: string.Empty, status: HttpStatusCode.ServiceUnavailable);

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
    }

    /// <summary>
    /// The key reaches the wire — it has to, or the lookup could not work — and it reaches it in the
    /// QUERY STRING, which is the placement both the <c>Program.cs</c> registration comment and
    /// <c>SonarrKeyIsScrubbedFromLogsTests</c> depend on. If it ever moves into a path segment,
    /// neither the framework's query-string collapse nor <c>LogMessageCleanser</c> covers it and that
    /// client needs <c>.RemoveAllLoggers()</c>. The path assertion is what would catch the move.
    /// </summary>
    [Fact]
    public async Task Sends_the_api_key_in_the_query_string_and_never_in_the_url_path()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EpisodeFeed("One Piece"), Encoding.UTF8, "application/json"),
        });
        var resolver = BuildWithHandler(context, handler);

        await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        var uri = Assert.Single(handler.RequestedUris);

        // The positive control: the key IS in this URI, so the path assertion below is being made
        // about a request that genuinely carries it rather than passing on an empty set.
        Assert.Contains(ApiKey, uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.Ordinal);
    }

    // ---- The memo: a repeated search must not re-ask Sonarr ------------------------------------

    /// <summary>
    /// The reason the memo exists. Every page of a paginated anime search, and every sibling request
    /// Sonarr issues for one episode, resolves the same series — so asking once per request would put
    /// a live Sonarr call on a path that is otherwise served entirely from cache.
    /// </summary>
    [Fact]
    public async Task Asks_sonarr_once_for_a_repeated_lookup_of_the_same_series()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EpisodeFeed("One Piece"), Encoding.UTF8, "application/json"),
        });
        var resolver = BuildWithHandler(context, handler);

        var first = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);
        var second = await resolver.ResolveAsync("93", Hints(), CancellationToken.None);

        // The positive control: the FIRST call really did go to Sonarr, so "only one request" is a
        // statement about the second being served from the memo rather than about nothing happening.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("One Piece", first?.PrimaryTitle);
        Assert.Equal("One Piece", second?.PrimaryTitle);
    }

    /// <summary>
    /// arb-iiy: a memoised title is only true OF THE INSTANCE THAT ANSWERED IT. Repointing Sonarr at
    /// a different server must not keep serving the previous one's answer for the rest of
    /// <see cref="SeriesTitleResolver.MemoTtl"/>.
    /// </summary>
    /// <remarks>
    /// The two Sonarrs answer DIFFERENT titles for the same tvdbid, which is what makes the
    /// assertion bite: a resolver still keyed on the tvdbid alone returns instance A's title here,
    /// and the per-host request counts say why — B was never asked. Both counts are asserted, so
    /// this cannot pass by nothing having happened: A's count proves the memo was really populated
    /// from A before the repoint, and B's proves the second resolve reached B rather than the memo.
    /// No clock is moved, so the entry from A is still WELL WITHIN its TTL when B is asked.
    /// </remarks>
    [Fact]
    public async Task Asks_the_new_sonarr_after_a_repoint_within_the_memo_ttl()
    {
        await using var context = CreateContext();
        var epoch = new ArrInstanceEpoch();
        var repository = new ArrInstanceRepository(context, timeProvider: null, epoch);

        const string InstanceA = "http://192.0.2.21:8989/";
        const string InstanceB = "http://192.0.2.22:8989/";

        var handler = new PerHostHandler(
            ("192.0.2.21", EpisodeFeed("One Piece")),
            ("192.0.2.22", EpisodeFeed("Bleach")));

        // One memo and one resolver across the repoint, exactly as the singleton cache and a
        // re-resolved scoped resolver behave in production.
        var memo = NewMemo();

        await repository.SetAsync(InstanceA, ApiKey, CancellationToken.None);
        var fromA = await BuildWithHandler(context, handler, memo, epoch: epoch)
            .ResolveAsync("92", Hints(), CancellationToken.None);

        await repository.SetAsync(InstanceB, ApiKey, CancellationToken.None);
        var fromB = await BuildWithHandler(context, handler, memo, epoch: epoch)
            .ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Equal("One Piece", fromA?.PrimaryTitle);
        Assert.Equal("Bleach", fromB?.PrimaryTitle);

        // The positive control on both sides: each instance was asked exactly once. Without the
        // epoch in the memo key, B's count is 0 and fromB reads "One Piece".
        Assert.Equal(1, handler.CountFor("192.0.2.21"));
        Assert.Equal(1, handler.CountFor("192.0.2.22"));
    }

    /// <summary>
    /// Negative answers are memoised too, and this is the case that matters most: an unreachable
    /// Sonarr would otherwise be asked — and time out — once per request forever.
    /// </summary>
    [Fact]
    public async Task Asks_sonarr_once_even_when_the_answer_was_nothing()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var resolver = BuildWithHandler(context, handler);

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("93", Hints(), CancellationToken.None));

        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// The memo is keyed per series, so one series' answer must not be handed to another. Without
    /// this the memo would be a correctness bug rather than an optimisation.
    /// </summary>
    [Fact]
    public async Task Does_not_serve_one_series_title_for_a_different_tvdb_id()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new SequencedHandler(EpisodeFeed("One Piece"), EpisodeFeed("Bleach"));
        var resolver = BuildWithHandler(context, handler);

        var first = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);
        var second = await resolver.ResolveAsync("92", Hints(tvdbId: TvdbId + 1), CancellationToken.None);

        Assert.Equal("One Piece", first?.PrimaryTitle);
        Assert.Equal("Bleach", second?.PrimaryTitle);
        Assert.Equal(2, handler.RequestCount);
    }

    // ---- The budget: a slow Sonarr must not hold up the search ---------------------------------

    /// <summary>
    /// A title is an OPTIMISATION of the upstream query, never a precondition for issuing it, so a
    /// Sonarr that has stopped answering must cost the search its short budget and not the pooled
    /// client's much longer timeout. The handler here never completes until cancelled, which is
    /// exactly the shape a hung upstream presents.
    /// </summary>
    [Fact]
    public async Task Gives_up_on_a_sonarr_that_does_not_answer_within_the_budget()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var budget = TimeSpan.FromMilliseconds(200);
        var resolver = BuildWithHandler(context, new NeverAnswersHandler(), lookupBudget: budget);

        var stopwatch = Stopwatch.StartNew();
        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);
        stopwatch.Stop();

        Assert.Null(identity);

        // Bounded ABOVE by a generous multiple of the budget rather than asserted near-exactly: the
        // claim under test is "the budget is what ends this", and a CI machine's scheduling noise
        // must not turn that into a flake. The default client timeout would blow this bound by an
        // order of magnitude, which is the regression this is here to catch.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"the lookup took {stopwatch.Elapsed}, which is not bounded by the {budget} budget");
    }

    /// <summary>
    /// The caller's own cancellation is not the resolver's budget firing, and must not be memoised
    /// as an answer about the series — the next request would inherit an aborted one's silence.
    /// </summary>
    [Fact]
    public async Task Does_not_memoise_an_answer_when_the_caller_cancelled()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var memo = NewMemo();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var resolver = BuildWithHandler(context, new NeverAnswersHandler(), memo);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("92", Hints(), cancelled.Token));

        // The same memo, now asked by a caller who has not cancelled, reaches Sonarr rather than
        // replaying the aborted request's silence.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(EpisodeFeed("One Piece"), Encoding.UTF8, "application/json"),
        });
        var second = BuildWithHandler(context, handler, memo);

        var identity = await second.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Equal("One Piece", identity?.PrimaryTitle);
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// A FAILED lookup is memoised for much less time than a successful one, and the gap is
    /// load-bearing rather than a tuning preference.
    /// </summary>
    /// <remarks>
    /// <para>The resolved title is part of the snapshot token, so an unresolved answer does not
    /// merely cost precision — it selects a SEPARATE, id-only snapshot that then lives for the
    /// snapshot TTL. Memoising "Sonarr said nothing" for the positive five minutes would hold
    /// searches on that unresolved variant for the whole memo window on top of the snapshot's own,
    /// so one transient blip degrades anime searches for far longer than the blip lasted. A short
    /// negative TTL is what bounds that.</para>
    ///
    /// <para><b>WHY THE REQUESTED EXPIRY RATHER THAN AN OBSERVED ONE.</b> Asserting by advancing a
    /// clock would need <c>MemoryCacheOptions.Clock</c>, whose <c>ISystemClock</c> lives in
    /// <c>Microsoft.Extensions.Internal</c> — a test pinned to an internal abstraction breaks on a
    /// package bump for reasons having nothing to do with this behaviour. Sleeping past a real 45 s
    /// is not an option either. What the rule actually constrains is the lifetime the resolver ASKS
    /// FOR, so that is what is recorded and asserted.</para>
    /// </remarks>
    [Fact]
    public async Task Memoises_a_failed_lookup_for_much_less_time_than_a_resolved_one()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var failing = new RecordingMemo();
        await BuildWithHandler(
                context,
                new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
                failing)
            .ResolveAsync("92", Hints(), CancellationToken.None);

        var resolving = new RecordingMemo();
        var identity = await Build(context, EpisodeFeed("One Piece"), memo: resolving)
            .ResolveAsync("92", Hints(), CancellationToken.None);

        // The positive control. Both halves must actually have written to the memo, or the
        // comparison below would be between two absent values and would pass vacuously.
        Assert.Equal("One Piece", identity?.PrimaryTitle);
        var negative = Assert.Single(failing.Expirations);
        var positive = Assert.Single(resolving.Expirations);

        Assert.Equal(SeriesTitleResolver.NegativeMemoTtl, negative);
        Assert.Equal(SeriesTitleResolver.MemoTtl, positive);

        // The relationship, asserted separately from the two constants: this is the property that
        // must survive any future retuning of either value.
        Assert.True(
            negative < positive,
            $"a failed lookup was memoised for {negative}, which is not shorter than the {positive} "
            + "a resolved one gets — a transient Sonarr failure would pin the unresolved snapshot "
            + "variant for the whole positive window.");
    }

    /// <summary>
    /// Records the relative expiry each <c>Set</c> asked for, delegating everything else to a real
    /// cache so the resolver's own memo behaviour is unchanged.
    /// </summary>
    private sealed class RecordingMemo : IMemoryCache
    {
        private readonly IMemoryCache _inner = new MemoryCache(new MemoryCacheOptions());

        public List<TimeSpan?> Expirations { get; } = [];

        public ICacheEntry CreateEntry(object key) => new RecordingEntry(_inner.CreateEntry(key), this);

        public void Remove(object key) => _inner.Remove(key);

        public bool TryGetValue(object key, out object? value) => _inner.TryGetValue(key, out value);

        public void Dispose() => _inner.Dispose();

        /// <summary>
        /// Captures on DISPOSE rather than on assignment: <c>Set</c> is an extension method that
        /// creates the entry, assigns the expiry and the value, then disposes it to commit — so
        /// dispose is the one point at which the entry is complete.
        /// </summary>
        private sealed class RecordingEntry : ICacheEntry
        {
            private readonly ICacheEntry _inner;
            private readonly RecordingMemo _owner;

            public RecordingEntry(ICacheEntry inner, RecordingMemo owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public object Key => _inner.Key;

            public object? Value
            {
                get => _inner.Value;
                set => _inner.Value = value;
            }

            public DateTimeOffset? AbsoluteExpiration
            {
                get => _inner.AbsoluteExpiration;
                set => _inner.AbsoluteExpiration = value;
            }

            public TimeSpan? AbsoluteExpirationRelativeToNow
            {
                get => _inner.AbsoluteExpirationRelativeToNow;
                set => _inner.AbsoluteExpirationRelativeToNow = value;
            }

            public TimeSpan? SlidingExpiration
            {
                get => _inner.SlidingExpiration;
                set => _inner.SlidingExpiration = value;
            }

            public IList<IChangeToken> ExpirationTokens => _inner.ExpirationTokens;

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks => _inner.PostEvictionCallbacks;

            public CacheItemPriority Priority
            {
                get => _inner.Priority;
                set => _inner.Priority = value;
            }

            public long? Size
            {
                get => _inner.Size;
                set => _inner.Size = value;
            }

            public void Dispose()
            {
                _owner.Expirations.Add(_inner.AbsoluteExpirationRelativeToNow);
                _inner.Dispose();
            }
        }
    }

    /// <summary>Hands out one preconfigured client, which is all the resolver asks of the factory.</summary>
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StubHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Always-closed circuit breaker; the breaker's own behaviour is tested elsewhere.</summary>
    private sealed class StubCircuitBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Answers each request with the next body in turn, so two series can be told apart.</summary>
    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;

        public SequencedHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// arb-iiy: answers according to WHICH Sonarr was asked, and counts the requests per host — so a
    /// repoint test can say both what came back and which server produced it.
    /// </summary>
    private sealed class PerHostHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodiesByHost;
        private readonly Dictionary<string, int> _countsByHost = new(StringComparer.OrdinalIgnoreCase);

        public PerHostHandler(params (string Host, string Body)[] hosts) =>
            _bodiesByHost = hosts.ToDictionary(h => h.Host, h => h.Body, StringComparer.OrdinalIgnoreCase);

        public int CountFor(string host) => _countsByHost.TryGetValue(host, out var count) ? count : 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            _countsByHost[host] = CountFor(host) + 1;

            // An unexpected host is a FAILED assertion rather than an empty feed: a silent 200 with
            // no episodes would degrade to "no title", which reads like the memo behaving correctly.
            Assert.True(_bodiesByHost.ContainsKey(host), $"Unexpected Sonarr host asked: {host}");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodiesByHost[host], Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Never completes until the request is cancelled — a hung upstream, which is what the budget
    /// exists to bound. A handler that merely delayed for a fixed time would also pass a test that
    /// had no bound at all, given a long enough delay; this one cannot.
    /// </summary>
    private sealed class NeverAnswersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }

    // ================= arb-5uw: ADR 0002's AnimeLists fallback tier =================

    /// <summary>
    /// THE POSITIVE CONTROL for the two zero-request assertions below. It establishes that this
    /// handler, this provider and this resolver really do produce a counted request when the tier
    /// runs — so a later <c>Assert.Equal(0, handler.RequestCount)</c> is evidence the tier was
    /// skipped, rather than evidence the handler was never reachable in the first place.
    /// </summary>
    [Fact]
    public async Task Positive_control_the_anime_lists_handler_records_a_request_when_the_tier_actually_runs()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new CountingAnimeListsHandler(AnimeListsXml("One Piece"));
        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(handler));

        // Sonarr answers with no series object, so the *arr tier admits nothing and the fallback runs.
        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
    }

    /// <summary>
    /// ADR 0002: several DISTINCT names claim the series, nothing can separate them, so NONE is
    /// admitted and the ambiguity is reported as an inspectable flag rather than left to be inferred
    /// from the null.
    /// </summary>
    [Fact]
    public async Task Several_distinct_anime_lists_names_admit_nothing_and_flag_the_ambiguity()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new CountingAnimeListsHandler(AnimeListsXml("One Piece", "Wan Pisu"));
        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(handler));

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Null(identity);
        Assert.Equal(MatchProvenanceFlags.AmbiguousMapping, resolver.LastAnimeListsFlags);

        // The tier really was consulted — otherwise the null above would prove nothing about ADR
        // 0002's withholding, only that the fallback never ran.
        Assert.Equal(1, handler.RequestCount);
    }

    /// <summary>
    /// The other half of ADR 0002: exactly one name is not ambiguous, so it resolves, and no
    /// ambiguity is reported.
    /// </summary>
    [Fact]
    public async Task Exactly_one_anime_lists_name_resolves_and_flags_no_ambiguity()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(new CountingAnimeListsHandler(AnimeListsXml("One Piece"))));

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
        Assert.Equal(MatchProvenanceFlags.None, resolver.LastAnimeListsFlags);
    }

    /// <summary>
    /// The *arr instance is authoritative, so when it answers the fallback tier is NOT consulted at
    /// all — asserted as zero counted requests on the handler the positive control above proves does
    /// count them.
    /// </summary>
    [Fact]
    public async Task An_arr_api_answer_means_the_anime_lists_tier_is_never_consulted()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new CountingAnimeListsHandler(AnimeListsXml("Wrong Title From The Static Map"));
        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(handler));

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed("One Piece"), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
        Assert.Equal(0, handler.RequestCount);
    }

    /// <summary>
    /// The INACTIVE state: no source URL configured, so the tier contributes nothing and — the
    /// property the whole registration rests on — issues no request. Zero is meaningful here only
    /// because the positive control above shows the same handler counts one when the tier runs.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_source_url_leaves_the_tier_inactive_and_issues_no_request()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var handler = new CountingAnimeListsHandler(AnimeListsXml("One Piece"));
        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(SourceUrl: null, ConfigDirectory: CreateTempConfigDirectory()),
            new HttpClient(handler));

        Assert.False(animeLists.IsConfigured);

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Null(identity);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(MatchProvenanceFlags.None, resolver.LastAnimeListsFlags);
    }

    /// <summary>
    /// The echo guard applies to the fallback tier too: a static-map name equal to the bare episode
    /// number the caller sent would put "92 92" on the wire, which is the exact failure this
    /// resolver exists to prevent.
    /// </summary>
    [Fact]
    public async Task An_anime_lists_name_equal_to_the_episode_number_is_not_echoed_back()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(new CountingAnimeListsHandler(AnimeListsXml("092"))));

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.Null(identity);
    }

    /// <summary>
    /// One name spelled two ways is ONE name, not a manufactured ambiguity: admitting nothing here
    /// would withhold a title that nothing actually competes with, which is a cost ADR 0002 does not
    /// ask anyone to pay.
    /// </summary>
    [Fact]
    public async Task The_same_anime_lists_name_in_two_spellings_is_not_ambiguous()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var animeLists = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            new HttpClient(new CountingAnimeListsHandler(AnimeListsXml("One Piece", " one piece "))));

        var resolver = BuildWithHandler(
            context,
            new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EpisodeFeed(seriesTitle: null), Encoding.UTF8, "application/json"),
            }),
            animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
        Assert.Equal(MatchProvenanceFlags.None, resolver.LastAnimeListsFlags);
    }

    /// <summary>
    /// arb-5uw: the provider must NOT assign <see cref="HttpClient.Timeout"/> in its constructor.
    /// Constructing it over a client that already has a request in flight throws if the assignment
    /// is restored, which is exactly the shape a pooled singleton produces in production.
    /// </summary>
    [Fact]
    public async Task Constructing_the_provider_over_a_client_with_a_started_request_does_not_throw()
    {
        using var handler = new NeverAnswersHandler();
        using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();

        // Start a request and leave it in flight: HttpClient.Timeout throws once this has happened,
        // so a constructor that assigned it would fail here rather than in production.
        var inFlight = client.GetAsync(new Uri(AnimeListsSourceUrl), cancel.Token);
        await Task.Yield();

        var provider = new AnimeListsProvider(
            new AnimeListsProviderOptions(new Uri(AnimeListsSourceUrl), CreateTempConfigDirectory()),
            client);

        Assert.True(provider.IsConfigured);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
    }

    /// <summary>
    /// The required dependency, asserted directly: null is rejected rather than quietly disabling
    /// the tier the way the previous optional parameter did.
    /// </summary>
    [Fact]
    public void The_anime_lists_provider_is_a_required_dependency()
    {
        using var context = CreateContext();

        var exception = Assert.Throws<ArgumentNullException>(() => new SeriesTitleResolver(
            new SonarrCredentialProvider(new ArrInstanceRepository(context)),
            new StubHttpClientFactory(new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))),
            new StubCircuitBreaker(),
            NewMemo(),
            new ArrInstanceEpoch(),
            animeLists: null!));

        Assert.Equal("animeLists", exception.ParamName);
    }
}
