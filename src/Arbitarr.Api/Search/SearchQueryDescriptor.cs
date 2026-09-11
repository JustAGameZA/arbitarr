using System.Globalization;
using System.Text;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Search;

/// <summary>
/// arb-2b6: renders a <see cref="SearchQuery"/> as a short descriptor identifying WHICH search an
/// event is about, in two spellings — one for humans (<see cref="Describe"/>, the event reason) and
/// one stable and machine-readable (<see cref="DescribeDetail"/>, the event detail).
///
/// <para><b>WHY THIS TYPE EXISTS RATHER THAN TWO INTERPOLATED STRINGS AT THE CALL SITE.</b> The two
/// spellings must describe the SAME query, and the audit finding this fixes (F-010) is precisely
/// what happens when a descriptor omits the fields that distinguish one search from another: 55
/// events all read <c>Query '' (tvsearch)</c>, because every RSS and id-based search carries no
/// query text and the reason was built from the text alone. Two independently maintained format
/// strings would drift the same way — one gaining a field the other lacks — and the drift is
/// invisible until an operator needs the events to tell two searches apart. One type, both
/// spellings, one set of tests.</para>
///
/// <para><b>BUILT FROM THE PARSED QUERY, NEVER THE RAW REQUEST.</b> Every value here comes off the
/// <see cref="SearchQuery"/> record. That is a secrets boundary, not a convenience: the client's
/// apikey travels on the inbound request's query string, and both surfaces these strings reach —
/// <c>/api/activity</c> and <c>/api/searches/recent</c> — are un-gated (CLAUDE.md §1). The record
/// carries no credential, so nothing here can render one. The declared <c>t=</c> mode is taken from
/// <see cref="SearchQuery.Type"/> (the parsed enum, closed to three values by
/// <c>SearchTypeParser</c>) rather than from the raw <c>t=</c> string the caller sent, so an
/// arbitrary caller-supplied value cannot be echoed into an un-gated feed either.</para>
///
/// <para><b>NO SECOND CLASSIFICATION OF THE QUERY SHAPE.</b> This type only reads fields that are
/// already on the record; it never re-derives what kind of search this is. In particular it does not
/// ask whether the query is the anime shape — <see cref="SearchQuery.IsIdScopedAbsoluteNumberQuery"/>
/// is the ONE predicate for that question (see its remarks: two copies drifted once already), and it
/// has already run by the time an event is recorded, leaving its answer in
/// <see cref="SearchQuery.Absolute"/>. Rendering that field is reading the existing answer, not
/// forming a new one.</para>
/// </summary>
public static class SearchQueryDescriptor
{
    /// <summary>
    /// The human-readable descriptor, for an event reason: e.g.
    /// <c>Query 'bleach' (tvsearch) cats=5030,5040 tvdbid=74796 S02E05</c>.
    /// Absent parts are omitted rather than rendered empty.
    /// </summary>
    public static string Describe(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var text = query.QueryText?.Trim();
        var builder = new StringBuilder();

        // An id-bearing or category-only request legitimately carries no text, and that is the
        // common case this fix is about — Sonarr's RSS sync and every id-based search. Rendering
        // Query '' for them is what made 55 events indistinguishable, so the no-text spellings say
        // what the request actually was instead of showing an empty pair of quotes.
        if (text is { Length: > 0 })
        {
            builder.Append(CultureInfo.InvariantCulture, $"Query '{text}'");
        }
        else if (HasAnySelector(query))
        {
            builder.Append("Query");
        }
        else
        {
            // No text and no selector at all: a category-wide feed, which is what an *arr RSS sync
            // issues. Naming it beats "Query" followed by nothing.
            builder.Append("Query (rss feed)");
        }

        builder.Append(CultureInfo.InvariantCulture, $" ({WireName(query.Type)})");

        if (query.Categories is { Count: > 0 })
        {
            builder.Append(CultureInfo.InvariantCulture, $" cats={string.Join(",", query.Categories)}");
        }

        if (query.TvdbId is { } tvdbId)
        {
            builder.Append(CultureInfo.InvariantCulture, $" tvdbid={tvdbId}");
        }

        if (query.TmdbId is { } tmdbId)
        {
            builder.Append(CultureInfo.InvariantCulture, $" tmdbid={tmdbId}");
        }

        // Season and episode are independent (#104: either can arrive without the other, with or
        // without an id), so they are rendered independently rather than only as a joined SxxEyy.
        if (query.Season is { } season && query.Episode is { } episode)
        {
            builder.Append(CultureInfo.InvariantCulture, $" S{season:D2}E{episode:D2}");
        }
        else if (query.Season is { } seasonOnly)
        {
            builder.Append(CultureInfo.InvariantCulture, $" S{seasonOnly:D2}");
        }
        else if (query.Episode is { } episodeOnly)
        {
            builder.Append(CultureInfo.InvariantCulture, $" E{episodeOnly:D2}");
        }

        // arb-u1c: absolute 0 is a REAL value, not a stand-in for "none" — SearchCacheKeyBuilder
        // distinguishes abs=0 from abs=none and keeps them on separate cache rows. A truthiness
        // check here would hide exactly the request whose cache row is its own.
        if (query.Absolute is { } absolute)
        {
            builder.Append(CultureInfo.InvariantCulture, $" abs={absolute}");
        }

        return builder.ToString();
    }

    /// <summary>
    /// The machine-readable descriptor, for an event detail: e.g.
    /// <c>type=tvsearch;cats=5030,5040;tvdbid=74796;season=2;episode=5;q=bleach</c>.
    /// Semicolon-separated <c>key=value</c> pairs in a fixed order, with absent parts omitted, so
    /// two events for the same search produce byte-identical details.
    /// </summary>
    public static string DescribeDetail(SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parts = new List<string>(7) { $"type={WireName(query.Type)}" };

        if (query.Categories is { Count: > 0 })
        {
            parts.Add($"cats={string.Join(",", query.Categories)}");
        }

        if (query.TvdbId is { } tvdbId)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"tvdbid={tvdbId}"));
        }

        if (query.TmdbId is { } tmdbId)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"tmdbid={tmdbId}"));
        }

        if (query.Season is { } season)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"season={season}"));
        }

        if (query.Episode is { } episode)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"episode={episode}"));
        }

        // See Describe: zero is a value here.
        if (query.Absolute is { } absolute)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"abs={absolute}"));
        }

        // Last, because it is the only free-text field: a query term containing a ';' cannot then
        // be mistaken for the start of another key=value pair by a reader scanning left to right.
        var text = query.QueryText?.Trim();
        if (text is { Length: > 0 })
        {
            parts.Add($"q={text}");
        }

        return string.Join(";", parts);
    }

    /// <summary>
    /// Whether the request identifies a search by something other than its text. Deliberately does
    /// NOT include <see cref="SearchQuery.Categories"/>: an *arr RSS sync is exactly a
    /// category-scoped request with no text, and it is the case the "(rss feed)" spelling names.
    /// </summary>
    private static bool HasAnySelector(SearchQuery query) =>
        query.TvdbId is not null
        || query.TmdbId is not null
        || query.Season is not null
        || query.Episode is not null
        || query.Absolute is not null;

    /// <summary>
    /// The inbound <c>t=</c> wire spelling of a parsed mode, so an operator reading an event sees
    /// the same word their *arr client sent. Matched explicitly rather than lower-casing the enum
    /// name: <c>TvSearch.ToString().ToLowerInvariant()</c> happens to give "tvsearch" today, but a
    /// renamed member would silently change a string operators read.
    /// </summary>
    private static string WireName(SearchType type) => type switch
    {
        SearchType.TvSearch => "tvsearch",
        SearchType.Movie => "movie",
        _ => "search",
    };
}
