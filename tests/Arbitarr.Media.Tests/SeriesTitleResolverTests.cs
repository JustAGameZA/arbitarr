using System.Net;
using System.Text;
using Arbitarr.Core.Identity;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Media;
using Arbitarr.Media.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Media.Tests;

/// <summary>
/// arb-u1c: <see cref="SeriesTitleResolver"/> turns the tvdbid on Sonarr's anime search into the
/// series title the upstream query needs, or admits nothing.
///
/// <para>
/// The cases that matter here are the ones where it must admit NOTHING, because each of them is a
/// way a plausible implementation sends a confidently wrong query upstream: echoing back the bare
/// episode number it was given as if it were a title, or picking the first of several AniDB names
/// that the data gives no basis to choose between (ADR 0002).
/// </para>
/// </summary>
public sealed class SeriesTitleResolverTests : IDisposable
{
    private const int TvdbId = 81797;
    private const string BaseUrl = "http://192.0.2.21:8989/";
    private const string ApiKey = "placeholder-sonarr-key-0123456789";

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"arbitarr-resolver-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
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

    private static SeriesTitleResolver Build(
        ArbitarrDbContext context,
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        AnimeListsProvider? animeLists = null)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });

        return new SeriesTitleResolver(
            new ArrInstanceRepository(context),
            new StubHttpClientFactory(new HttpClient(handler)),
            new StubCircuitBreaker(),
            animeLists);
    }

    private static IdentityResolutionHints Hints(int? tvdbId = TvdbId) => new(tvdbId, TmdbId: null, Year: null);

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
        var resolver = new SeriesTitleResolver(
            new ArrInstanceRepository(context),
            new StubHttpClientFactory(new HttpClient(handler)),
            new StubCircuitBreaker());

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
        var resolver = new SeriesTitleResolver(
            new ArrInstanceRepository(context),
            new StubHttpClientFactory(new HttpClient(handler)),
            new StubCircuitBreaker());

        await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        var uri = Assert.Single(handler.RequestedUris);

        // The positive control: the key IS in this URI, so the path assertion below is being made
        // about a request that genuinely carries it rather than passing on an empty set.
        Assert.Contains(ApiKey, uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.Ordinal);
    }

    // ---- ADR 0002: the AnimeLists fallback admits nothing when it cannot choose -----------------

    /// <summary>
    /// ADR 0002, and the reason the fallback is not "take the first name". <c>AnimeListsEntry.Names</c>
    /// is an unordered set of alternate renderings with no primary designation, so several DISTINCT
    /// names means the data cannot say which to search for. Picking one anyway would send a
    /// confidently wrong query upstream; admitting nothing degrades to the id-only request, which is
    /// imprecise but never wrong.
    /// </summary>
    [Fact]
    public async Task Admits_nothing_when_animelists_offers_several_distinct_names()
    {
        await using var context = CreateContext();
        var resolver = BuildWithAnimeLists(context, new AnimeListsEntry(
            AniDbId: 69,
            TvdbId: TvdbId,
            TmdbId: null,
            DefaultTvdbSeason: 1,
            Names: new[] { "Ghost in the Shell: Arise", "Ghost in the Shell: SAC_2045" }));

        Assert.Null(await resolver.ResolveAsync("92", Hints(), CancellationToken.None));
    }

    /// <summary>
    /// The positive control for the test above: the fallback is reached and CAN resolve, so
    /// "admits nothing when ambiguous" is a decision about the names rather than a fallback that
    /// never returns anything at all.
    /// </summary>
    [Fact]
    public async Task Resolves_from_animelists_when_exactly_one_name_is_on_offer()
    {
        await using var context = CreateContext();
        var resolver = BuildWithAnimeLists(context, new AnimeListsEntry(
            AniDbId: 69,
            TvdbId: TvdbId,
            TmdbId: null,
            DefaultTvdbSeason: 1,
            Names: new[] { "One Piece" }));

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
    }

    /// <summary>
    /// Names that differ only in casing or surrounding whitespace are the SAME name, not competing
    /// candidates — otherwise a duplicate row in a hand-edited XML file would be read as ambiguity
    /// and suppress a title the data actually agrees on.
    /// </summary>
    [Fact]
    public async Task Treats_names_differing_only_in_case_or_whitespace_as_one_name()
    {
        await using var context = CreateContext();
        var resolver = BuildWithAnimeLists(context, new AnimeListsEntry(
            AniDbId: 69,
            TvdbId: TvdbId,
            TmdbId: null,
            DefaultTvdbSeason: 1,
            Names: new[] { "One Piece", " one piece ", "One Piece" }));

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
    }

    /// <summary>
    /// Sonarr wins when both can answer: it is the authority that already reconciled this series,
    /// and the Q5-D preference order puts it first.
    /// </summary>
    [Fact]
    public async Task Prefers_the_sonarr_title_over_the_animelists_name()
    {
        await using var context = CreateContext();
        await new ArrInstanceRepository(context).SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var animeLists = MakeAnimeLists(new AnimeListsEntry(
            AniDbId: 69, TvdbId: TvdbId, TmdbId: null, DefaultTvdbSeason: 1,
            Names: new[] { "Wan Pisu" }));

        var resolver = Build(context, EpisodeFeed("One Piece"), animeLists: animeLists);

        var identity = await resolver.ResolveAsync("92", Hints(), CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal("One Piece", identity.PrimaryTitle);
    }

    private SeriesTitleResolver BuildWithAnimeLists(ArbitarrDbContext context, AnimeListsEntry entry) =>
        new(
            new ArrInstanceRepository(context),
            new StubHttpClientFactory(new HttpClient(new FakeHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))),
            new StubCircuitBreaker(),
            MakeAnimeLists(entry));

    /// <summary>
    /// An <see cref="AnimeListsProvider"/> backed by a one-entry XML document served from a stub
    /// handler and persisted to a per-test directory, so the fallback is exercised through the real
    /// provider rather than a hand-made stand-in.
    /// </summary>
    private AnimeListsProvider MakeAnimeLists(AnimeListsEntry entry)
    {
        var names = string.Concat(entry.Names.Select(n =>
            $"<name>{System.Security.SecurityElement.Escape(n)}</name>"));
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><anime-list>"
            + $"<anime anidbid=\"{entry.AniDbId}\" tvdbid=\"{entry.TvdbId}\" defaulttvdbseason=\"{entry.DefaultTvdbSeason}\">"
            + $"{names}</anime></anime-list>";

        var directory = Path.Combine(Path.GetTempPath(), $"arbitarr-animelists-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });

        return new AnimeListsProvider(
            new AnimeListsProviderOptions(
                new Uri("http://anime-lists.example.invalid/anime-list-full.xml"),
                ConfigDirectory: directory,
                MinimumRequestSpacing: TimeSpan.Zero),
            new HttpClient(handler));
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
}
