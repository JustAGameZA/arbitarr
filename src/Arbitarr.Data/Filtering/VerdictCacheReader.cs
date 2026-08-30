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

    public VerdictCacheReader(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public CachedVerdict? TryGet(string releaseKeyHash)
    {
        ArgumentNullException.ThrowIfNull(releaseKeyHash);

        var entry = _dbContext.VerdictCacheEntries
            .AsNoTracking()
            .SingleOrDefault(e => e.ReleaseKeyHash == releaseKeyHash);

        return entry is null ? null : new CachedVerdict((Verdict)entry.Verdict, entry.Confidence);
    }
}
