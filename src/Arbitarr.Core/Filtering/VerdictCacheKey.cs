using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Computes the AI verdict cache key established at Step 5 and populated by Step 6
/// (<c>Arbitarr.Data.Entities.VerdictCacheEntry.ReleaseKeyHash</c>). The key is a hash of
/// (normalized title + size + source + protocol) — explicitly <b>not</b> <see cref="ReleaseCandidate.Guid"/>,
/// which can rotate per-request for the same underlying release on some indexers (R17) — carrying
/// the model name, model digest, and prompt version so a model/prompt upgrade invalidates rather
/// than silently mixing verdicts from different models.
/// </summary>
public static class VerdictCacheKey
{
    /// <summary>
    /// Computes the cache key for <paramref name="candidate"/> under the given model identity.
    /// Two candidates with the same normalized title, size, source, and protocol produce the same
    /// key (even with different <see cref="ReleaseCandidate.Guid"/> values); changing
    /// <paramref name="modelName"/>, <paramref name="modelDigest"/>, or <paramref name="promptVersion"/>
    /// changes the key.
    /// </summary>
    public static string Compute(
        ReleaseCandidate candidate,
        string sourceName,
        string modelName,
        string modelDigest,
        string promptVersion)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(modelDigest);
        ArgumentNullException.ThrowIfNull(promptVersion);

        var normalizedTitle = Normalize(candidate.Title);
        var input = string.Join(
            '',
            normalizedTitle,
            candidate.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceName,
            candidate.Protocol.ToString(),
            modelName,
            modelDigest,
            promptVersion);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Title normalization used by the cache key: trims surrounding whitespace and lower-invariants
    /// the title, so two releases differing only in casing/whitespace collide to one key.
    /// </summary>
    private static string Normalize(string title) => title.Trim().ToLowerInvariant();
}
