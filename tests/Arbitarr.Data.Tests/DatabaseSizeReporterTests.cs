using Arbitarr.Data.Entities;
using Arbitarr.Data.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Tests;

/// <summary>
/// M7-8: <see cref="DatabaseSizeReporter"/> reports total on-disk bytes and a per-table row
/// count against a real SQLite file, so the settings surface can show the cost of a
/// retention choice beside the setting.
/// </summary>
public sealed class DatabaseSizeReporterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-dbsize-test-{Guid.NewGuid():N}.db");

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
    public async Task ReadAsync_ReportsTotal_AndCountsRowsPerTable()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
        }

        DatabaseSizeReport before;
        using (var context = CreateContext())
        {
            before = await new DatabaseSizeReporter(context).ReadAsync(CancellationToken.None);
        }

        Assert.True(before.TotalBytes > 0);
        var table = "SuppressionAuditLogEntries";
        Assert.Equal(0, before.RowsIn(table));

        using (var context = CreateContext())
        {
            for (var i = 0; i < 400; i++)
            {
                context.SuppressionAuditLogEntries.Add(new SuppressionAuditLogEntry
                {
                    OccurredAt = DateTimeOffset.UtcNow,
                    ReleaseIdentifier = $"release-{i}",
                    QueryKey = "query",
                    RuleName = "rule",
                    Reason = new string('x', 512) + i,
                });
            }

            await context.SaveChangesAsync();
        }

        using (var context = CreateContext())
        {
            var reporter = new DatabaseSizeReporter(context);
            var after = await reporter.ReadAsync(CancellationToken.None);

            Assert.Equal(table, reporter.TableNameOf<SuppressionAuditLogEntry>());
            Assert.Equal(400, after.RowsIn(table));
            Assert.True(after.TotalBytes >= before.TotalBytes);
            Assert.Null(after.RowsIn("no-such-table"));
        }
    }
}
