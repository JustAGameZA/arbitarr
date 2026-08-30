using System.Globalization;
using Arbitarr.Core.Settings;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Tests;

/// <summary>
/// M4-1 (rules survive restart), M4-3 (A3: named API keys select distinct profiles), the
/// M4-5 persistence half (every rule-driven suppression writes a <see cref="SuppressionAuditLogEntry"/>,
/// asserted by count equality), and M4-8 (fresh-install defaults for ShadowMode/AiConfidenceThreshold,
/// D3) — all against the real SQLite migration set, not an in-memory provider, so the schema itself
/// is exercised.
/// </summary>
public sealed class FilterEnginePersistenceTests : IDisposable
{
    private readonly string _dbPath;

    public FilterEnginePersistenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-filter-persist-test-{Guid.NewGuid():N}.db");
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
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    [Fact]
    public void FilterRules_SurviveRestart_RoundTripThroughSqlite()
    {
        var now = DateTimeOffset.UtcNow;

        using (var context = CreateContext())
        {
            context.Database.Migrate();

            var profile = new FilterProfileEntry
            {
                Name = "default",
                IsDefault = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.FilterProfiles.Add(profile);
            context.SaveChanges();

            context.FilterRules.Add(new FilterRuleEntry
            {
                FilterProfileId = profile.Id,
                Name = "deny-cam",
                IsAllow = false,
                Pattern = @"\bCAM\b",
                Precedence = 3,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            context.FilterRules.Add(new FilterRuleEntry
            {
                FilterProfileId = profile.Id,
                Name = "allow-remux",
                IsAllow = true,
                Pattern = @"REMUX",
                Precedence = 4,
                Enabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
            context.ApiKeyProfiles.Add(new ApiKeyProfileEntry
            {
                ApiKeyName = "sonarr-main",
                FilterProfileId = profile.Id,
                CreatedAt = now,
            });
            context.SaveChanges();
        }

        // Reopen: a fresh context/connection against the same file simulates a process restart.
        using (var context = CreateContext())
        {
            var profile = context.FilterProfiles.Single(p => p.Name == "default");
            Assert.True(profile.IsDefault);

            var rules = context.FilterRules
                .Where(r => r.FilterProfileId == profile.Id)
                .OrderBy(r => r.Name)
                .ToList();
            Assert.Equal(2, rules.Count);

            var allowRemux = rules.Single(r => r.Name == "allow-remux");
            Assert.True(allowRemux.IsAllow);
            Assert.Equal("REMUX", allowRemux.Pattern);
            Assert.Equal(4, allowRemux.Precedence);

            var denyCam = rules.Single(r => r.Name == "deny-cam");
            Assert.False(denyCam.IsAllow);
            Assert.Equal(@"\bCAM\b", denyCam.Pattern);

            var apiKeyMapping = context.ApiKeyProfiles.Single(k => k.ApiKeyName == "sonarr-main");
            Assert.Equal(profile.Id, apiKeyMapping.FilterProfileId);
        }
    }

    [Fact]
    public void ApiKeyProfileEntry_TwoNamedKeys_ResolveDistinctProfiles_WithDifferentSurvivorSets()
    {
        var now = DateTimeOffset.UtcNow;

        using var context = CreateContext();
        context.Database.Migrate();

        var strictProfile = new FilterProfileEntry { Name = "strict", CreatedAt = now, UpdatedAt = now };
        var lenientProfile = new FilterProfileEntry { Name = "lenient", IsDefault = true, CreatedAt = now, UpdatedAt = now };
        context.FilterProfiles.AddRange(strictProfile, lenientProfile);
        context.SaveChanges();

        // Strict profile denies anything tagged CAM; lenient profile has no rules at all.
        context.FilterRules.Add(new FilterRuleEntry
        {
            FilterProfileId = strictProfile.Id,
            Name = "deny-cam",
            IsAllow = false,
            Pattern = @"\bCAM\b",
            Precedence = 3,
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        });

        context.ApiKeyProfiles.Add(new ApiKeyProfileEntry { ApiKeyName = "radarr-strict", FilterProfileId = strictProfile.Id, CreatedAt = now });
        context.ApiKeyProfiles.Add(new ApiKeyProfileEntry { ApiKeyName = "radarr-lenient", FilterProfileId = lenientProfile.Id, CreatedAt = now });
        context.SaveChanges();

        var candidates = new[]
        {
            Candidate("Movie.Title.2024.CAM.x264"),
            Candidate("Movie.Title.2024.1080p.BluRay.x264"),
        };

        var strictKeyProfileId = context.ApiKeyProfiles.Single(k => k.ApiKeyName == "radarr-strict").FilterProfileId;
        var lenientKeyProfileId = context.ApiKeyProfiles.Single(k => k.ApiKeyName == "radarr-lenient").FilterProfileId;

        var strictRules = context.FilterRules.Where(r => r.FilterProfileId == strictKeyProfileId).ToList();
        var lenientRules = context.FilterRules.Where(r => r.FilterProfileId == lenientKeyProfileId).ToList();

        var strictEngine = new RuleEngine(ToProfile("strict", strictRules));
        var lenientEngine = new RuleEngine(ToProfile("lenient", lenientRules));

        var strictResult = strictEngine.Evaluate(candidates, "query-1", now);
        var lenientResult = lenientEngine.Evaluate(candidates, "query-1", now);

        Assert.Single(strictResult.Survivors);
        Assert.Equal(2, lenientResult.Survivors.Count);
        Assert.NotEqual(strictResult.Survivors.Count, lenientResult.Survivors.Count);
    }

    [Fact]
    public void SuppressionAuditLogMapper_EveryRejection_PersistsExactlyOneRow()
    {
        var now = DateTimeOffset.UtcNow;

        using var context = CreateContext();
        context.Database.Migrate();

        var profile = new FilterProfile("strict", new IFilterRule[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.Normal, @"\bCAM\b"),
        });

        var candidates = new[]
        {
            Candidate("Movie.Title.2024.CAM.x264"),
            Candidate("Movie.Title.2024.CAM.PROPER.x264"),
            Candidate("Movie.Title.2024.1080p.BluRay.x264"),
        };

        var engine = new RuleEngine(profile);
        var result = engine.Evaluate(candidates, "query-1", now);
        Assert.Equal(2, result.Suppressions.Count);

        var entries = SuppressionAuditLogMapper.ToEntries(
            result,
            queryKey: "query-1",
            shadowMode: false,
            ruleNameSelector: _ => "deny-cam");

        context.SuppressionAuditLogEntries.AddRange(entries);
        context.SaveChanges();

        using var verifyContext = CreateContext();
        var persistedCount = verifyContext.SuppressionAuditLogEntries.Count(e => e.QueryKey == "query-1");
        Assert.Equal(result.Suppressions.Count, persistedCount);
    }

    [Fact]
    public async Task SettingsReader_FreshInstallNoSeeding_ShadowModeDefaultsOn()
    {
        using var context = CreateContext();
        context.Database.Migrate();

        var reader = new SettingsReader(context);

        var shadowMode = await reader.GetShadowModeAsync();

        Assert.True(shadowMode);
    }

    [Fact]
    public async Task SettingsReader_FreshInstallNoSeeding_AiConfidenceThresholdDefaultsToPointNine()
    {
        using var context = CreateContext();
        context.Database.Migrate();

        var reader = new SettingsReader(context);

        var threshold = await reader.GetAiConfidenceThresholdAsync();

        Assert.Equal(0.9, threshold);
    }

    /// <summary>
    /// Behaviour lock for every <see cref="SettingsReader"/> key: a well-formed stored row is
    /// honoured. Two of the five keys were previously covered only on the no-row default path, so
    /// the stored-value branch went unverified.
    /// </summary>
    [Theory]
    [InlineData(SettingKey.ShadowMode, "false")]
    [InlineData(SettingKey.AiConfidenceThreshold, "0.55")]
    [InlineData(SettingKey.TitleNormalizationEnabled, "true")]
    [InlineData(SettingKey.ClassifierPollInterval, "00:02:30")]
    [InlineData(SettingKey.SyncArbitrationBudget, "00:00:07")]
    public async Task SettingsReader_StoredValue_IsHonoured(SettingKey key, string stored)
    {
        using var context = CreateContext();
        context.Database.Migrate();
        context.Settings.Add(new SettingEntry { Name = key.ToString(), Value = stored });
        await context.SaveChangesAsync();

        var reader = new SettingsReader(context);

        Assert.Equal(stored, Stringify(await ReadAsync(reader, key)));
    }

    /// <summary>
    /// Behaviour lock: an unparseable stored value falls back to the catalog default rather than
    /// throwing. This is the branch a shared parse helper must preserve for all five keys.
    /// </summary>
    [Theory]
    [InlineData(SettingKey.ShadowMode)]
    [InlineData(SettingKey.AiConfidenceThreshold)]
    [InlineData(SettingKey.TitleNormalizationEnabled)]
    [InlineData(SettingKey.ClassifierPollInterval)]
    [InlineData(SettingKey.SyncArbitrationBudget)]
    public async Task SettingsReader_MalformedStoredValue_FallsBackToCatalogDefault(SettingKey key)
    {
        using var context = CreateContext();
        context.Database.Migrate();
        context.Settings.Add(new SettingEntry { Name = key.ToString(), Value = "not-a-valid-value" });
        await context.SaveChangesAsync();

        var reader = new SettingsReader(context);

        Assert.Equal(SettingsCatalog.GetDefault(key), await ReadAsync(reader, key));
    }

    private static async Task<object> ReadAsync(SettingsReader reader, SettingKey key) => key switch
    {
        SettingKey.ShadowMode => await reader.GetShadowModeAsync(),
        SettingKey.AiConfidenceThreshold => await reader.GetAiConfidenceThresholdAsync(),
        SettingKey.TitleNormalizationEnabled => await reader.GetTitleNormalizationEnabledAsync(),
        SettingKey.ClassifierPollInterval => await reader.GetClassifierPollIntervalAsync(),
        SettingKey.SyncArbitrationBudget => await reader.GetSyncArbitrationBudgetAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "key is not served by SettingsReader"),
    };

    /// <summary>Renders a read value the same way the reader's own parsers accept it.</summary>
    private static string Stringify(object value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    private static FilterProfile ToProfile(string name, List<FilterRuleEntry> entries)
    {
        var rules = entries.Select(e => (IFilterRule)new FilterRule(e.Name, e.IsAllow, (Precedence)e.Precedence, e.Pattern));
        return new FilterProfile(name, rules);
    }

    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = Guid.NewGuid().ToString(),
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/release"),
    };
}
