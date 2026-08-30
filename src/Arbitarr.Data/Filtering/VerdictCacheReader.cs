using Arbitarr.Core.Filtering;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Filtering;

/// <summary>
/// EF Core-backed <see cref="IVerdictCacheReader"/> against the <see cref="Entities.VerdictCacheEntry"/>
/// table. Deliberately synchronous (Q1-B): <see cref="SuppressionPrecedenceChain"/>'s AI slot must
/// never await/block the request path on anything resembling a live model call, so this reader
/// issues a genuine synchronous EF query rather than blocking on an async one. Kept outside
/// Arbitarr.Core so Core stays free of any reference to Arbitarr.Data (AC6); Core only defines the
/// persistence-agnostic <see cref="IVerdictCacheReader"/> contract that this class implements.
/// </summary>
public sealed class VerdictCacheReader : IVerdictCacheReader
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public VerdictCacheReader(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public CachedVerdict? TryGet(string releaseKeyHash)
    {
        ArgumentNullException.ThrowIfNull(releaseKeyHash);

        var entry = _dbContext.VerdictCacheEntries
            .AsNoTracking()
            .SingleOrDefault(e => e.ReleaseKeyHash == releaseKeyHash);

        if (entry is null)
        {
            return null;
        }

        // M5 security review (MED): refresh LastAccessedAt on a cache hit so the row-ceiling LRU
        // trim (MaintenanceJob.PruneAiVerdictCacheAsync) evicts genuinely cold entries rather than
        // entries that are still being read regularly but happened to be written first. This stays
        // a single synchronous, local ExecuteUpdate against one row by primary key — no await, no
        // network/model call — preserving Q1-B's "never block the request path on anything
        // resembling a live model call" contract.
        _dbContext.VerdictCacheEntries
            .Where(e => e.Id == entry.Id)
            .ExecuteUpdate(setters => setters.SetProperty(e => e.LastAccessedAt, _timeProvider.GetUtcNow()));

        return new CachedVerdict((Verdict)entry.Verdict, entry.Confidence);
    }
}
