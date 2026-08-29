using ArrSearcher.Data;
using ArrSearcher.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ArrSearcher.Media.Cache;

/// <summary>
/// EF Core/SQLite-backed <see cref="IMetadataCacheStore"/>, following the same hydrate-on-first-use,
/// save-after-mutation idiom established by Step 2a's <c>SourceHealthRepository</c>: a lookup reads
/// the row (or reports its absence) and a save upserts it, with no separate "does this exist" round
/// trip required by callers.
/// </summary>
public sealed class MetadataCacheStore : IMetadataCacheStore
{
    private readonly ArrSearcherDbContext _dbContext;

    public MetadataCacheStore(ArrSearcherDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<MetadataCacheLookup> GetAsync(string seriesKey, string source, CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.MetadataCacheEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SeriesKey == seriesKey && e.Source == source, cancellationToken);

        if (entry is null)
        {
            return MetadataCacheLookup.Absent();
        }

        return entry.IsNegative
            ? MetadataCacheLookup.NegativeHit(entry.SourceSnapshotVersion, entry.RefreshAfter)
            : MetadataCacheLookup.Hit(entry.PayloadJson, entry.SourceSnapshotVersion, entry.RefreshAfter);
    }

    public Task SaveAsync(
        string seriesKey,
        string source,
        string payloadJson,
        string sourceSnapshotVersion,
        DateTimeOffset refreshAfter,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(seriesKey, source, payloadJson, sourceSnapshotVersion, isNegative: false, refreshAfter, cancellationToken);

    public Task SaveNegativeAsync(
        string seriesKey,
        string source,
        string sourceSnapshotVersion,
        DateTimeOffset refreshAfter,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(seriesKey, source, payloadJson: string.Empty, sourceSnapshotVersion, isNegative: true, refreshAfter, cancellationToken);

    private async Task UpsertAsync(
        string seriesKey,
        string source,
        string payloadJson,
        string sourceSnapshotVersion,
        bool isNegative,
        DateTimeOffset refreshAfter,
        CancellationToken cancellationToken)
    {
        var entry = await _dbContext.MetadataCacheEntries
            .SingleOrDefaultAsync(e => e.SeriesKey == seriesKey && e.Source == source, cancellationToken);

        if (entry is null)
        {
            entry = new MetadataCacheEntry
            {
                SeriesKey = seriesKey,
                Source = source,
                PayloadJson = payloadJson,
                SourceSnapshotVersion = sourceSnapshotVersion,
            };
            _dbContext.MetadataCacheEntries.Add(entry);
        }

        entry.PayloadJson = payloadJson;
        entry.SourceSnapshotVersion = sourceSnapshotVersion;
        entry.IsNegative = isNegative;
        entry.FetchedAt = DateTimeOffset.UtcNow;
        entry.RefreshAfter = refreshAfter;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
