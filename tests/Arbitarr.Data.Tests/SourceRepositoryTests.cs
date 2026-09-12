using Arbitarr.Data.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #53 stage 53a: proves the migration is lossless against an existing populated database and that
/// <see cref="SourceRepository"/> validates at the repository boundary (AC24 — reject, never clamp)
/// exactly as <see cref="SettingsRepositoryTests"/> does for <c>SettingsRepository</c>. Stage 53a
/// ships no read path, so there is nothing here proving sources are *used* — only that they can be
/// written safely. That is deliberate (plan §4, item 4).
/// </summary>
public sealed class SourceRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arr-searcher-sources-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task Migration_applies_to_an_existing_populated_database_without_data_loss()
    {
        // Simulate an existing deployment: migrate to the state just before AddSourcesTable, write a
        // settings row (stand-in for pre-existing operator data), then migrate the rest of the way
        // (including AddSourcesTable) and confirm the earlier row survives untouched.
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            var migrator = context.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("VerdictCacheEntryRewrittenTitle");

            context.Settings.Add(new Entities.SettingEntry
            {
                Name = "pre_existing_setting",
                Value = "keep-me",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            await context.Database.MigrateAsync();

            var preserved = await context.Settings.SingleAsync(e => e.Name == "pre_existing_setting");
            Assert.Equal("keep-me", preserved.Value);

            // The new table exists and is queryable (empty, as expected — nothing writes to it automatically).
            var sourceCount = await context.Sources.CountAsync();
            Assert.Equal(0, sourceCount);
        }
    }

    [Fact]
    public async Task AddAsync_persists_a_valid_source_without_storing_the_key_on_the_entity()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Primary NZBHydra",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: "REDACTED-test-value-1",
            enabled: true,
            CancellationToken.None);

        Assert.True(source.Id > 0);

        var all = await repository.GetAllAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal("Primary NZBHydra", all[0].DisplayName);

        Assert.True(await repository.HasApiKeyAsync(source.Id, CancellationToken.None));

        // The key is never a property of Source — confirm it landed write-only in Settings instead.
        var settingRow = await context.Settings.SingleAsync(e => e.Name == SourceRepository.ApiKeySettingName(source.Id));
        Assert.Equal("REDACTED-test-value-1", settingRow.Value);
    }

    [Fact]
    public async Task AddAsync_rejects_a_malformed_base_url_and_persists_nothing()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Bad URL Source",
            baseUrl: "not-a-url",
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("ftp://192.0.2.21")]
    [InlineData("192.0.2.21:5076")]
    [InlineData("")]
    public async Task AddAsync_rejects_non_http_schemes_and_relative_urls(string baseUrl)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Scheme Test",
            baseUrl: baseUrl,
            apiKey: null,
            enabled: true,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("http://nzbhydra2.example.invalid:5076")]
    [InlineData("http://invalid:5076")]
    public async Task AddAsync_rejects_a_dot_invalid_host_and_persists_nothing(string baseUrl)
    {
        // arb-c29: an RFC 2606 .invalid host can never resolve, so seeding or storing one degrades a
        // source to a permanently unreachable upstream (this is docker-compose.yml's own placeholder).
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Invalid Host Source",
            baseUrl: baseUrl,
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// Positive control for the .invalid rejection above: a normal, resolvable-looking address is
    /// still accepted, so the new check rejects only the reserved domain rather than every address.
    /// </summary>
    [Fact]
    public async Task AddAsync_still_accepts_a_normal_base_url()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Normal Source",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        Assert.True(source.Id > 0);
        Assert.Single(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddAsync_rejects_a_duplicate_display_name_case_insensitively()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Primary NZBHydra",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: "NzbHydra",
            displayName: "primary nzbhydra",
            baseUrl: "http://192.0.2.22:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        Assert.Single(await repository.GetAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UpdateAsync_replaces_fields_and_optionally_rotates_the_key()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Primary NZBHydra",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: "REDACTED-test-value-old",
            enabled: true,
            CancellationToken.None);

        var updated = await repository.UpdateAsync(
            source.Id,
            kind: "NzbHydra",
            displayName: "Renamed NZBHydra",
            baseUrl: "http://192.0.2.31:5076",
            apiKey: "REDACTED-test-value-new",
            enabled: false,
            CancellationToken.None);

        Assert.Equal("Renamed NZBHydra", updated.DisplayName);
        Assert.Equal("http://192.0.2.31:5076", updated.BaseUrl);
        Assert.False(updated.Enabled);

        var settingRow = await context.Settings.SingleAsync(e => e.Name == SourceRepository.ApiKeySettingName(source.Id));
        Assert.Equal("REDACTED-test-value-new", settingRow.Value);
    }

    [Fact]
    public async Task UpdateAsync_without_a_new_key_leaves_the_stored_secret_untouched()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "Primary NZBHydra",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: "REDACTED-test-value-old",
            enabled: true,
            CancellationToken.None);

        await repository.UpdateAsync(
            source.Id,
            kind: "NzbHydra",
            displayName: "Primary NZBHydra",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        var settingRow = await context.Settings.SingleAsync(e => e.Name == SourceRepository.ApiKeySettingName(source.Id));
        Assert.Equal("REDACTED-test-value-old", settingRow.Value);
    }

    [Fact]
    public async Task UpdateAsync_rejects_an_unknown_id()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.UpdateAsync(
            id: 999,
            kind: "NzbHydra",
            displayName: "Nope",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None));
    }

    /// <summary>
    /// arb-x7w8.1: each of the three kinds round-trips under its OWN name. Asserted per kind via a
    /// Theory rather than by writing three rows and checking "all three kinds are present", because
    /// the latter passes if a resolver ever normalises one kind into another — the per-kind assertion
    /// is what pins that the value read back is the value written.
    /// </summary>
    [Theory]
    [InlineData(SourceRepository.NzbHydraKind)]
    [InlineData(SourceRepository.NewznabKind)]
    [InlineData(SourceRepository.TorznabKind)]
    public async Task AddAsync_round_trips_each_known_kind_under_its_own_name(string kind)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: kind,
            displayName: $"Round trip {kind}",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        var stored = await repository.GetAsync(source.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(kind, stored!.Kind);
    }

    /// <summary>
    /// The casing guard (arb-pn5) now has to hold for the two kinds added in arb-x7w8.1 as well.
    /// Asserted per casing variant, not "some variant is rejected": a fix that special-cased only
    /// lowercase would leave the uppercase spelling stored and silently never matched by the
    /// resolver's ordinal comparison — the exact original bug, reachable through the new kinds.
    /// </summary>
    [Theory]
    [InlineData("newznab")]
    [InlineData("NEWZNAB")]
    [InlineData("torznab")]
    [InlineData("TORZNAB")]
    public async Task AddAsync_rejects_a_wrongly_cased_new_kind_and_persists_nothing(string wrongCasing)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: wrongCasing,
            displayName: "Bad casing " + wrongCasing,
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// The load-bearing assertion of arb-x7w8.1: <c>null</c> (unlimited) and <c>0</c> (a cap of
    /// zero) are two different stored states and must round-trip as two different stored states.
    ///
    /// <para>Asserted PER ROW — one source written with null limits and one written with 0 — and
    /// then asserted that the two differ. "Some row has null" would pass against an implementation
    /// that collapsed every limit to one value; comparing the two rows to each other is what makes
    /// the collapse detectable in either direction. Collapsing null to 0 silently disables an
    /// unlimited indexer; collapsing 0 to null lets a limited one run free past its cap.</para>
    /// </summary>
    [Fact]
    public async Task Null_and_zero_limits_round_trip_as_distinct_states()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var unlimited = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Unlimited indexer",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions
            {
                QueryLimit = null,
                SetQueryLimit = true,
                GrabLimit = null,
                SetGrabLimit = true,
            });

        var capped = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Zero-capped indexer",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions
            {
                QueryLimit = 0,
                SetQueryLimit = true,
                GrabLimit = 0,
                SetGrabLimit = true,
            });

        // Re-read from the database rather than trusting the tracked entities, so this exercises the
        // stored representation — the place a collapse would actually happen.
        context.ChangeTracker.Clear();

        var storedUnlimited = await repository.GetAsync(unlimited.Id, CancellationToken.None);
        var storedCapped = await repository.GetAsync(capped.Id, CancellationToken.None);
        Assert.NotNull(storedUnlimited);
        Assert.NotNull(storedCapped);

        // Per row, written value read back unchanged.
        Assert.Null(storedUnlimited!.QueryLimit);
        Assert.Null(storedUnlimited.GrabLimit);
        Assert.Equal(0, storedCapped!.QueryLimit);
        Assert.Equal(0, storedCapped.GrabLimit);

        // And the two states are observably different from each other, in both directions.
        Assert.NotEqual(storedUnlimited.QueryLimit, storedCapped.QueryLimit);
        Assert.NotEqual(storedUnlimited.GrabLimit, storedCapped.GrabLimit);
    }

    /// <summary>
    /// The remaining arb-x7w8.1 columns round-trip, and an omitted <see cref="SourceOptions"/> takes
    /// the documented entity defaults rather than zero/empty — notably <c>NzbAccessMode "Proxy"</c>,
    /// the mode that does NOT expose the indexer key to the client.
    /// </summary>
    [Fact]
    public async Task A_source_added_without_options_takes_the_documented_defaults()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.TorznabKind,
            displayName: "Defaulted indexer",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        context.ChangeTracker.Clear();
        var stored = await repository.GetAsync(source.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("/api", stored!.ApiPath);
        Assert.Equal(0, stored.Priority);
        Assert.Null(stored.TimeoutSeconds);
        Assert.Null(stored.QueryLimit);
        Assert.Null(stored.GrabLimit);
        Assert.Equal("Day", stored.LimitsUnit);
        Assert.Equal("Proxy", stored.NzbAccessMode);
    }

    [Fact]
    public async Task Per_indexer_columns_round_trip_when_supplied()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Tuned indexer",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions
            {
                ApiPath = "/api/v2.0/indexers/all/results/torznab",
                Priority = 25,
                TimeoutSeconds = 45,
                SetTimeoutSeconds = true,
                QueryLimit = 100,
                SetQueryLimit = true,
                GrabLimit = 10,
                SetGrabLimit = true,
                LimitsUnit = "Hour",
                // Proxy is the only accepted mode until arb-x7w8.14 — see
                // Redirect_access_mode_is_rejected_until_arb_x7w8_14_ships_its_warning.
                NzbAccessMode = SourceRepository.ProxyAccessMode,
            });

        context.ChangeTracker.Clear();
        var stored = await repository.GetAsync(source.Id, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal("/api/v2.0/indexers/all/results/torznab", stored!.ApiPath);
        Assert.Equal(25, stored.Priority);
        Assert.Equal(45, stored.TimeoutSeconds);
        Assert.Equal(100, stored.QueryLimit);
        Assert.Equal(10, stored.GrabLimit);
        Assert.Equal("Hour", stored.LimitsUnit);
        Assert.Equal(SourceRepository.ProxyAccessMode, stored.NzbAccessMode);
    }

    /// <summary>
    /// Closed string sets are matched by exact ordinal name, per CLAUDE.md §3. Asserted per rejected
    /// value: a case-insensitive accept would store <c>"day"</c>, which the ordinal comparison that
    /// picks the rolling window would then never match, leaving the limits silently unenforced.
    /// </summary>
    [Theory]
    [InlineData("day")]
    [InlineData("DAY")]
    [InlineData("hour")]
    [InlineData("Week")]
    [InlineData(" Day ")]
    // CLAUDE.md §3's numeric-form trap, asserted for a closed string set. An Enum.TryParse-style
    // reader accepts the ordinal ("1"), and neither Enum.IsDefined nor trimming closes it, since
    // 1 IS defined and "+1" parses too. Matching by name closes it by construction; these pin that.
    [InlineData("1")]
    [InlineData("+1")]
    public async Task AddAsync_rejects_a_limits_unit_outside_the_known_set(string limitsUnit)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Bad unit " + limitsUnit,
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { LimitsUnit = limitsUnit }));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// Same exact-match posture for the access mode, where it matters most: this value decides
    /// whether the indexer key is exposed to the client, so a leniently-matched variant would change
    /// a security posture rather than a preference.
    /// </summary>
    [Theory]
    [InlineData("proxy")]
    [InlineData("PROXY")]
    [InlineData("redirect")]
    [InlineData("REDIRECT")]
    [InlineData("Passthrough")]
    // The numeric forms, per CLAUDE.md §3: "1" is exactly how an Enum.TryParse-style reader would
    // mint the second member of a two-value set — here, the key-exposing one.
    [InlineData("1")]
    [InlineData("+1")]
    public async Task AddAsync_rejects_an_nzb_access_mode_outside_the_known_set(string accessMode)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Bad mode " + accessMode,
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { NzbAccessMode = accessMode }));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// The correctly-spelled <c>"Redirect"</c> is rejected too, and that is the POINT rather than an
    /// oversight: Redirect ships OFF until arb-x7w8.14 brings the Settings UI warning that the key is
    /// exposed to the client in that mode. Leaving it out of <c>KnownNzbAccessModes</c> makes the
    /// owner's ship-OFF ruling a mechanism — a write is a 400 by construction — rather than a note
    /// somebody has to remember, the same posture <c>ValidateBaseUrl</c> takes toward <c>.invalid</c>.
    ///
    /// <para>Asserted for both write paths, since either one storing it would produce a source that
    /// exposes its key. When arb-x7w8.14 admits the value, THIS test is what must be changed
    /// deliberately — which is exactly the review step the omission is there to force.</para>
    /// </summary>
    [Fact]
    public async Task Redirect_access_mode_is_rejected_until_arb_x7w8_14_ships_its_warning()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        // It is not in the accepted set, by construction.
        Assert.DoesNotContain(SourceRepository.RedirectAccessMode, SourceRepository.KnownNzbAccessModes);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Redirect on create",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { NzbAccessMode = SourceRepository.RedirectAccessMode }));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));

        // And the update path cannot smuggle it in either: a source created legitimately as Proxy
        // must not be rewritable to Redirect while the warning does not exist.
        var existing = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Proxy source",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.UpdateAsync(
            existing.Id,
            kind: SourceRepository.NewznabKind,
            displayName: "Proxy source",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { NzbAccessMode = SourceRepository.RedirectAccessMode }));

        context.ChangeTracker.Clear();
        var unchanged = await repository.GetAsync(existing.Id, CancellationToken.None);
        Assert.Equal(SourceRepository.ProxyAccessMode, unchanged!.NzbAccessMode);
    }

    /// <summary>
    /// The unpinned half of the Set/Clear design: that an update carrying NO opinion about the
    /// tuning columns leaves every one of them exactly as stored.
    ///
    /// <para>Asserted PER FIELD, and for both ways a caller can express "no opinion" — an all-null
    /// <see cref="SourceOptions"/> and a null <see cref="SourceOptions"/>. Without this, an
    /// implementation that reset a column to its default on every update would pass the whole suite:
    /// the create-path tests would still see the right values, because on create the default IS the
    /// right value. Only an update over a non-default row can tell the two apart.</para>
    /// </summary>
    [Fact]
    public async Task UpdateAsync_without_an_opinion_leaves_every_tuning_column_unchanged()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Fully tuned",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions
            {
                ApiPath = "/api/v2.0/indexers/example/results/torznab",
                Priority = 40,
                TimeoutSeconds = 90,
                SetTimeoutSeconds = true,
                QueryLimit = 250,
                SetQueryLimit = true,
                GrabLimit = 25,
                SetGrabLimit = true,
                LimitsUnit = "Hour",
                NzbAccessMode = SourceRepository.ProxyAccessMode,
            });

        // (a) An all-null options object: every Set/Clear flag false, every value null.
        await repository.UpdateAsync(
            source.Id,
            kind: SourceRepository.NewznabKind,
            displayName: "Fully tuned",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions());

        context.ChangeTracker.Clear();
        AssertStillTuned(await repository.GetAsync(source.Id, CancellationToken.None));

        // (b) No options object at all — the shape every pre-existing caller uses.
        await repository.UpdateAsync(
            source.Id,
            kind: SourceRepository.NewznabKind,
            displayName: "Fully tuned",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            options: null);

        context.ChangeTracker.Clear();
        AssertStillTuned(await repository.GetAsync(source.Id, CancellationToken.None));

        static void AssertStillTuned(Entities.Source? stored)
        {
            Assert.NotNull(stored);
            Assert.Equal("/api/v2.0/indexers/example/results/torznab", stored!.ApiPath);
            Assert.Equal(40, stored.Priority);
            Assert.Equal(90, stored.TimeoutSeconds);
            Assert.Equal(250, stored.QueryLimit);
            Assert.Equal(25, stored.GrabLimit);
            Assert.Equal("Hour", stored.LimitsUnit);
            Assert.Equal(SourceRepository.ProxyAccessMode, stored.NzbAccessMode);
        }
    }

    /// <summary>
    /// The three update outcomes for a nullable limit must be three DIFFERENT outcomes: clear it to
    /// null (unlimited), set it to 0 (a cap of zero), or leave it alone. Asserted against each other
    /// rather than only against expected constants, so an implementation that collapsed any pair
    /// into one is caught — which is the whole reason the Clear flags exist rather than a plain
    /// null-check.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_distinguishes_clearing_a_limit_from_zeroing_it_and_from_leaving_it()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        async Task<Entities.Source> TunedAsync(string name)
        {
            var created = await repository.AddAsync(
                kind: SourceRepository.NewznabKind,
                displayName: name,
                baseUrl: "http://indexer.example/",
                apiKey: null,
                enabled: true,
                CancellationToken.None,
                new SourceOptions { QueryLimit = 500, SetQueryLimit = true });

            Assert.Equal(500, created.QueryLimit);
            return created;
        }

        Task UpdateAsync(Entities.Source s, SourceOptions options) => repository.UpdateAsync(
            s.Id,
            kind: SourceRepository.NewznabKind,
            displayName: s.DisplayName,
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            options);

        var cleared = await TunedAsync("Cleared limit");
        var zeroed = await TunedAsync("Zeroed limit");
        var left = await TunedAsync("Untouched limit");

        // At this layer "clear" is SetQueryLimit with a null value; the endpoint's ClearQueryLimit
        // flag is the wire spelling of exactly this, since JSON cannot distinguish an omitted field
        // from an explicit null.
        await UpdateAsync(cleared, new SourceOptions { SetQueryLimit = true, QueryLimit = null });
        await UpdateAsync(zeroed, new SourceOptions { QueryLimit = 0, SetQueryLimit = true });
        await UpdateAsync(left, new SourceOptions());

        context.ChangeTracker.Clear();
        var storedCleared = await repository.GetAsync(cleared.Id, CancellationToken.None);
        var storedZeroed = await repository.GetAsync(zeroed.Id, CancellationToken.None);
        var storedLeft = await repository.GetAsync(left.Id, CancellationToken.None);

        Assert.Null(storedCleared!.QueryLimit);
        Assert.Equal(0, storedZeroed!.QueryLimit);
        Assert.Equal(500, storedLeft!.QueryLimit);

        // The three outcomes are pairwise different — no pair collapsed into one.
        Assert.NotEqual(storedCleared.QueryLimit, storedZeroed.QueryLimit);
        Assert.NotEqual(storedCleared.QueryLimit, storedLeft.QueryLimit);
        Assert.NotEqual(storedZeroed.QueryLimit, storedLeft.QueryLimit);
    }

    /// <summary>
    /// The timeout is the third nullable column and now carries the same Clear affordance: null
    /// means "fall back to the global default", a state an operator must be able to return to after
    /// setting an override.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_can_clear_a_timeout_back_to_the_global_default()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Timeout override",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { TimeoutSeconds = 30, SetTimeoutSeconds = true });

        Assert.Equal(30, source.TimeoutSeconds);

        await repository.UpdateAsync(
            source.Id,
            kind: SourceRepository.NewznabKind,
            displayName: "Timeout override",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { SetTimeoutSeconds = true, TimeoutSeconds = null });

        context.ChangeTracker.Clear();
        var stored = await repository.GetAsync(source.Id, CancellationToken.None);
        Assert.Null(stored!.TimeoutSeconds);
    }

    /// <summary>
    /// An API path may not be blank, carry a query string, or embed a key. The last is the one that
    /// matters: a key pasted here would be a second, READABLE home for a secret whose whole design
    /// is to live write-only in a Settings row, and it would ride into every backup and every
    /// response that projects a source.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/api?t=search")]
    [InlineData("/api?apikey=placeholder-not-a-real-key")]
    [InlineData("/api&apikey=placeholder-not-a-real-key")]
    [InlineData("/api&APIKEY=placeholder-not-a-real-key")]
    public async Task AddAsync_rejects_a_malformed_api_path(string apiPath)
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        await Assert.ThrowsAsync<SourceValidationException>(() => repository.AddAsync(
            kind: SourceRepository.NewznabKind,
            displayName: "Bad path " + Guid.NewGuid().ToString("N"),
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { ApiPath = apiPath }));

        Assert.Empty(await repository.GetAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// Positive control for the rejections above: a realistic non-default path is still accepted, so
    /// those theories are rejecting the shapes they name rather than every path.
    /// </summary>
    [Fact]
    public async Task AddAsync_still_accepts_a_normal_non_default_api_path()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: SourceRepository.TorznabKind,
            displayName: "Jackett-style path",
            baseUrl: "http://indexer.example/",
            apiKey: null,
            enabled: true,
            CancellationToken.None,
            new SourceOptions { ApiPath = "/api/v2.0/indexers/example/results/torznab" });

        Assert.True(source.Id > 0);
    }

    /// <summary>
    /// arb-x7w8.1 migration, up and down. Up is exercised against a database populated at the
    /// PREVIOUS schema (a source row written before the columns existed), which is the case that can
    /// actually fail: a NOT NULL column added without a default cannot be applied to an existing
    /// row. The existing row must come back carrying the documented defaults — above all
    /// <c>NzbAccessMode "Proxy"</c>, so an upgrade never silently converts a configured source into
    /// one that exposes its key.
    /// </summary>
    [Fact]
    public async Task The_per_indexer_columns_migration_applies_to_an_existing_source_row()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            var migrator = context.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("AddDownloadRefusalTable");

            // Written through raw SQL: at this migration the CLR entity's new properties have no
            // columns to map to, so the DbSet cannot be used to create a genuine pre-upgrade row.
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO Sources (Kind, DisplayName, BaseUrl, Enabled, CreatedAt, UpdatedAt)
                VALUES ('NzbHydra', 'Pre-upgrade source', 'http://indexer.example/', 1, '2026-01-01 00:00:00+00:00', '2026-01-01 00:00:00+00:00');
                """);
        }

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            await context.Database.MigrateAsync();

            var upgraded = await context.Sources.SingleAsync(s => s.DisplayName == "Pre-upgrade source");

            // The pre-existing data survived...
            Assert.Equal("NzbHydra", upgraded.Kind);
            Assert.Equal("http://indexer.example/", upgraded.BaseUrl);

            // ...and the new columns carry their defaults rather than NULL.
            Assert.Equal("/api", upgraded.ApiPath);
            Assert.Equal(0, upgraded.Priority);
            Assert.Equal("Day", upgraded.LimitsUnit);
            Assert.Equal("Proxy", upgraded.NzbAccessMode);

            // The nullable limits stay NULL (unlimited) — a default here would have destroyed the
            // null-is-not-zero distinction on every row that predates the column.
            Assert.Null(upgraded.TimeoutSeconds);
            Assert.Null(upgraded.QueryLimit);
            Assert.Null(upgraded.GrabLimit);
        }
    }

    /// <summary>
    /// The same migration rolls back cleanly: after migrating DOWN to the previous migration the
    /// seven columns are gone and the pre-existing source row is still there. Asserted by reading
    /// the SQLite schema directly rather than through the CLR entity, which still declares the
    /// properties regardless of what the database holds.
    /// </summary>
    [Fact]
    public async Task The_per_indexer_columns_migration_rolls_back_cleanly()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            await context.Database.MigrateAsync();

            context.Sources.Add(new Entities.Source
            {
                Kind = SourceRepository.NewznabKind,
                DisplayName = "Survives rollback",
                BaseUrl = "http://indexer.example/",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var addedColumns = new[]
        {
            "ApiPath", "Priority", "TimeoutSeconds", "QueryLimit", "GrabLimit", "LimitsUnit", "NzbAccessMode",
        };

        // Positive control: the columns really are present before the rollback, so the "gone"
        // assertion below is proven capable of failing rather than passing against a schema that
        // never had them.
        foreach (var column in addedColumns)
        {
            Assert.Contains(column, await ReadSourcesColumnsAsync());
        }

        using (var context = new ArbitarrDbContext(optionsBuilder.Options))
        {
            var migrator = context.GetInfrastructure()
                .GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("AddDownloadRefusalTable");
        }

        var afterRollback = await ReadSourcesColumnsAsync();
        foreach (var column in addedColumns)
        {
            Assert.DoesNotContain(column, afterRollback);
        }

        // The rollback dropped columns, not rows.
        Assert.Contains("DisplayName", afterRollback);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Sources WHERE DisplayName = 'Survives rollback';";
        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    private async Task<List<string>> ReadSourcesColumnsAsync()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_database.ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Sources');";

        var columns = new List<string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    [Fact]
    public async Task HasApiKeyAsync_is_false_when_no_key_was_ever_set()
    {
        using var context = CreateContext();
        var repository = new SourceRepository(context);

        var source = await repository.AddAsync(
            kind: "NzbHydra",
            displayName: "No Key Source",
            baseUrl: "http://192.0.2.21:5076",
            apiKey: null,
            enabled: true,
            CancellationToken.None);

        Assert.False(await repository.HasApiKeyAsync(source.Id, CancellationToken.None));
    }
}
