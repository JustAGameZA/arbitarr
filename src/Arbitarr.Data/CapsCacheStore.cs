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
}
