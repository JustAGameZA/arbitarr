using Arbitarr.Core.Filtering;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Filtering;

/// <summary>
/// EF Core-backed <see cref="IVerdictCacheWriter"/> against the <see cref="VerdictCacheEntry"/>
/// table. Used only by background classification code (<c>Arbitarr.Ai.ClassifierWorker</c>), never
/// on the request path — see <see cref="VerdictCacheReader"/> for the request-path (sync) side.
/// Kept outside Arbitarr.Core so Core stays free of any reference to Arbitarr.Data (AC6).
/// </summary>
public sealed class VerdictCacheWriter : IVerdictCacheWriter
{
    private readonly ArbitarrDbContext _dbContext;

    public VerdictCacheWriter(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task PutAsync(
        string releaseKeyHash,
        string modelName,
        string modelDigest,
        string promptVersion,
        Verdict verdict,
        double confidence,
        string? rewrittenTitle = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseKeyHash);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(modelDigest);
        ArgumentNullException.ThrowIfNull(promptVersion);

        // Defense in depth: HasMaxLength is not enforced by SQLite, so clamp here as well so any
        // future producer is bounded even if it forgets to truncate.
        rewrittenTitle = VerdictCacheLimits.TruncateRewrittenTitle(rewrittenTitle);

        var now = DateTimeOffset.UtcNow;

        var entry = await _dbContext.VerdictCacheEntries
            .SingleOrDefaultAsync(e => e.ReleaseKeyHash == releaseKeyHash, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            entry = new VerdictCacheEntry
            {
                ReleaseKeyHash = releaseKeyHash,
                ModelName = modelName,
                ModelDigest = modelDigest,
                PromptVersion = promptVersion,
                Verdict = (int)verdict,
                Confidence = confidence,
                CreatedAt = now,
                LastAccessedAt = now,
                RewrittenTitle = rewrittenTitle,
            };
            _dbContext.VerdictCacheEntries.Add(entry);
        }
        else
        {
            entry.ModelName = modelName;
            entry.ModelDigest = modelDigest;
            entry.PromptVersion = promptVersion;
            entry.Verdict = (int)verdict;
            entry.Confidence = confidence;
            entry.LastAccessedAt = now;
            if (rewrittenTitle is not null)
            {
                // Additive: a follow-up PutAsync call that only attaches a rewrite (this worker's
                // pattern) must not clobber a previously cached rewrite with null when re-called
                // without one.
                entry.RewrittenTitle = rewrittenTitle;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
