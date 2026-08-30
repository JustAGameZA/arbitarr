using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// M4-3 acceptance: <see cref="ApiKeyProfileResolver"/> maps a resolved client name to its
/// configured <see cref="FilterProfileEntry"/> via <see cref="ApiKeyProfileEntry"/> (A3); a
/// null/blank/unknown client name falls back to the default profile, same as
/// <see cref="FilterProfileLoader.LoadDefaultProfileAsync"/> — never a migration, never a rewrite
/// of size/category/guid.
/// </summary>
public sealed class ApiKeyProfileResolverTests : IDisposable
{
    private readonly string _dbPath;
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public ApiKeyProfileResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-apikeyprofileresolver-test-{Guid.NewGuid():N}.db");
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

    private static async Task<FilterProfileEntry> SeedProfileAsync(ArbitarrDbContext context, string name, bool isDefault)
    {
        var profile = new FilterProfileEntry
        {
            Name = name,
            IsDefault = isDefault,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        context.FilterProfiles.Add(profile);
        await context.SaveChangesAsync();
        return profile;
    }

    private static async Task SeedMappingAsync(ArbitarrDbContext context, string apiKeyName, long filterProfileId)
    {
        context.ApiKeyProfiles.Add(new ApiKeyProfileEntry
        {
            ApiKeyName = apiKeyName,
            FilterProfileId = filterProfileId,
            CreatedAt = Now,
        });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task ResolveAsync_KnownClientName_ReturnsItsMappedProfile()
    {
        using var context = CreateContext();
        await SeedProfileAsync(context, "Default", isDefault: true);
        var strictProfile = await SeedProfileAsync(context, "Strict", isDefault: false);
        await SeedMappingAsync(context, "sonarr-client", strictProfile.Id);

        var resolver = new ApiKeyProfileResolver(context, new FilterProfileLoader(context));
        var resolved = await resolver.ResolveAsync("sonarr-client");

        Assert.Equal("Strict", resolved.Name);
    }

    [Fact]
    public async Task ResolveAsync_NullClientName_ReturnsDefaultProfile()
    {
        using var context = CreateContext();
        await SeedProfileAsync(context, "Default", isDefault: true);
        var strictProfile = await SeedProfileAsync(context, "Strict", isDefault: false);
        await SeedMappingAsync(context, "sonarr-client", strictProfile.Id);

        var resolver = new ApiKeyProfileResolver(context, new FilterProfileLoader(context));
        var resolved = await resolver.ResolveAsync(null);

        Assert.Equal("Default", resolved.Name);
    }

    [Fact]
    public async Task ResolveAsync_BlankClientName_ReturnsDefaultProfile()
    {
        using var context = CreateContext();
        await SeedProfileAsync(context, "Default", isDefault: true);

        var resolver = new ApiKeyProfileResolver(context, new FilterProfileLoader(context));
        var resolved = await resolver.ResolveAsync("   ");

        Assert.Equal("Default", resolved.Name);
    }

    [Fact]
    public async Task ResolveAsync_UnknownClientName_ReturnsDefaultProfile()
    {
        using var context = CreateContext();
        await SeedProfileAsync(context, "Default", isDefault: true);

        var resolver = new ApiKeyProfileResolver(context, new FilterProfileLoader(context));
        var resolved = await resolver.ResolveAsync("radarr-client-not-configured");

        Assert.Equal("Default", resolved.Name);
    }
}
