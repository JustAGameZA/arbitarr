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
    ///
    /// <para>
    /// arb-a7ll: <see cref="ReleaseCandidate.Poster"/> and <see cref="ReleaseCandidate.UsenetGroup"/>
    /// are key components because the prompt RENDERS both (the arb-458f metadata lines), so they are
    /// part of what the model was asked about. Without them a verdict formed under one poster is
    /// served for the same title posted by another — including verdicts produced before #327 under a
    /// forged poster line. <c>PromptVersion</c> is not the instrument for this: it versions the prompt
    /// TEMPLATE and only the template (CONTEXT.md, arb-p4r), the template is unchanged here, and an
    /// operator who pins <c>Arbitarr:Ai:PromptVersion</c> could defeat it anyway. The key is what no
    /// configuration can suppress. These are appended AFTER the existing components, so the ordering
    /// of what was already hashed is untouched — but every key still changes, so the ONE-TIME
    /// consequence is that the entire verdict cache is invalidated on the deploy that ships this.
    /// That is accepted: the misses are one round of re-classification, and the alternative is
    /// continuing to serve verdicts keyed on metadata the model never saw agree.
    /// </para>
    ///
    /// <para>
    /// Both are hashed RAW — no trim, no lower-casing. The M5 remark's argument for normalizing the
    /// title does not carry over: normalization is only safe where the differing inputs are the same
    /// claim, and a poster differing in case or surrounding whitespace is a different From header and
    /// so a different poster, exactly as the prompt shows it to the model. Encoding is collision-free
    /// by construction rather than by separator choice: a null poster (the indexer reported none)
    /// carries a sentinel prefix distinct from the prefix on a present value, so null and "" — which
    /// <see cref="ReleaseCandidate.Poster"/> documents as different claims — cannot fold together;
    /// and the group list is emitted count-first with every element length-prefixed, so
    /// <c>["a,b"]</c> and <c>["a","b"]</c> differ in the hashed input no matter which characters a
    /// newsgroup name turns out to permit.
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
            decodingIdentity,
            EncodePoster(candidate.Poster),
            EncodeGroups(candidate.UsenetGroup));

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// arb-a7ll: encodes the poster so "no poster reported" (<see langword="null"/>) and "the poster
    /// is blank" ("") cannot produce the same hashed input. Coercing null to "" — the obvious
    /// shortening — would merge two states <see cref="ReleaseCandidate.Poster"/> exists to keep
    /// apart, so the marker is chosen instead of the coercion. The value itself is passed through
    /// unaltered; see the Compute remark for why it is not normalized.
    /// </summary>
    private static string EncodePoster(string? poster) => poster is null ? "0" : "1:" + poster;

    /// <summary>
    /// arb-a7ll: encodes the newsgroup list unambiguously — the element count, then each element
    /// preceded by its length. Length-prefixing rather than joining is what makes <c>["a,b"]</c> and
    /// <c>["a","b"]</c> distinct: any join relies on its separator being absent from every element,
    /// which is an assumption about indexer-supplied data rather than a property of this code, and
    /// the list arrives from the wire.
    /// </summary>
    private static string EncodeGroups(IReadOnlyList<string> groups)
    {
        var builder = new StringBuilder();
        builder.Append(groups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var group in groups)
        {
            builder
                .Append(':')
                .Append(group.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(group);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Title normalization used by the cache key: trims surrounding whitespace and lower-invariants
    /// the title, so two releases differing only in casing/whitespace collide to one key.
    /// </summary>
    private static string Normalize(string title) => title.Trim().ToLowerInvariant();
}
