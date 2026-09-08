using System.Globalization;

namespace Arbitarr.Api.Search;

/// <summary>
/// Security-m3 MEDIUM #4: bounds attacker/client-controlled <c>tvdbid</c>/<c>tmdbid</c>/
/// <c>season</c>/<c>ep</c> query-string inputs at the endpoint boundary, independent of what any
/// downstream component does with them. Unvalidated, these bind straight through as <c>int?</c>
/// and each distinct out-of-range value (including negatives and <see cref="int.MinValue"/>)
/// widens <see cref="Arbitarr.Core.Identity.SearchCacheKeyBuilder"/>'s key space with a fresh
/// cache row. Values outside a plausible real-world range are treated as absent, so the query
/// falls back to its next identity signal (e.g. the title-set/q-text path) instead of minting a
/// new row per garbage value.
/// </summary>
public static class IdParamClamp
{
    /// <summary>Plausible upper bound for a TVDB/TMDB provider id.</summary>
    public const int MaxProviderId = 9_999_999;

    /// <summary>Plausible upper bound for a season number.</summary>
    public const int MaxSeason = 9_999;

    /// <summary>Plausible upper bound for an episode number.</summary>
    public const int MaxEpisode = 99_999;

    /// <summary>
    /// Turns a raw <c>tvdbid</c>/<c>tmdbid</c>/<c>season</c>/<c>ep</c> query-string value into an
    /// <see cref="int"/>, treating an absent, empty or unparseable value as absent (#104).
    /// </summary>
    /// <remarks>
    /// The Torznab/Newznab routes bind these as <see cref="string"/> and call this rather than
    /// binding <c>int?</c> directly, because minimal-API <c>int?</c> binding rejects an EMPTY value
    /// — <c>tvdbid=</c> — with a 400 and a <c>text/plain</c> <c>BadHttpRequestException</c> body
    /// ("Failed to bind parameter") before the endpoint runs at all. An *arr client that renders an
    /// unresolved id as a blank parameter therefore got a non-XML error document from an indexer
    /// route contractually obliged to answer in Torznab/Newznab XML, and the search never ran even
    /// though <c>q</c>/<c>season</c>/<c>ep</c> alone were perfectly sufficient to serve it.
    ///
    /// An empty id is treated as ABSENT rather than as an error for the same reason the clamps
    /// below treat an out-of-range one that way: the query still carries its other identity signals,
    /// and answering the search is more useful than refusing it. A non-numeric value takes the same
    /// path — it cannot be an id, and it was never rejected on its merits before this either
    /// (<c>tvdbid=abc</c> hit the identical binding failure).
    /// </remarks>
    public static int? ParseOptional(string? value) =>
        int.TryParse(
            value?.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    public static int? ClampProviderId(int? id) =>
        id is null or <= 0 or > MaxProviderId ? null : id;

    public static int? ClampSeason(int? season) =>
        season is null or < 0 or > MaxSeason ? null : season;

    public static int? ClampEpisode(int? episode) =>
        episode is null or < 0 or > MaxEpisode ? null : episode;
}
