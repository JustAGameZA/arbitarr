using Arbitarr.Data.Sources;
using Microsoft.Data.Sqlite;
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
    private readonly string _dbPath;

    public SourceRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-sources-test-{Guid.NewGuid():N}.db");
    }

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

    [Fact]
    public async Task Migration_applies_to_an_existing_populated_database_without_data_loss()
    {
        // Simulate an existing deployment: migrate to the state just before AddSourcesTable, write a
        // settings row (stand-in for pre-existing operator data), then migrate the rest of the way
        // (including AddSourcesTable) and confirm the earlier row survives untouched.
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");

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
