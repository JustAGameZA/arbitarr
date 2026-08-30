using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// M4-7 acceptance: with <c>ShadowMode</c> at its default (ON), a deny-matched release is present
/// in <see cref="FilterStage.ApplyAsync"/>'s output and carries a suppression annotation; with
/// shadow mode OFF, the same release is absent. Both directions are asserted against a real
/// SQLite-backed <see cref="ArbitarrDbContext"/> (default filter profile + rule rows + the
/// ShadowMode setting row), matching this repo's persistence-test convention.
/// </summary>
public sealed class FilterStageTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public FilterStageTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-filterstage-test-{Guid.NewGuid():N}.db");
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

    private static async Task SeedDenyRuleProfileAsync(ArbitarrDbContext context)
    {
        var profile = new FilterProfileEntry
        {
            Name = "Default",
            IsDefault = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        context.FilterProfiles.Add(profile);
        await context.SaveChangesAsync();

        context.FilterRules.Add(new FilterRuleEntry
        {
            FilterProfileId = profile.Id,
            Name = "deny-cam",
            IsAllow = false,
            Pattern = "CAM",
            Precedence = 2,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private static async Task SetShadowModeAsync(ArbitarrDbContext context, bool shadowMode)
    {
        context.Settings.Add(new SettingEntry
        {
            Name = nameof(Core.Settings.SettingKey.ShadowMode),
            Value = shadowMode.ToString(),
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private static RenderedRelease DenyMatchedRelease() => new(
        "TestSource",
        new ReleaseCandidate
        {
            Title = "Some.Movie.2026.CAM.x264",
            Guid = "guid-1",
            PubDate = Now,
            Size = 123,
            Link = new Uri("https://example.invalid/1"),
        });

    [Fact]
    public async Task ApplyAsync_ShadowModeOn_ReleaseIsPresentAndAnnotated()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: true);

        var stage = new FilterStage(
            new FilterProfileLoader(context),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        var input = new[] { DenyMatchedRelease() };
        var output = await stage.ApplyAsync(input, "query-1");

        var release = Assert.Single(output);
        Assert.NotNull(release.SuppressionAnnotation);
        Assert.Equal(1, await context.SuppressionAuditLogEntries.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_ShadowModeOff_ReleaseIsAbsent()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: false);

        var stage = new FilterStage(
            new FilterProfileLoader(context),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        var input = new[] { DenyMatchedRelease() };
        var output = await stage.ApplyAsync(input, "query-1");

        Assert.Empty(output);
        Assert.Equal(1, await context.SuppressionAuditLogEntries.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_NoRuleMatches_ReleasePassesThroughUnannotated()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: true);

        var stage = new FilterStage(
            new FilterProfileLoader(context),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        var clean = new RenderedRelease(
            "TestSource",
            new ReleaseCandidate
            {
                Title = "Some.Movie.2026.1080p.BluRay.x264",
                Guid = "guid-2",
                PubDate = Now,
                Size = 456,
                Link = new Uri("https://example.invalid/2"),
            });

        var output = await stage.ApplyAsync(new[] { clean }, "query-1");

        var release = Assert.Single(output);
        Assert.Null(release.SuppressionAnnotation);
        Assert.Equal(0, await context.SuppressionAuditLogEntries.CountAsync());
    }
}
