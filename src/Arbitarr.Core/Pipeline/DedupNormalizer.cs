using System.Globalization;
using System.Text;

namespace Arbitarr.Core.Pipeline;

/// <summary>
/// The whole surface of <see cref="IDedupStage"/>'s title rule: case-folds a release title and
/// collapses every run of whitespace and punctuation to a single space, then trims. Two titles
/// that normalise to the same string satisfy dedup's first condition; nothing else about them is
/// compared.
///
/// <para><b>Deliberately NOT the deny-list <c>TitleNormalizer</c> in
/// <c>src/Arbitarr.Ai/Normalization/</c>, for two reasons from
/// <c>docs/adr/0019-dedup-is-a-pipeline-stage-with-conservative-exact-merge.md</c>, either
/// sufficient on its own.</b> First, <c>Arbitarr.Api</c> — where the pipeline stages live — does
/// not reference <c>Arbitarr.Ai</c> at all, and the ADR forbids adding that reference to borrow a
/// normaliser: it would couple dedup to the AI surface. Second, that normaliser is default-OFF
/// behind <c>SettingKey.TitleNormalizationEnabled</c>, so depending on it would make dedup's
/// merge behaviour follow an unrelated AI setting — flipping an AI toggle would silently change
/// which releases collapse. If the two normalisations ever need to agree, the shared piece moves
/// here to <c>Arbitarr.Core</c>; the project reference does not get added.</para>
///
/// <para><b>This is not deobfuscation.</b> An obfuscated Usenet title is normalised as the literal
/// text it is, so two indexers carrying the same obfuscated post merge only if they spell it
/// identically and otherwise split. ADR 0019 records that split as the acceptable outcome: under
/// ADR 0002's asymmetry a visible duplicate row is cheap, while a false merge silently hides one
/// release behind another.</para>
///
/// <para><b>What this folds is load-bearing.</b> Broadening it merges more and narrowing it merges
/// less, and ADR 0019's consequences section says so explicitly: a change here is a change to the
/// dedup policy, not a tidy-up.</para>
/// </summary>
public static class DedupNormalizer
{
    /// <summary>
    /// Returns the dedup-comparable form of <paramref name="title"/>: lower-cased invariantly,
    /// with every maximal run of whitespace or punctuation replaced by one space, and leading and
    /// trailing space removed. A null or all-separator title normalises to the empty string.
    /// </summary>
    /// <remarks>
    /// Lower-cased with <see cref="CultureInfo.InvariantCulture"/> rather than the ambient culture
    /// so two processes in different locales agree on whether a pair merges; a culture-sensitive
    /// fold makes dedup's answer depend on the host's regional settings, which is not a property
    /// a matching rule may have.
    ///
    /// <para>Whitespace and punctuation are collapsed to the SAME separator rather than being
    /// deleted, so <c>"Show S01E01"</c> and <c>"Show.S01E01"</c> agree while <c>"Show S01E01"</c>
    /// and <c>"ShowS01E01"</c> do not — deleting separators outright would merge titles whose only
    /// evidence of being the same release is that removing characters made them equal.</para>
    ///
    /// <para>Symbols (<see cref="char.IsSymbol(char)"/>: <c>+</c>, <c>=</c>, <c>~</c>, currency
    /// signs) are deliberately left alone. They are not punctuation in Unicode's classification and
    /// they carry meaning in real release names (<c>"Disney+"</c>); folding them would merge titles
    /// that differ in content, which is the expensive direction of ADR 0019's asymmetry.</para>
    /// </remarks>
    public static string Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(title.Length);
        var pendingSeparator = false;

        foreach (var c in title)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                // Only remember that a separator run occurred; it is emitted lazily when the next
                // real character arrives, which is what makes a trailing run cost nothing and
                // removes the need for a separate trim pass.
                pendingSeparator = builder.Length > 0;
                continue;
            }

            if (pendingSeparator)
            {
                builder.Append(' ');
                pendingSeparator = false;
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
