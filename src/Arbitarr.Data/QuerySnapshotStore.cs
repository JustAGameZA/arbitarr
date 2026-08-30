using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data;

/// <summary>
/// EF Core-backed <see cref="IQuerySnapshotStore"/> against the
/// <see cref="QuerySnapshotCacheEntry"/> table. Kept outside Arbitarr.Core so Core stays free of
/// any reference to Arbitarr.Data (AC6); Core only defines the persistence-agnostic
/// <see cref="IQuerySnapshotStore"/> contract that this class implements. Mirrors
/// <see cref="CapsCacheStore"/>'s upsert-by-unique-key pattern.
/// </summary>
public sealed class QuerySnapshotStore : IQuerySnapshotStore
{
    private readonly ArbitarrDbContext _dbContext;

    public QuerySnapshotStore(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.QuerySnapshotCacheEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SnapshotToken == snapshotToken, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.ExpiresAt <= asOf)
        {
            return null;
        }

        return entry.PayloadJson;
    }

    public async Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        var entry = await _dbContext.QuerySnapshotCacheEntries
            .SingleOrDefaultAsync(e => e.SnapshotToken == snapshotToken, cancellationToken)
            .ConfigureAwait(false);

        var expiresAt = createdAt + ttl;

        if (entry is null)
        {
            entry = new QuerySnapshotCacheEntry
            {
                SnapshotToken = snapshotToken,
                PayloadJson = payloadJson,
                CreatedAt = createdAt,
                ExpiresAt = expiresAt,
            };
            _dbContext.QuerySnapshotCacheEntries.Add(entry);
        }
        else
        {
            entry.PayloadJson = payloadJson;
            entry.CreatedAt = createdAt;
            entry.ExpiresAt = expiresAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
