using System.Diagnostics;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Filtering;
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
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
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
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
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
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
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

    /// <summary>
    /// M4-3 acceptance (A3): a client mapped (via <see cref="ApiKeyProfileEntry"/>) to a non-default
    /// profile gets that profile's rules applied — a release that only the mapped profile's deny
    /// rule matches is withheld when <paramref name="clientName"/> mapped to it, but survives (no
    /// matching rule) under the default profile used when no/unmapped client name is supplied.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ClientMappedToProfile_UsesMappedProfileNotDefault()
    {
        using var context = CreateContext();

        var defaultProfile = new FilterProfileEntry
        {
            Name = "Default",
            IsDefault = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        var clientProfile = new FilterProfileEntry
        {
            Name = "ClientProfile",
            IsDefault = false,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        context.FilterProfiles.AddRange(defaultProfile, clientProfile);
        await context.SaveChangesAsync();

        context.FilterRules.Add(new FilterRuleEntry
        {
            FilterProfileId = clientProfile.Id,
            Name = "deny-cam",
            IsAllow = false,
            Pattern = "CAM",
            Precedence = 2,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();

        context.ApiKeyProfiles.Add(new ApiKeyProfileEntry
        {
            ApiKeyName = "sonarr-client",
            FilterProfileId = clientProfile.Id,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();

        await SetShadowModeAsync(context, shadowMode: false);

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        var input = new[] { DenyMatchedRelease() };

        var mappedOutput = await stage.ApplyAsync(input, "query-1", clientName: "sonarr-client");
        Assert.Empty(mappedOutput);

        var defaultOutput = await stage.ApplyAsync(input, "query-2");
        Assert.Single(defaultOutput);
    }

    /// <summary>
    /// M4-2 pipeline-level fail-open (P1, R11): a hazardous pattern that times out inside
    /// <see cref="Core.Filtering.FilterRule.Evaluate"/> must not stall <see cref="FilterStage.ApplyAsync"/>
    /// or reduce its output — the search still returns the full result set, and a benign deny rule
    /// in the same profile still applies (and is auditable) alongside the skipped hazard. The hazard
    /// pattern reuses the backreference construct from ReDoSTimeoutTests to force the
    /// backtracking-engine fallback (NonBacktracking alone would make a backreference-free
    /// "(a+)+$"-style pattern linear-time and it would never time out).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HazardousPatternTimesOut_PipelineFailsOpen_BenignRuleStillApplies()
    {
        using var context = CreateContext();

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
            Name = "hazard",
            IsAllow = false,
            Pattern = @"(a+)+\1$",
            Precedence = 1,
            Enabled = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        });
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
        await SetShadowModeAsync(context, shadowMode: false);

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        // Hits the hazard's catastrophic-backtracking input shape but does not match "CAM", so its
        // survival can only be explained by the hazard rule being skipped (fail-open), not by the
        // benign rule failing to run.
        var hazardTitle = new string('a', 40) + "!";
        var hazardousRelease = new RenderedRelease(
            "TestSource",
            new ReleaseCandidate
            {
                Title = hazardTitle,
                Guid = "guid-hazard",
                PubDate = Now,
                Size = 111,
                Link = new Uri("https://example.invalid/hazard"),
            });
        var camRelease = DenyMatchedRelease();

        var stopwatch = Stopwatch.StartNew();
        var output = await stage.ApplyAsync(new[] { hazardousRelease, camRelease }, "query-1");
        stopwatch.Stop();

        // Bound wall-clock: one hazardous rule evaluation must not exceed a small multiple of
        // FilterRule.MatchTimeout (250ms), never "hang the pipeline".
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"ApplyAsync took {stopwatch.Elapsed}, expected bounded by MatchTimeout.");

        // Fail-open: the hazardous release still survives (its rule was skipped, not enforced as a
        // deny) — the search still returns the full result set for it.
        var survivor = Assert.Single(output);
        Assert.Equal("guid-hazard", survivor.Candidate.Guid);

        // The benign deny rule still applied to the other release: enforced (shadow OFF) means it
        // is withheld from output and recorded exactly once in the audit trail — proof the pipeline
        // kept running normally after skipping the timed-out hazard, rather than aborting early.
        var auditEntries = await context.SuppressionAuditLogEntries.ToListAsync();
        var camAudit = Assert.Single(auditEntries);
        Assert.Contains("deny-cam", camAudit.RuleName);

        // No audit entry exists for the hazardous release: the skip is silent with respect to
        // enforcement (it was never treated as a suppression) but is provable here via the timing
        // bound plus survival — the pipeline did not stall or error on it.
        Assert.DoesNotContain(auditEntries, e => e.RuleName.Contains("hazard"));
    }

    /// <summary>
    /// M4 security review (MEDIUM): a raw, attacker-controlled <c>q=</c> value must not be reflected
    /// verbatim into the suppression annotation (rendered as an XML attribute in the Torznab
    /// response) — only a clamped copy is interpolated into the reason text. The audit trail's own
    /// <c>QueryKey</c> column is untouched by this clamp (separate concern, LOW finding below).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ShadowModeOn_LongQueryKey_ReasonIsClamped_AuditQueryKeyUnaffected()
    {
        using var context = CreateContext();
        await SeedDenyRuleProfileAsync(context);
        await SetShadowModeAsync(context, shadowMode: true);

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now));

        var longQuery = new string('q', 500);
        var input = new[] { DenyMatchedRelease() };
        var output = await stage.ApplyAsync(input, longQuery);

        var release = Assert.Single(output);
        Assert.NotNull(release.SuppressionAnnotation);
        // Reflected copy is clamped to 120 chars + the "…" marker.
        Assert.DoesNotContain(longQuery, release.SuppressionAnnotation);
        Assert.Contains(new string('q', 120) + "…", release.SuppressionAnnotation);

        // Audit QueryKey semantics are unaffected by the reflection clamp itself — the stored value
        // is only bounded by the separate schema max-length (512, see MigrationTests/ArbitarrDbContext).
        var auditEntry = await context.SuppressionAuditLogEntries.SingleAsync();
        Assert.Equal(longQuery, auditEntry.QueryKey);
    }

    private static async Task SetTitleNormalizationEnabledAsync(ArbitarrDbContext context, bool enabled)
    {
        context.Settings.Add(new SettingEntry
        {
            Name = nameof(Core.Settings.SettingKey.TitleNormalizationEnabled),
            Value = enabled.ToString(),
            UpdatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    private static RenderedRelease CleanRelease(string title, string guid) => new(
        "TestSource",
        new ReleaseCandidate
        {
            Title = title,
            Guid = guid,
            PubDate = Now,
            Size = 789,
            Link = new Uri("https://example.invalid/3"),
        });

    /// <summary>
    /// M5-8/AC26b: with the kill-switch OFF (the default), the render path must never apply a
    /// title rewrite even when one is cached for the release — the rendered title is byte-identical
    /// to the original.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TitleNormalizationDisabled_TitleIsByteIdentical()
    {
        using var context = CreateContext();
        var modelIdentity = new AiModelIdentity("test-model", "digest-1", "v1");
        var release = CleanRelease("Some.Movie.2026.1080p.BluRay.x264", "guid-norm-1");
        var key = VerdictCacheKey.Compute(release.Candidate, release.SourceName, modelIdentity.ModelName, modelIdentity.ModelDigest, modelIdentity.PromptVersion);
        var cacheReader = new StubVerdictCacheReader(new Dictionary<string, CachedVerdict>
        {
            [key] = new CachedVerdict(Verdict.Accept, 0.99, "Some Movie 2026 1080p BluRay x264"),
        });

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now),
            cacheReader,
            modelIdentity);

        var output = await stage.ApplyAsync(new[] { release }, "query-1");

        var rendered = Assert.Single(output);
        Assert.Equal("Some.Movie.2026.1080p.BluRay.x264", rendered.Candidate.Title);
    }

    /// <summary>
    /// M5-8/AC26b/R17: with the kill-switch ON and a cached, worker-produced rewrite present for the
    /// release, the render path applies the rewritten title while preserving
    /// <see cref="ReleaseCandidate.OriginalTitle"/> (AC26a).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TitleNormalizationEnabled_CachedRewritePresent_AppliesRewrite_PreservesOriginalTitle()
    {
        using var context = CreateContext();
        await SetTitleNormalizationEnabledAsync(context, enabled: true);
        var modelIdentity = new AiModelIdentity("test-model", "digest-1", "v1");
        const string originalTitle = "Some.Movie.2026.1080p.BluRay.x264";
        const string rewrittenTitle = "Some Movie 2026 1080p BluRay x264";
        var release = CleanRelease(originalTitle, "guid-norm-2");
        var key = VerdictCacheKey.Compute(release.Candidate, release.SourceName, modelIdentity.ModelName, modelIdentity.ModelDigest, modelIdentity.PromptVersion);
        var cacheReader = new StubVerdictCacheReader(new Dictionary<string, CachedVerdict>
        {
            [key] = new CachedVerdict(Verdict.Accept, 0.99, rewrittenTitle),
        });

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now),
            cacheReader,
            modelIdentity);

        var output = await stage.ApplyAsync(new[] { release }, "query-1");

        var rendered = Assert.Single(output);
        Assert.Equal(rewrittenTitle, rendered.Candidate.Title);
        Assert.Equal(originalTitle, rendered.Candidate.OriginalTitle);
    }

    /// <summary>
    /// M5-8/AC26b (P1 fail-open): with the kill-switch ON but no cache entry for the release (not
    /// yet classified by the background worker), the render path falls back to the original title
    /// rather than failing or blocking.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TitleNormalizationEnabled_NoCacheEntry_FallsBackToOriginalTitle()
    {
        using var context = CreateContext();
        await SetTitleNormalizationEnabledAsync(context, enabled: true);
        var modelIdentity = new AiModelIdentity("test-model", "digest-1", "v1");
        const string originalTitle = "Some.Movie.2026.1080p.BluRay.x264";
        var release = CleanRelease(originalTitle, "guid-norm-3");
        var cacheReader = new StubVerdictCacheReader(new Dictionary<string, CachedVerdict>());

        var stage = new FilterStage(
            new ApiKeyProfileResolver(context, new FilterProfileLoader(context)),
            new SettingsReader(context),
            context,
            new FakeTimeProvider(Now),
            cacheReader,
            modelIdentity);

        var output = await stage.ApplyAsync(new[] { release }, "query-1");

        var rendered = Assert.Single(output);
        Assert.Equal(originalTitle, rendered.Candidate.Title);
    }

    private sealed class StubVerdictCacheReader : IVerdictCacheReader
    {
        private readonly IReadOnlyDictionary<string, CachedVerdict> _entries;

        public StubVerdictCacheReader(IReadOnlyDictionary<string, CachedVerdict> entries) => _entries = entries;

        public CachedVerdict? TryGet(string releaseKeyHash) =>
            _entries.TryGetValue(releaseKeyHash, out var value) ? value : null;
    }
}
