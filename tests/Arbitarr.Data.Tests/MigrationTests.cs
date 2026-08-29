using Arbitarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace Arbitarr.Data.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dbPath;

    public MigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-migration-test-{Guid.NewGuid():N}.db");
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
    public void Migrate_OnFreshTempFile_CreatesFullSchema()
    {
        Assert.False(File.Exists(_dbPath));

        using (var context = CreateContext())
        {
            context.Database.Migrate();
        }

        Assert.True(File.Exists(_dbPath));

        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        using var reader = command.ExecuteReader();

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            tableNames.Add(reader.GetString(0));
        }

        var expectedTables = new[]
        {
            nameof(ArbitarrDbContext.MetadataCacheEntries),
            nameof(ArbitarrDbContext.SearchResultCacheEntries),
            nameof(ArbitarrDbContext.QuerySnapshotCacheEntries),
            nameof(ArbitarrDbContext.CapsCacheEntries),
            nameof(ArbitarrDbContext.SourceHealthRecords),
            nameof(ArbitarrDbContext.SuppressionAuditLogEntries),
            nameof(ArbitarrDbContext.Settings),
        };

        foreach (var expectedTable in expectedTables)
        {
            Assert.Contains(expectedTable, tableNames);
        }
    }

    [Fact]
    public void Migrate_IsIdempotent_WhenAppliedTwice()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
        }

        using (var context = CreateContext())
        {
            // Should not throw when migrations are already applied.
            context.Database.Migrate();
        }

        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void Settings_RoundTrip_PersistsRow()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            context.Settings.Add(new Entities.SettingEntry
            {
                Name = "search_result_cache.fresh_until_seconds",
                Value = "900",
                Floor = "0",
                Ceiling = "1800",
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            context.SaveChanges();
        }

        using (var context = CreateContext())
        {
            var setting = context.Settings.Single(s => s.Name == "search_result_cache.fresh_until_seconds");
            Assert.Equal("900", setting.Value);
            Assert.Equal("0", setting.Floor);
            Assert.Equal("1800", setting.Ceiling);
        }
    }
}
