using System.Text.Json.Serialization;

namespace Arbitarr.Core.Media;

/// <summary>
/// One series of a Sonarr library, AS ARBITARR SERVES IT — a PROJECTION of the upstream record and
/// never a passthrough of it.
///
/// <para><b>EVERY FILESYSTEM FIELD AND EVERY IMAGE URL IS EXCLUDED ON PURPOSE, AND THAT EXCLUSION IS
/// THE POINT.</b> The rationale for the path fields is the one <see cref="ArrQueueItem"/> states at
/// length for the queue projection (arb-6l9b.3) and it is not repeated here: Sonarr's own series
/// record carries <c>path</c>, <c>rootFolderPath</c> and <c>folderName</c>, absolute paths describing
/// the layout of the OPERATOR'S HOST filesystem, on a surface that is rendered into a browser and
/// quoted into bug reports. None of them tells an operator anything about the series that the members
/// below do not.</para>
///
/// <para><b><c>images</c> IS EXCLUDED FOR A SECOND REASON ON TOP OF THAT ONE, AND IT IS THE STRONGER
/// ONE.</b> An upstream <c>images</c> entry is not merely topology: it carries a <c>remoteUrl</c> and
/// a <c>url</c>, and the latter is routinely an ABSOLUTE URL BACK AT THE *ARR ITSELF — the operator's
/// own host, port and any path a reverse proxy adds. Serving it would publish the instance's address
/// through a response whose whole point is that the address never comes back (the envelope has no
/// member for it and the credential never returns). A poster is also the single largest thing in the
/// upstream document, so dropping it is what makes a whole-library fetch affordable at all. If the UI
/// ever wants artwork it must be proxied deliberately, not leaked incidentally through this
/// record.</para>
///
/// <para><b>THE SAFETY IS STRUCTURAL, NOT A FILTER.</b> The excluded members are ABSENT from
/// <see cref="UpstreamSeriesRecord"/> rather than present-and-ignored, exactly as
/// <see cref="UpstreamQueueRecord"/> does it: <see cref="System.Text.Json"/> discards unmapped
/// properties without complaint, so a field nobody declared cannot be picked up by a later projection
/// edit. Do not replace this with a passthrough plus a list of fields to strip, and do not add a
/// path-shaped or image-shaped member to it; <c>AdminArrLibraryEndpointsTests</c> plants all of those
/// fields in a multi-record fixture and asserts PER RECORD that none of them reaches the
/// response.</para>
/// </summary>
public sealed record ArrSeriesItem(
    int Id,
    string? Title,
    int? Year,
    int? TvdbId,
    string? Status,
    bool Monitored,
    int SeasonCount,
    int EpisodeFileCount,
    int EpisodeCount,
    long? SizeOnDisk,
    string? Network);

/// <summary>
/// One movie of a Radarr library, AS ARBITARR SERVES IT. The same projection contract
/// <see cref="ArrSeriesItem"/> documents applies unchanged — path fields and <c>images</c> are absent
/// from <see cref="UpstreamMovieRecord"/> structurally, for the reasons that record's doc gives.
/// </summary>
public sealed record ArrMovieItem(
    int Id,
    string? Title,
    int? Year,
    int? TmdbId,
    bool Monitored,
    bool HasFile,
    long? SizeOnDisk,
    string? Status);

/// <summary>
/// A page of an *arr library together with how the read went, and the count the filter left behind.
/// <see cref="Status"/> is the whole verdict; <see cref="Items"/> is empty for every value other than
/// <see cref="ArrSectionStatus.Ok"/>.
/// </summary>
/// <remarks>
/// There is deliberately NO field here for an upstream message, an exception, or the address that was
/// called — the same construction <see cref="ArrQueuePage"/> and <c>SonarrConnectivityProber</c> use:
/// a result type with nowhere to put such text cannot leak it.
///
/// <para><see cref="TotalRecords"/> IS THE COUNT AFTER FILTERING AND BEFORE PAGING, which is a
/// different provenance from the queue's identically named field. The queue's comes from upstream,
/// which pages the endpoint itself; <c>/api/v3/series</c> and <c>/api/v3/movie</c> are UNPAGED, so
/// this one is produced here and getting it the wrong way round (counting before the filter) is
/// silent — the page still renders, the pager just lies about how many pages there are.</para>
/// </remarks>
/// <param name="Status">The closed verdict.</param>
/// <param name="TotalRecords">The number of rows that matched the filter, across all pages.</param>
/// <param name="Items">The projected rows for the requested page.</param>
public sealed record ArrLibraryPage<T>(
    ArrSectionStatus Status,
    int TotalRecords,
    IReadOnlyList<T> Items)
{
    /// <summary>A failed read, carrying the verdict and nothing else.</summary>
    public static ArrLibraryPage<T> Failed(ArrSectionStatus status) =>
        new(status, 0, Array.Empty<T>());
}

/// <summary>
/// One upstream series record, carrying only the members this codebase reads. Fields Sonarr also
/// sends — <c>path</c>, <c>rootFolderPath</c>, <c>folderName</c>, and above all <c>images</c> — are
/// deliberately ABSENT rather than present-and-ignored, for the reasons <see cref="ArrSeriesItem"/>
/// sets out. A member that does not exist cannot be picked up by a later projection edit.
/// </summary>
internal sealed class UpstreamSeriesRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    /// <summary>
    /// Upstream sends this as a JSON number that may carry a fractional part; bytes are whole, and a
    /// long is what the frontend's byte formatter expects — the same handling
    /// <see cref="UpstreamQueueRecord.Size"/> gets.
    /// </summary>
    [JsonPropertyName("sizeOnDisk")]
    public double? SizeOnDisk { get; set; }

    /// <summary>
    /// Sonarr's rollup of episode counts. Carried because it is the only place the season and episode
    /// totals come from, and because the alternative — counting the <c>seasons</c> array — would mean
    /// declaring a nested shape whose other members are exactly the per-season <c>statistics</c> this
    /// projection has no use for.
    /// </summary>
    [JsonPropertyName("statistics")]
    public UpstreamSeriesStatistics? Statistics { get; set; }
}

/// <summary>
/// The subset of Sonarr's per-series <c>statistics</c> object this projection serves. Its
/// <c>sizeOnDisk</c> sibling is read from the record's own top-level field instead, so this shape
/// carries only the three counts.
/// </summary>
internal sealed class UpstreamSeriesStatistics
{
    [JsonPropertyName("seasonCount")]
    public int SeasonCount { get; set; }

    [JsonPropertyName("episodeFileCount")]
    public int EpisodeFileCount { get; set; }

    [JsonPropertyName("episodeCount")]
    public int EpisodeCount { get; set; }
}

/// <summary>
/// One upstream movie record, carrying only the members this codebase reads. As with
/// <see cref="UpstreamSeriesRecord"/>, Radarr's <c>path</c>, <c>rootFolderPath</c>,
/// <c>folderName</c> and <c>images</c> are ABSENT by construction rather than filtered out.
/// </summary>
internal sealed class UpstreamMovieRecord
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("monitored")]
    public bool Monitored { get; set; }

    [JsonPropertyName("hasFile")]
    public bool HasFile { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Same fractional-number handling as <see cref="UpstreamSeriesRecord.SizeOnDisk"/>.</summary>
    [JsonPropertyName("sizeOnDisk")]
    public double? SizeOnDisk { get; set; }
}
