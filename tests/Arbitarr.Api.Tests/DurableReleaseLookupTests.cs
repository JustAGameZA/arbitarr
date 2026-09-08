using Arbitarr.Api.Search;
using Arbitarr.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

public sealed class DurableReleaseLookupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arbitarr-proxy-guid-test-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _timeProvider = new(TestReleases.FixedPubDate);

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
        var options = new DbContextOptionsBuilder<ArbitarrDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;
        var context = new ArbitarrDbContext(options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task RecordRangeAsync_ResolvesFromANewContext_AfterInMemoryStateWouldBeLost()
    {
        var release = TestReleases.Torrent(sourceName: "eztv", guid: "durable-guid");

        using (var writerContext = CreateContext())
        {
            await new DurableReleaseLookup(writerContext, _timeProvider).RecordRangeAsync([release]);
        }

        using var readerContext = CreateContext();
        var resolved = await new DurableReleaseLookup(readerContext, _timeProvider).FindAsync(release.ProxyGuid);

        Assert.NotNull(resolved);
        Assert.Equal(release.SourceName, resolved!.SourceName);
        Assert.Equal(release.Candidate.Link, resolved.Candidate.Link);
    }

    [Fact]
    public async Task FindAsync_RejectsExpiredEntry_EvenBeforeMaintenanceRuns()
    {
        var release = TestReleases.Torrent(guid: "expired-guid");
        using var context = CreateContext();
        var lookup = new DurableReleaseLookup(context, _timeProvider);
        await lookup.RecordRangeAsync([release]);

        _timeProvider.Advance(DurableReleaseLookup.Retention);

        Assert.Null(await lookup.FindAsync(release.ProxyGuid));
    }
}