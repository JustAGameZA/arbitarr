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
/// #53 stage 53b: unit-level coverage of <see cref="SourceSeeder"/>, the implementation of the plan's
/// §3.2 OWNER RULING (seed once from the environment, then the database is authoritative).
///
/// The end-to-end behaviour is pinned by <c>Arbitarr.Integration.Tests.SourceResolutionTests</c>
/// against the real composition root; these tests cover what is awkward to assert through HTTP —
/// specifically the two log lines the ruling requires (the one-time seeding line and the divergence
/// warning), which are the whole mitigation for this design's one real cost and are therefore
/// behaviour worth pinning rather than incidental output.
/// </summary>
public sealed class SourceSeederTests : IDisposable
{
    // RFC 5737 TEST-NET-1: non-routable, and no real address is committed.
    private const string EnvironmentBaseUrl = "http://192.0.2.10:5076";
    private const string DatabaseBaseUrl = "http://192.0.2.20:5076";

    private readonly SqliteTestDatabase _database = new("arbitarr-53b-seeder");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static EnvironmentSourceConfiguration EnvConfig(
        string baseUrl = EnvironmentBaseUrl,
        string apiKey = "placeholder-env-key",
        string sourceName = "NZBHydra2",
        bool supplied = true) =>
        new(baseUrl, apiKey, sourceName, BaseUrlWasSupplied: supplied, SourceNameWasSupplied: supplied);

    private static EnvironmentSourceConfiguration NoEnvironment() =>
        new("http://127.0.0.1:5076", string.Empty, "NZBHydra2",
            BaseUrlWasSupplied: false, SourceNameWasSupplied: false);

    [Fact]
    public async Task Seeds_from_the_environment_when_the_table_is_empty()
    {
        using var context = CreateContext();
        var resolved = new ResolvedSourceConfiguration();
        var logger = new RecordingLogger();

        await SourceSeeder.SeedAndResolveAsync(context, resolved, EnvConfig(), logger);

        var seeded = Assert.Single(await context.Sources.AsNoTracking().ToListAsync());
        Assert.Equal(SourceSeeder.NzbHydraKind, seeded.Kind);
        Assert.Equal(EnvironmentBaseUrl, seeded.BaseUrl);
        Assert.True(resolved.IsConfigured);
        Assert.Equal(EnvironmentBaseUrl, resolved.BaseUrl);
    }

    /// <summary>
    /// The ruling requires the seed to be logged explicitly and once, naming the seeded source and
    /// stating that env vars are now inert — that is what makes the one-way migration observable
    /// rather than invisible. The API key must not appear.
    /// </summary>
    [Fact]
    public async Task The_seeding_log_line_names_the_source_and_says_the_environment_is_now_inert()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(apiKey: "placeholder-seed-key"), logger);

        var seedLine = Assert.Single(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("seeded 1 source"));

        Assert.Contains("NZBHydra2", seedLine.Message);
        Assert.Contains("inert", seedLine.Message);
        Assert.Contains("no effect", seedLine.Message);

        // AC2 / #43 posture: the key's presence is reported, never its value.
        Assert.Contains("API key set", seedLine.Message);
        Assert.DoesNotContain("placeholder-seed-key", seedLine.Message, StringComparison.Ordinal);
        Assert.All(logger.Entries, e =>
            Assert.DoesNotContain("placeholder-seed-key", e.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// arb-c29: docker-compose.yml's own placeholder base URL is an RFC 2606 .invalid host, and the
    /// seed-once ruling means an unrejected seed of it would be authoritative forever, degrading the
    /// deployment to a permanently unreachable NZBHydra2 with nothing logging why. The seeder must
    /// reject it at seed time and seed nothing, exactly like the no-environment-configured branch.
    /// </summary>
    [Fact]
    public async Task Does_not_seed_a_dot_invalid_environment_base_url()
    {
        using var context = CreateContext();
        var resolved = new ResolvedSourceConfiguration();
        var logger = new RecordingLogger();

        await SourceSeeder.SeedAndResolveAsync(
            context, resolved, EnvConfig(baseUrl: "http://nzbhydra2.example.invalid:5076"), logger);

        Assert.Empty(await context.Sources.AsNoTracking().ToListAsync());
        Assert.False(resolved.IsConfigured);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("not a usable address"));
    }

    [Fact]
    public async Task Does_not_re_seed_or_overwrite_on_a_second_run()
    {
        using var context = CreateContext();

        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(), new RecordingLogger());
        var afterFirst = Assert.Single(await context.Sources.AsNoTracking().ToListAsync());

        // Second run, with a *different* environment: it must change nothing at all.
        await SourceSeeder.SeedAndResolveAsync(
            context,
            new ResolvedSourceConfiguration(),
            EnvConfig(baseUrl: "http://192.0.2.99:5076", sourceName: "Renamed"),
            new RecordingLogger());

        var afterSecond = Assert.Single(await context.Sources.AsNoTracking().ToListAsync());
        Assert.Equal(afterFirst.Id, afterSecond.Id);
        Assert.Equal(afterFirst.BaseUrl, afterSecond.BaseUrl);
        Assert.Equal(afterFirst.DisplayName, afterSecond.DisplayName);
        Assert.Equal(afterFirst.UpdatedAt, afterSecond.UpdatedAt);
    }

    /// <summary>
    /// §3.2's divergence mitigation: once seeded, a changed env var does nothing, so the operator
    /// must be told — by name — that their compose edit is inert.
    /// </summary>
    [Fact]
    public async Task Warns_when_an_environment_variable_diverges_from_the_stored_value()
    {
        using var context = CreateContext();
        context.Sources.Add(new Source
        {
            Kind = SourceSeeder.NzbHydraKind,
            DisplayName = "NZBHydra2",
            BaseUrl = DatabaseBaseUrl,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var logger = new RecordingLogger();
        await SourceSeeder.SeedAndResolveAsync(
            context,
            new ResolvedSourceConfiguration(),
            EnvConfig(baseUrl: EnvironmentBaseUrl, apiKey: "placeholder-divergent"),
            logger);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("NZBHydra2", warning.Message);
        Assert.Contains("base URL", warning.Message);
        Assert.Contains("API key", warning.Message);
        Assert.Contains("database value is in force", warning.Message);
        Assert.DoesNotContain("placeholder-divergent", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A compiled-in default that merely differs from a DB row is not divergence. Warning about it
    /// would fire on every start of every DB-configured deployment and train operators to ignore the
    /// line that matters.
    /// </summary>
    [Fact]
    public async Task Does_not_warn_when_no_environment_variables_are_supplied()
    {
        using var context = CreateContext();
        context.Sources.Add(new Source
        {
            Kind = SourceSeeder.NzbHydraKind,
            DisplayName = "NZBHydra2",
            BaseUrl = DatabaseBaseUrl,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        var logger = new RecordingLogger();
        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), NoEnvironment(), logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Does_not_warn_when_the_environment_agrees_with_the_stored_value()
    {
        using var context = CreateContext();
        var logger = new RecordingLogger();

        // Seed, then resolve again with the identical environment — the common restart case.
        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(), new RecordingLogger());
        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(), logger);

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Resolves_the_stored_api_key_so_a_seeded_deployment_stays_configured()
    {
        using var context = CreateContext();

        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(), new RecordingLogger());

        // Fresh resolve with no environment at all — the container-recreated-without-env case.
        var resolved = new ResolvedSourceConfiguration();
        await SourceSeeder.SeedAndResolveAsync(context, resolved, NoEnvironment(), new RecordingLogger());

        Assert.True(resolved.IsConfigured);
        Assert.Equal(EnvironmentBaseUrl, resolved.BaseUrl);
    }

    [Fact]
    public async Task Seeds_nothing_when_the_table_is_empty_and_no_environment_is_supplied()
    {
        using var context = CreateContext();
        var resolved = new ResolvedSourceConfiguration();

        await SourceSeeder.SeedAndResolveAsync(context, resolved, NoEnvironment(), new RecordingLogger());

        Assert.Empty(await context.Sources.AsNoTracking().ToListAsync());
        Assert.False(resolved.IsConfigured);
        Assert.Null(resolved.BaseUrl);
    }

    [Fact]
    public async Task Seeds_a_source_without_an_api_key_and_reports_it_as_not_configured()
    {
        using var context = CreateContext();
        var resolved = new ResolvedSourceConfiguration();
        var logger = new RecordingLogger();

        // A base URL but no key: a real, if half-finished, operator state.
        await SourceSeeder.SeedAndResolveAsync(
            context, resolved, EnvConfig(apiKey: string.Empty), logger);

        var seeded = Assert.Single(await context.Sources.AsNoTracking().ToListAsync());
        Assert.Equal(EnvironmentBaseUrl, seeded.BaseUrl);
        Assert.False(await context.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == SourceRepository.ApiKeySettingName(seeded.Id)));

        // "Configured" has always meant "has an API key" — unchanged by 53b (plan §3.4).
        Assert.False(resolved.IsConfigured);
        Assert.Contains(logger.Entries, e => e.Message.Contains("API key not set"));
    }

    /// <summary>
    /// #53 stage 53d's aggregate decision (plan §3.4), pinned: <b>"configured" means an ENABLED
    /// source with an API key</b>, not merely "a key exists somewhere".
    ///
    /// <para>53c's architectural review left this open and warned that
    /// <c>ResolvedSourceConfiguration.IsConfigured</c> reads only the key, so it "means 'has a key',
    /// NOT 'has an enabled source with a key'". The enabled-ness is in fact enforced one layer up,
    /// in <c>ResolveFromDatabaseAsync</c>'s <c>Where(... &amp;&amp; s.Enabled)</c> — so the behaviour
    /// was already correct and undocumented. This test is what makes it a decision rather than a
    /// coincidence: a future edit that lifts the enabled filter out of that query, or that re-derives
    /// the aggregate from a bare key lookup, fails here.</para>
    ///
    /// <para><b>Why this is the right meaning:</b> #50 exists to separate "configured" from
    /// "reporting". A source the operator has deliberately disabled will never be searched, so
    /// telling them on the dashboard that it is configured describes a working setup that cannot
    /// answer a single query — the misleading state #50 was written to prevent.</para>
    ///
    /// <para><b>Positive control.</b> The first half enables the same row, with the same key, and
    /// asserts <c>IsConfigured</c> is TRUE. Without it the <c>Assert.False</c> below would pass just
    /// as happily against a resolver that reported everything as unconfigured, or against a fixture
    /// whose key was never stored — an empty set satisfies a negative assertion. Proving the true
    /// case first is what makes the false case bite.</para>
    /// </summary>
    [Fact]
    public async Task A_disabled_source_with_a_key_is_not_reported_as_configured()
    {
        using var context = CreateContext();

        // Seed a normal enabled source that carries a key.
        await SourceSeeder.SeedAndResolveAsync(
            context, new ResolvedSourceConfiguration(), EnvConfig(), new RecordingLogger());

        var source = Assert.Single(await context.Sources.ToListAsync());
        Assert.True(await context.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == SourceRepository.ApiKeySettingName(source.Id)));

        // POSITIVE CONTROL: enabled and keyed resolves as configured, so the
        // negative assertion below is demonstrably capable of failing.
        var whileEnabled = new ResolvedSourceConfiguration();
        await SourceSeeder.SeedAndResolveAsync(
            context, whileEnabled, NoEnvironment(), new RecordingLogger());
        Assert.True(whileEnabled.IsConfigured);
        Assert.Equal(EnvironmentBaseUrl, whileEnabled.BaseUrl);

        // Now disable that same row. The key is untouched and still stored.
        source.Enabled = false;
        await context.SaveChangesAsync();
        Assert.True(await context.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == SourceRepository.ApiKeySettingName(source.Id)));

        var whileDisabled = new ResolvedSourceConfiguration();
        await SourceSeeder.SeedAndResolveAsync(
            context, whileDisabled, NoEnvironment(), new RecordingLogger());

        // The key still exists in the Settings table; the aggregate is false anyway.
        Assert.False(whileDisabled.IsConfigured);
        Assert.Null(whileDisabled.BaseUrl);
        Assert.Null(whileDisabled.ApiKey);
    }

    /// <summary>Minimal in-memory <see cref="ILogger"/> capturing level and formatted message.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
