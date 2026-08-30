namespace Arbitarr.Core.Filtering;

/// <summary>
/// Storage bounds for the persisted verdict cache, shared by the producer
/// (<c>Arbitarr.Host.ClassifierPollingWorker</c>), the writer
/// (<c>Arbitarr.Data.Filtering.VerdictCacheWriter</c>) and the EF model so they cannot drift.
/// The EF <c>HasMaxLength</c> annotation is advisory on SQLite (no CHECK constraint is emitted),
/// so the bound is enforced in code before anything reaches the database: an upstream indexer
/// can return an arbitrarily long release title, and <c>TitleNormalizer</c> only strips tokens,
/// never caps length.
/// </summary>
public static class VerdictCacheLimits
{
    /// <summary>Maximum stored length of <see cref="CachedVerdict.RewrittenTitle"/>, in chars.</summary>
    public const int MaxRewrittenTitleLength = 1024;

    /// <summary>
    /// Truncates <paramref name="rewrittenTitle"/> to <see cref="MaxRewrittenTitleLength"/>;
    /// <see langword="null"/> and in-bound values pass through unchanged.
    /// </summary>
    public static string? TruncateRewrittenTitle(string? rewrittenTitle) =>
        rewrittenTitle is { Length: > MaxRewrittenTitleLength }
            ? rewrittenTitle[..MaxRewrittenTitleLength]
            : rewrittenTitle;
}
