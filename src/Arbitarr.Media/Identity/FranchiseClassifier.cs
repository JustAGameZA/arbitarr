using Arbitarr.Core.Identity;

namespace Arbitarr.Media.Identity;

/// <summary>
/// Classifies a candidate <see cref="SeriesIdentity"/> against the series a search actually requested,
/// distinguishing "same series" from "franchise sibling" from "unrelated" — never gating admission.
/// </summary>
/// <remarks>
/// <para>
/// The Ghost in the Shell franchise is the motivating case: <c>Arise</c>,
/// <c>Stand Alone Complex</c>, and <c>SAC_2045</c> are three canonical, distinct identities that share
/// the "Ghost in the Shell" token but must never merge. This classifier never merges them either — it
/// compares provider IDs first (the only reliable same-series signal) and falls back to shared-token
/// detection purely to *label* an otherwise-unrelated-looking candidate as a franchise sibling, so
/// Step 3b's ranking can de-rank it with a reason instead of a caller having no idea why two
/// same-titled-ish results both appeared.
/// </para>
/// <para>
/// This never rejects a candidate outright (the explicitly-rejected hard-gate design from iteration 1)
/// — it only classifies. Admission/exclusion remains the caller's decision.
/// </para>
/// </remarks>
public static class FranchiseClassifier
{
    /// <summary>
    /// Classifies <paramref name="candidate"/> relative to <paramref name="requested"/>.
    /// </summary>
    /// <param name="requested">The series identity the search actually requested.</param>
    /// <param name="candidate">A candidate identity encountered during resolution.</param>
    /// <returns>
    /// <see cref="FranchiseRelation.Same"/> when both identities share a non-null provider ID (TVDB or
    /// TMDB); <see cref="FranchiseRelation.Sibling"/> when they share a franchise title token but
    /// resolve to different provider IDs (or either ID is unknown); otherwise
    /// <see cref="FranchiseRelation.Unrelated"/>.
    /// </returns>
    public static FranchiseClassification Classify(SeriesIdentity requested, SeriesIdentity candidate)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(candidate);

        if (SameProviderId(requested, candidate))
        {
            return new FranchiseClassification(FranchiseRelation.Same, Reason: null);
        }

        var sharedToken = FindSharedFranchiseToken(requested, candidate);
        if (sharedToken is not null)
        {
            var reason =
                $"sibling, not same series: shares franchise token '{sharedToken}' with requested series " +
                $"'{requested.PrimaryTitle}' but resolved to a distinct identity " +
                $"(candidate TVDB={FormatId(candidate.TvdbId)}, TMDB={FormatId(candidate.TmdbId)} vs. " +
                $"requested TVDB={FormatId(requested.TvdbId)}, TMDB={FormatId(requested.TmdbId)})";

            return new FranchiseClassification(FranchiseRelation.Sibling, reason);
        }

        return new FranchiseClassification(FranchiseRelation.Unrelated, Reason: null);
    }

    private static bool SameProviderId(SeriesIdentity a, SeriesIdentity b)
    {
        if (a.TvdbId is { } tvdbA && b.TvdbId is { } tvdbB)
        {
            return tvdbA == tvdbB;
        }

        if (a.TmdbId is { } tmdbA && b.TmdbId is { } tmdbB)
        {
            return tmdbA == tmdbB;
        }

        return false;
    }

    /// <summary>
    /// Finds a title token shared between the two identities' full title sets (primary + alternates),
    /// splitting on whitespace and ':'/'-' separators so "Ghost in the Shell: Arise" and "Ghost in the
    /// Shell: SAC_2045" are recognized as sharing "Ghost in the Shell" without requiring exact title
    /// equality anywhere.
    /// </summary>
    private static string? FindSharedFranchiseToken(SeriesIdentity requested, SeriesIdentity candidate)
    {
        var requestedPhrases = AllTitles(requested).Select(ExtractLeadPhrase).ToArray();
        var candidatePhrases = AllTitles(candidate).Select(ExtractLeadPhrase).ToArray();

        foreach (var phrase in requestedPhrases)
        {
            if (phrase.Length == 0)
            {
                continue;
            }

            if (candidatePhrases.Any(c => string.Equals(c, phrase, StringComparison.OrdinalIgnoreCase)))
            {
                return phrase;
            }
        }

        return null;
    }

    private static IEnumerable<string> AllTitles(SeriesIdentity identity) =>
        new[] { identity.PrimaryTitle }.Concat(identity.AlternateTitles);

    /// <summary>
    /// Extracts the portion of a title before the first ':' or '-' separator, trimmed — the shared
    /// franchise-name lead-in (e.g. "Ghost in the Shell" out of "Ghost in the Shell: Arise").
    /// </summary>
    private static string ExtractLeadPhrase(string title)
    {
        var separatorIndex = title.IndexOfAny([':', '-']);
        var lead = separatorIndex >= 0 ? title[..separatorIndex] : title;
        return lead.Trim();
    }

    private static string FormatId(int? id) => id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
}
