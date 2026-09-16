using Arbitarr.Core.Sources;
using Arbitarr.Core.Sources.CircuitBreaker;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-x7w8.4: <see cref="SourceRegistry"/> resolves the enabled <c>Sources</c> rows into the
/// <see cref="IUpstreamSource"/> instances a search fans out to — N of them, per scope, so an
/// operator's edit takes effect without a restart.
/// </summary>
/// <remarks>
/// The real-container half (that the composition root actually registers this, and that each
/// resolved source carries its own redirect-disabled client) is pinned separately by
/// <c>Arbitarr.Integration.Tests.SourceRegistryWiringTests</c>. A test that builds its own subject
/// cannot see a missing registration.
/// </remarks>
public sealed class SourceRegistryTests : IDisposable
{
    // RFC 5737 TEST-NET-1 throughout: non-routable, and no real address is committed.
    private const string HydraBaseUrl = "http://192.0.2.40:5076";
    private const string NewznabBaseUrl = "http://192.0.2.41:9117";
    private const string TorznabBaseUrl = "http://192.0.2.42:9117";

    private readonly SqliteTestDatabase _database = new("arbitarr-x7w84-registry");
    private readonly RecordingHttpClientFactory _httpClientFactory = new();
    private readonly RecordingLogger _logger = new();

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private SourceRegistry CreateRegistry(ArbitarrDbContext context) =>
        new(
            context,
            new SourceCredentialProvider(new SourceRepository(context)),
            _httpClientFactory,
            new AlwaysClosedCircuitBreaker(),
            _logger);

    private static Source Row(
        string displayName,
        string baseUrl,
        string kind = SourceRepository.NewznabKind,
        string apiPath = "/api",
        int priority = 0,
        bool enabled = true,
        int? timeoutSeconds = null) =>
        new()
        {
            Kind = kind,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            ApiPath = apiPath,
            Priority = priority,
            Enabled = enabled,
            TimeoutSeconds = timeoutSeconds,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// N=0. An install with no sources resolves an empty list and does not throw — searching nothing
    /// is the correct behaviour for a correctly-unconfigured install, and throwing here would turn
    /// the dashboard's empty state into a 500.
    /// </summary>
    [Fact]
    public async Task No_sources_resolve_to_an_empty_list()
    {
        using var context = CreateContext();

        Assert.Empty(await CreateRegistry(context).ResolveAsync(CancellationToken.None));
    }

    /// <summary>N=1. One enabled row resolves to one source carrying that row's display name.</summary>
    [Fact]
    public async Task One_enabled_source_resolves_to_one_upstream_source()
    {
        using var context = CreateContext();
        context.Sources.Add(Row("solo", NewznabBaseUrl));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal("solo", Assert.Single(resolved).Name);
    }

    /// <summary>
    /// N=3, and the ORDER is asserted, not merely the membership. Higher priority wins (see
    /// <see cref="Source.Priority"/>), so the set comes back highest-first; the fan-out and the
    /// dedup stage both read this order.
    /// </summary>
    [Fact]
    public async Task Three_enabled_sources_resolve_highest_priority_first()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("lowest", NewznabBaseUrl, priority: 1),
            Row("highest", TorznabBaseUrl, kind: SourceRepository.TorznabKind, priority: 99),
            Row("middle", HydraBaseUrl, kind: SourceRepository.NzbHydraKind, priority: 50));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal(new[] { "highest", "middle", "lowest" }, resolved.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// Equal priorities are broken by id, so the order is TOTAL. Two sources an operator left at the
    /// neutral default must not swap places between requests — that would make the dedup stage's
    /// "first member of an exact-match group" answer vary for identical data.
    /// </summary>
    [Fact]
    public async Task Equal_priorities_are_broken_by_id_so_the_order_is_total()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("added-first", NewznabBaseUrl),
            Row("added-second", TorznabBaseUrl));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal(new[] { "added-first", "added-second" }, resolved.Select(s => s.Name).ToArray());
    }

    /// <summary>
    /// <b>A source disabled MID-FLIGHT stops contributing on the next resolution.</b> This is the
    /// property the whole bead is about: before the registry, the source set was fixed at startup, so
    /// disabling a source in the UI did nothing until a restart.
    /// </summary>
    /// <remarks>
    /// The second resolution uses a SEPARATE registry over a fresh context, which is what a new scope
    /// gives in production — resolving twice from one instance would not distinguish a live read from
    /// a value cached in a field.
    /// </remarks>
    [Fact]
    public async Task A_source_disabled_mid_flight_stops_contributing()
    {
        using var seedContext = CreateContext();
        seedContext.Sources.AddRange(
            Row("stays", NewznabBaseUrl),
            Row("gets-disabled", TorznabBaseUrl, kind: SourceRepository.TorznabKind));
        await seedContext.SaveChangesAsync();

        using (var firstScope = CreateContext())
        {
            var before = await CreateRegistry(firstScope).ResolveAsync(CancellationToken.None);
            Assert.Equal(2, before.Count);
        }

        using (var writeScope = CreateContext())
        {
            var row = await writeScope.Sources.FirstAsync(s => s.DisplayName == "gets-disabled");
            row.Enabled = false;
            await writeScope.SaveChangesAsync();
        }

        using var secondScope = CreateContext();
        var after = await CreateRegistry(secondScope).ResolveAsync(CancellationToken.None);

        Assert.Equal("stays", Assert.Single(after).Name);
    }

    /// <summary>
    /// An unrecognised <c>Kind</c> is SKIPPED with a warning, and the rest of the set still resolves.
    /// Throwing would discard every other source over one bad row — a single kind written by a newer
    /// version, then downgraded, would take the whole search path down.
    /// </summary>
    [Fact]
    public async Task An_unknown_kind_is_skipped_without_discarding_the_rest()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("good", NewznabBaseUrl),
            Row("from-the-future", TorznabBaseUrl, kind: "SomeKindThisVersionDoesNotKnow"));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal("good", Assert.Single(resolved).Name);
    }

    /// <summary>
    /// <b>The skip warning names the ROW ID and no operator-supplied text.</b>
    /// </summary>
    /// <remarks>
    /// <para>The kind and the base URL are both operator-supplied, and the base URL may still carry
    /// userinfo (<c>SourceRepository.ValidateBaseUrl</c> accepts it today, unlike its *arr siblings).
    /// Neither belongs in a log row: <c>LogMessageCleanser</c> scrubs credentials in QUERY STRINGS,
    /// so a credential in a bare value or a URL's userinfo reaches the log store verbatim
    /// (CLAUDE.md §1).</para>
    ///
    /// <para>The POSITIVE CONTROL is the first assertion: the warning must actually have been
    /// emitted and must name the id. Without it, "the kind does not appear" would pass just as
    /// happily over a set with no warnings in it at all — the vacuous shape CLAUDE.md §4 forbids.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_skip_warning_names_the_row_id_and_no_operator_supplied_text()
    {
        const string secretBearingKind = "KindCarryingUser:PasswordLikeText";

        using var context = CreateContext();
        var row = Row("skipped", "http://user:hunter2@192.0.2.99:9117", kind: secretBearingKind);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        // The control: a warning WAS emitted, and it identifies the row — so the absence assertions
        // below are about a message that exists.
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), warning, StringComparison.Ordinal);

        Assert.DoesNotContain(secretBearingKind, warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.99", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A row whose <c>ApiPath</c> resolves OFF its own origin is skipped, never resolved into a
    /// live source.</b>
    /// </summary>
    /// <remarks>
    /// <para><see cref="Arbitarr.Sources.Newznab.NewznabSource"/> composes its endpoint as
    /// <c>new Uri(BaseUrl, ApiPath.TrimStart('/'))</c>, and Uri composition lets an ABSOLUTE second
    /// argument win outright — so this row would otherwise send every request, carrying its API key,
    /// to a host the operator never configured. <c>SourceRepository.ValidateApiPath</c> does not
    /// close it: it rejects a query string and an embedded apikey and accepts everything else on
    /// purpose.</para>
    ///
    /// <para>The theory covers several escape spellings rather than only the absolute-URL one,
    /// because the guard compares the RESOLVED origin rather than the path's text — which is what
    /// makes it hold against forms nobody enumerated. The neighbouring row proves the skip is
    /// surgical: one bad path must not discard the rest of the set.</para>
    /// </remarks>
    [Theory]
    [InlineData("http://elsewhere.example/api")]
    [InlineData("https://elsewhere.example/api")]
    // Same host, different scheme: a downgrade that puts the key on the wire in clear.
    [InlineData("http://192.0.2.41:9117/api")]
    // Same host and scheme, different port.
    [InlineData("https://192.0.2.41:9999/api")]
    // SAME scheme, host AND port as the base — so all three origin comparisons pass and ONLY the
    // userinfo clause can reject this row. Userinfo is not an origin component, which is precisely
    // why it needs its own clause: credentials nobody configured would otherwise ride every request
    // and land in the logged URI's AUTHORITY, which neither the framework's query-string redaction
    // nor LogMessageCleanser covers (CLAUDE.md §1). Admitted here it would also reach the adapter's
    // own guard and throw out of the whole resolution instead of skipping this one row.
    [InlineData("https://intruder@192.0.2.41:9117/api")]
    public async Task An_api_path_that_resolves_off_origin_is_skipped(string escapingApiPath)
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("innocent", TorznabBaseUrl, kind: SourceRepository.TorznabKind),
            Row("escaping", "https://192.0.2.41:9117", apiPath: escapingApiPath));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal("innocent", Assert.Single(resolved).Name);
        Assert.Single(_logger.Warnings);
    }

    /// <summary>
    /// An <c>ApiPath</c> that resolves ON origin still resolves — the guard above must reject the
    /// escape and nothing else. Without this, a guard that skipped EVERY Newznab row would pass every
    /// assertion in the test above while disabling the feature entirely.
    /// </summary>
    /// <remarks>
    /// <b>The scheme-relative <c>//host/path</c> form belongs HERE, not above, and that is a measured
    /// fact rather than an assumption.</b> It looks like a classic escape, but the adapter composes
    /// its endpoint as <c>new Uri(BaseUrl, ApiPath.TrimStart('/'))</c> — and the <c>TrimStart</c>
    /// strips BOTH leading slashes, so <c>//elsewhere.example/api</c> becomes the ordinary relative
    /// segment <c>elsewhere.example/api</c> and resolves to
    /// <c>{BaseUrl}/elsewhere.example/api</c>. Checked by composing it directly: the target stays on
    /// the row's own origin, so refusing it would reject a harmless path. This is exactly why the
    /// guard compares the RESOLVED origin rather than the path's spelling — a spelling-based guard
    /// would have to decide this case from how it looks, and would get it wrong in one direction or
    /// the other.
    /// </remarks>
    [Theory]
    [InlineData("/api")]
    [InlineData("api")]
    [InlineData("/api/v2.0/indexers/all/results/torznab")]
    [InlineData("//elsewhere.example/api")]
    public async Task An_api_path_that_resolves_on_origin_still_resolves(string apiPath)
    {
        using var context = CreateContext();
        context.Sources.Add(Row("ordinary", NewznabBaseUrl, apiPath: apiPath));
        await context.SaveChangesAsync();

        var resolved = await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal("ordinary", Assert.Single(resolved).Name);
        Assert.Empty(_logger.Warnings);
    }

    /// <summary>
    /// <b>THE KEY IS READ PER SCOPE AND NEVER CAPTURED ACROSS SCOPES.</b> A key changed between two
    /// scopes must be the SECOND value that is used.
    /// </summary>
    /// <remarks>
    /// <para>This is what a static field, a singleton registration, a closure over a startup-built
    /// list or any process-lifetime cache over the credential would break, and none of the other
    /// tests in this file would notice — they all resolve once. A captured key is not merely stale:
    /// it means an operator who rotates a compromised key keeps sending the old one until the
    /// process restarts, which is the defect arb-x7w8.4 exists to remove.</para>
    ///
    /// <para><b>A SCOPE, not a call, is the unit — and deliberately so.</b> The registry memoises
    /// within one scope, because a request whose two consumers each re-read the key would read every
    /// source's secret twice per request for no benefit. So the guarantee this pins is the one that
    /// matters operationally: the NEXT request sees the rotated key. Asserting a second call on the
    /// SAME registry would instead assert the absence of that memoisation, contradicting it. See
    /// <see cref="A_second_resolution_in_one_scope_does_not_resolve_again_but_a_new_scope_does"/>,
    /// which pins the memoisation itself with its own positive control.</para>
    ///
    /// <para>Read back off the constructed adapter rather than from the database, because the
    /// database obviously holds the new value — the question is which value the SOURCE is carrying.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_key_is_read_per_resolution_rather_than_captured()
    {
        const string firstKey = "secret-api-key-registry-first";
        const string secondKey = "secret-api-key-registry-second";

        using var context = CreateContext();
        var row = Row("rotated", NewznabBaseUrl);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        context.Settings.Add(new SettingEntry
        {
            Name = SourceRepository.ApiKeySettingName(row.Id),
            Value = firstKey,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        // The control: the FIRST scope carries the first key, so the inequality below is a change
        // that was observed rather than a value that was never there.
        Assert.Equal(
            firstKey,
            KeyOf(Assert.Single(await CreateRegistry(context).ResolveAsync(CancellationToken.None))));

        var keyRow = await context.Settings.FirstAsync(e => e.Name == SourceRepository.ApiKeySettingName(row.Id));
        keyRow.Value = secondKey;
        await context.SaveChangesAsync();

        // A FRESH registry, as the next request gets. Anything holding the key beyond its own scope
        // — a static, a singleton registration, a list built once at startup — still answers with
        // the first key here and fails.
        Assert.Equal(
            secondKey,
            KeyOf(Assert.Single(await CreateRegistry(context).ResolveAsync(CancellationToken.None))));
    }

    /// <summary>
    /// The key an adapter is actually carrying. Read off its options because that is the value it
    /// will send; asserting the database would only restate what the test already wrote.
    /// </summary>
    private static string KeyOf(IUpstreamSource source)
    {
        var optionsField = source.GetType()
            .GetField("_options", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var options = optionsField?.GetValue(source)
            ?? throw new InvalidOperationException(
                $"{source.GetType().Name} no longer holds its options in _options; this test reads "
                + "the key the adapter carries and must be updated with the field.");

        return (string)(options.GetType().GetProperty("ApiKey")?.GetValue(options)
            ?? throw new InvalidOperationException("The adapter's options no longer expose ApiKey."));
    }

    /// <summary>
    /// <b>EVERY SOURCE GETS ITS OWN <see cref="HttpClient"/>.</b> The adapters honour their row's
    /// <c>TimeoutSeconds</c> by assigning <see cref="HttpClient.Timeout"/> on the client they are
    /// handed (NewznabSource:46-51), so a client shared across sources would silently make the
    /// last-constructed source's timeout win for all of them — and would throw outright once a
    /// request had started on that instance.
    /// </summary>
    [Fact]
    public async Task Every_source_gets_a_distinct_http_client()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("ten-seconds", NewznabBaseUrl, timeoutSeconds: 10),
            Row("thirty-seconds", TorznabBaseUrl, kind: SourceRepository.TorznabKind, timeoutSeconds: 30),
            Row("hydra", HydraBaseUrl, kind: SourceRepository.NzbHydraKind, timeoutSeconds: 20));
        await context.SaveChangesAsync();

        await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        // Reference equality, not count: three clients that were the SAME instance would still be
        // three handed out, and the timeouts below would then all read as the last one written.
        Assert.Equal(3, _httpClientFactory.Created.Distinct().Count());
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20) },
            _httpClientFactory.Created.Select(c => c.Timeout).ToArray());
    }

    /// <summary>
    /// A row with no <c>TimeoutSeconds</c> takes the adapter's own default rather than "no timeout" —
    /// a source that has never been tuned must not behave differently from one whose operator
    /// explicitly chose the default (see <see cref="Source.TimeoutSeconds"/>).
    /// </summary>
    [Fact]
    public async Task A_row_with_no_timeout_override_takes_the_adapter_default()
    {
        using var context = CreateContext();
        context.Sources.Add(Row("untuned", NewznabBaseUrl, timeoutSeconds: null));
        await context.SaveChangesAsync();

        await CreateRegistry(context).ResolveAsync(CancellationToken.None);

        Assert.Equal(
            Arbitarr.Sources.Newznab.NewznabSourceOptions.DefaultRequestTimeout,
            Assert.Single(_httpClientFactory.Created).Timeout);
    }

    /// <summary>
    /// <b>A SECOND RESOLUTION IN THE SAME SCOPE DOES NOT READ THE SOURCES AGAIN, and a NEW SCOPE
    /// DOES.</b>
    /// </summary>
    /// <remarks>
    /// <para>The registry is scoped and memoises within the scope, so the merge stage and the caps
    /// aggregator in one request share one resolution rather than each re-reading every enabled row
    /// and every source's API key. Reading a key twice per request is the cost this exists to avoid,
    /// and it is a secret read — so "once per request" is worth pinning rather than assuming.</para>
    ///
    /// <para><b>The second half is the POSITIVE CONTROL, and without it the first is vacuous.</b> An
    /// implementation that never resolved anything at all — or a counter that was never incremented —
    /// would satisfy "the count did not go up" perfectly. Asserting that a NEW registry DOES read
    /// again proves the counter moves when it should, so the first assertion is about memoisation
    /// rather than about a mechanism that was never running.</para>
    ///
    /// <para>Counted at the HTTP-client factory rather than at the provider because a client is
    /// created once per source per resolution, which makes the count a direct observation of how
    /// many times the set was built.</para>
    /// </remarks>
    [Fact]
    public async Task A_second_resolution_in_one_scope_does_not_resolve_again_but_a_new_scope_does()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("first", NewznabBaseUrl),
            Row("second", TorznabBaseUrl, kind: SourceRepository.TorznabKind));
        await context.SaveChangesAsync();

        var registry = CreateRegistry(context);

        var firstResolution = await registry.ResolveAsync(CancellationToken.None);
        var afterFirst = _httpClientFactory.Created.Count;
        Assert.Equal(2, afterFirst);

        var secondResolution = await registry.ResolveAsync(CancellationToken.None);

        // Nothing was built the second time, and the caller got the SAME instances — a re-resolution
        // that happened to produce an equal-looking set would still have re-read the keys.
        Assert.Equal(afterFirst, _httpClientFactory.Created.Count);
        Assert.Same(firstResolution, secondResolution);

        // THE CONTROL: a new scope resolves again, so the count above is one that can move.
        await CreateRegistry(context).ResolveAsync(CancellationToken.None);
        Assert.Equal(afterFirst + 2, _httpClientFactory.Created.Count);
    }

    /// <summary>
    /// Records the clients handed out so a test can assert each source got its own. Each call returns
    /// a NEW instance, which is what <see cref="IHttpClientFactory"/> itself does — a fake that
    /// returned one shared client would make the distinctness test above pass over behaviour
    /// production does not have.
    /// </summary>
    private sealed class RecordingHttpClientFactory : IHttpClientFactory
    {
        private readonly List<HttpClient> _created = [];

        public IReadOnlyList<HttpClient> Created => _created;

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new NeverSendsHandler(), disposeHandler: true);
            _created.Add(client);
            return client;
        }

        /// <summary>
        /// Nothing in these tests issues a request — the registry only CONSTRUCTS adapters — so this
        /// throws rather than returning a canned response. A handler that answered would let a test
        /// accidentally depend on a fake upstream without saying so.
        /// </summary>
        private sealed class NeverSendsHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException(
                    "SourceRegistryTests construct sources; they never issue upstream requests.");
        }
    }

    /// <summary>Open circuit, so construction is never gated on breaker state.</summary>
    private sealed class AlwaysClosedCircuitBreaker : IAsyncCircuitBreaker
    {
        public Task<bool> CanCallAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task RecordSuccessAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RecordFailureAsync(string sourceName, Exception exception, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger : ILogger<SourceRegistry>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<string> Warnings =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Every level enabled: a recorder gated at Warning would make an "it warned" assertion pass
        // over a set the production code was never allowed to populate at other levels.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
