using System.Text.Json;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data;

/// <summary>
/// EF Core-backed <see cref="ICapsCacheStore"/> against the <see cref="CapsCacheEntry"/> table.
/// Kept outside Arbitarr.Core so Core stays free of any reference to Arbitarr.Data (AC6); Core
/// only defines the persistence-agnostic <see cref="ICapsCacheStore"/> contract that this class
/// implements.
/// </summary>
public sealed class CapsCacheStore : ICapsCacheStore
{
    private readonly ArbitarrDbContext _dbContext;

    public CapsCacheStore(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.CapsCacheEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.SourceName == sourceName, cancellationToken)
            .ConfigureAwait(false);

        return entry is null ? null : JsonSerializer.Deserialize<SourceCaps>(entry.PayloadJson);
    }

    public async Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caps);

        var entry = await _dbContext.CapsCacheEntries
            .SingleOrDefaultAsync(e => e.SourceName == sourceName, cancellationToken)
            .ConfigureAwait(false);

        var payloadJson = JsonSerializer.Serialize(caps);

        if (entry is null)
        {
            entry = new CapsCacheEntry
            {
                SourceName = sourceName,
                PayloadJson = payloadJson,
                FetchedAt = DateTimeOffset.UtcNow,
                IsStale = false,
            };
            _dbContext.CapsCacheEntries.Add(entry);
        }
        else
        {
            entry.PayloadJson = payloadJson;
            entry.FetchedAt = DateTimeOffset.UtcNow;
            entry.IsStale = false;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes both protocol families' rows for one source name. The keys are built through
    /// <see cref="CapsAggregator.CacheKey"/> rather than by matching a name PREFIX, because a prefix
    /// match over the <c>{name}#{protocol}</c> convention would also sweep away the entries of any
    /// other source whose name merely starts with this one's — deleting "Hydra" would take "Hydra
    /// (backup)" with it. Enumerating the protocols keeps the delete set exactly the set
    /// <see cref="CapsRefresher"/> writes.
    ///
    /// <para>Rows absent from the table are simply not there to remove; EF issues no statement for
    /// them and this reports no error, per the interface contract.</para>
    /// </summary>
    public async Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        var keys = CapsAggregator.AllProtocols
            .Select(protocol => CapsAggregator.CacheKey(sourceName, protocol))
            .ToArray();

        var entries = await _dbContext.CapsCacheEntries
            .Where(e => keys.Contains(e.SourceName))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return;
        }

        _dbContext.CapsCacheEntries.RemoveRange(entries);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
