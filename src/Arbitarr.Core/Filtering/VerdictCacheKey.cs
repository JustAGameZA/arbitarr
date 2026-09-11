using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Computes the AI verdict cache key established at Step 5 and populated by Step 6
/// (<c>Arbitarr.Data.Entities.VerdictCacheEntry.ReleaseKeyHash</c>). The key is a hash of
/// (normalized title + size + source + protocol) — explicitly <b>not</b> <see cref="ReleaseCandidate.Guid"/>,
/// which can rotate per-request for the same underlying release on some indexers (R17) — carrying
/// the model name, model digest, prompt version, and decoding identity so a model/prompt/decoding
/// change invalidates rather than silently mixing verdicts produced under different conditions.
/// </summary>
public static class VerdictCacheKey
{
    /// <summary>
    /// arb-2jti: the ASCII unit separator (U+001F), used to join the hashed fields below. Chosen
    /// because no field value (title, size, source, protocol, model identity) can legitimately
    /// contain it, so it cannot be confused with real field content. Changing this value silently
    /// changes every computed cache key, which invalidates the whole verdict cache on next deploy.
    /// </summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// Computes the cache key for <paramref name="candidate"/> under the given model identity.
    /// Two candidates with the same normalized title, size, source, and protocol produce the same
    /// key (even with different <see cref="ReleaseCandidate.Guid"/> values); changing
    /// <paramref name="modelName"/>, <paramref name="modelDigest"/>, <paramref name="promptVersion"/>,
    /// or <paramref name="decodingIdentity"/> changes the key.
    ///
    /// <para>
    /// arb-qg3o: <paramref name="decodingIdentity"/> is a stable token for the sampling constants
    /// the call was decoded under (temperature/seed). It is a separate component from
    /// <paramref name="promptVersion"/> on purpose: prompt version means the prompt TEMPLATE
    /// version, and overloading it to also signal a decoding change both stretches that term and
    /// leaves a hole — an operator who pins <c>Arbitarr:Ai:PromptVersion</c> explicitly would keep
    /// serving verdicts cached under the old decoding. Folding decoding in here closes that by
    /// construction, since no configuration value can suppress it.
    /// </para>
    ///
    /// <para>
    /// M5 security review (LOW): keys on <see cref="ReleaseCandidate.OriginalTitle"/> (the
    /// pre-normalization title), not <see cref="ReleaseCandidate.Title"/> — the normalizer strips
    /// noise tokens, so two releases differing only by stripped noise would otherwise collide to
    /// the same key whenever their size also happened to match, silently reusing one release's
    /// verdict for a different one.
    /// </para>
    /// </summary>
    public static string Compute(
        ReleaseCandidate candidate,
        string sourceName,
        string modelName,
        string modelDigest,
        string promptVersion,
        string decodingIdentity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(sourceName);
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(modelDigest);
        ArgumentNullException.ThrowIfNull(promptVersion);
        ArgumentNullException.ThrowIfNull(decodingIdentity);

        var normalizedTitle = Normalize(candidate.OriginalTitle);
        var input = string.Join(
            FieldSeparator,
            normalizedTitle,
            candidate.Size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            sourceName,
            candidate.Protocol.ToString(),
            modelName,
            modelDigest,
            promptVersion,
            decodingIdentity);

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// Title normalization used by the cache key: trims surrounding whitespace and lower-invariants
    /// the title, so two releases differing only in casing/whitespace collide to one key.
    /// </summary>
    private static string Normalize(string title) => title.Trim().ToLowerInvariant();
}
