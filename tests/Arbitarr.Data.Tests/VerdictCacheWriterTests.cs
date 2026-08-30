using Arbitarr.Core.Filtering;
using Arbitarr.Data.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Tests;

/// <summary>
/// Proves the persisted <c>RewrittenTitle</c> bound is enforced in code against a real SQLite
/// database: EF's <c>HasMaxLength</c> emits no CHECK constraint on SQLite, so without the writer
/// clamp an arbitrarily long upstream title would be stored verbatim.
/// </summary>
public sealed class VerdictCacheWriterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-verdict-writer-test-{Guid.NewGuid():N}.db");

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
    public async Task PutAsync_OversizedRewrittenTitle_IsStoredTruncatedToLimit()
    {
        var oversized = new string('x', VerdictCacheLimits.MaxRewrittenTitleLength + 4096);

        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var writer = new VerdictCacheWriter(context);
            await writer.PutAsync("hash-1", "model", "digest", "v1", Verdict.Accept, 0.9, oversized);
        }

        using (var context = CreateContext())
        {
            var stored = await context.VerdictCacheEntries.SingleAsync(e => e.ReleaseKeyHash == "hash-1");
            Assert.NotNull(stored.RewrittenTitle);
            Assert.Equal(VerdictCacheLimits.MaxRewrittenTitleLength, stored.RewrittenTitle.Length);
        }
    }

    [Fact]
    public async Task PutAsync_UpdateWithoutRewrite_PreservesExistingRewrite()
    {
        using (var context = CreateContext())
        {
            context.Database.Migrate();
            var writer = new VerdictCacheWriter(context);
            await writer.PutAsync("hash-2", "model", "digest", "v1", Verdict.Accept, 0.9, "Clean Title");
            await writer.PutAsync("hash-2", "model", "digest", "v1", Verdict.Reject, 0.4);
        }

        using (var context = CreateContext())
        {
            var stored = await context.VerdictCacheEntries.SingleAsync(e => e.ReleaseKeyHash == "hash-2");
            Assert.Equal((int)Verdict.Reject, stored.Verdict);
            Assert.Equal("Clean Title", stored.RewrittenTitle);
        }
    }
}
