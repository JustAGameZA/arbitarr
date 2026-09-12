using System.Text.Json.Serialization;

namespace Arbitarr.Core.Media;

/// <summary>
/// How a queue read against a configured *arr instance turned out. A CLOSED enum, for the same
/// reason <see cref="Arbitarr.Core.Sources.SourceProbeOutcome"/> is one: every value the wire can
/// carry is enumerated here, so no failure path has anywhere to put credential-derived text. The
/// envelope's human wording is chosen from this value ALONE (see
/// <c>Arbitarr.Api.Admin.AdminArrQueueEndpoints.DescribeStatus</c>) — never from the upstream body,
/// an exception, the configured URL, or the key.
///
/// <para><b>WHY NOT REUSE <see cref="Arbitarr.Core.Sources.SourceProbeOutcome"/>.</b> That enum is a
/// CONNECTIVITY verdict and carries <c>TlsFailure</c>, a member a queue read has no separate answer
/// for: a failed handshake is simply "we could not read the queue", and the operator's fix for it is
/// the one the connectivity probe already spells out in its own section. Reusing it would put a
/// member on this wire contract that this surface never produces, which is the mirror of the mistake
/// CONTEXT.md records for <c>OllamaProbeOutcome</c> — there the unreachable member was
/// <c>AuthenticationFailed</c>. Five members — <see cref="Ok"/>, <see cref="NotConfigured"/>,
/// <see cref="Unreachable"/>, <see cref="AuthenticationFailed"/>, <see cref="UnexpectedResponse"/> —
/// all reachable, is the right closed set here: <c>TlsFailure</c> is dropped for the reason above,
/// and <see cref="NotConfigured"/> is added because this surface is read on a schedule against an
/// instance that may never have been configured, a state a probe (which an operator triggers
/// deliberately, having just entered an address) cannot be in.</para>
/// </summary>
public enum ArrSectionStatus
{
    /// <summary>
    /// Nothing usable is stored. Covers BOTH "no address at all" and "an address with no key" — the
    /// half-configured state the credential provider reports as null. They are one value here
    /// because the operator's next action is identical (finish configuring the section), and because
    /// telling them apart on the wire would be a signal derived from what is stored.
    /// </summary>
    NotConfigured,

    /// <summary>The instance answered with a queue document that parsed.</summary>
    Ok,

    /// <summary>Nothing answered before the budget expired, or the connection failed outright.</summary>
    Unreachable,

    /// <summary>The instance answered, and refused the key (401/403).</summary>
    AuthenticationFailed,

    /// <summary>
    /// Something answered but it was not a queue document — a login page, a reverse proxy's error
    /// page, a 5xx, a redirect, or JSON of the wrong shape.
    /// </summary>
    UnexpectedResponse,
}

/// <summary>
/// One row of an *arr download queue, AS ARBITARR SERVES IT — a PROJECTION of the upstream record
/// and never a passthrough of it.
///
/// <para><b>EVERY FILESYSTEM FIELD IS EXCLUDED ON PURPOSE, AND THAT EXCLUSION IS THE POINT.</b>
/// Sonarr's and Radarr's own queue records carry <c>outputPath</c>, <c>path</c>,
/// <c>rootFolderPath</c> and <c>folderName</c> — absolute paths describing the layout of the
/// OPERATOR'S HOST filesystem. This response is public-repo-adjacent surface: it is rendered into a
/// browser, it is quoted into bug reports, and its shape is documented in a repository anyone can
/// read. None of those fields tells an operator anything about the download's progress that the
/// fields below do not, so carrying them would be topology disclosure bought for nothing. Anything
/// key-shaped is excluded for the same reason one step more obviously.</para>
///
/// <para><b>THE SAFETY IS STRUCTURAL, NOT A FILTER.</b> This record enumerates what is served, so a
/// field that is not named here cannot reach the wire even if a future upstream version adds one —
/// which is exactly what a denylist over the raw document would fail to do. Do not replace this with
/// a passthrough plus a list of fields to strip, and do not add a path-shaped member to it;
/// <c>ArrQueueProjectionExcludesPathsTests</c> plants those fields in a multi-record fixture and
/// asserts PER RECORD that none of them reaches the response.</para>
/// </summary>
public sealed record ArrQueueItem(
    string? Title,
    string? Status,
    string? TrackedDownloadStatus,
    string? TrackedDownloadState,
    long? Size,
    long? SizeLeft,
    string? TimeLeft,
    DateTimeOffset? EstimatedCompletionTime,
    string? Protocol,
    string? DownloadClient,
    string? Indexer,
    IReadOnlyList<string> StatusMessages,
    string? ErrorMessage);

/// <summary>
/// A page of an *arr queue together with how the read went. <see cref="Status"/> is the whole
/// verdict; <see cref="Items"/> is empty for every value other than <see cref="ArrSectionStatus.Ok"/>.
/// </summary>
/// <remarks>
/// There is deliberately NO field here for an upstream message, an exception, or the address that
/// was called. A result type with nowhere to put such text cannot leak it, which is the same
/// construction <c>SonarrConnectivityProber</c> uses by returning a bare enum.
/// </remarks>
/// <param name="Status">The closed verdict.</param>
/// <param name="TotalRecords">The upstream's total across all pages, or 0 when the read did not succeed.</param>
/// <param name="Items">The projected rows for the requested page.</param>
public sealed record ArrQueuePage(
    ArrSectionStatus Status,
    int TotalRecords,
    IReadOnlyList<ArrQueueItem> Items)
{
    /// <summary>A failed read, carrying the verdict and nothing else.</summary>
    public static ArrQueuePage Failed(ArrSectionStatus status) =>
        new(status, 0, Array.Empty<ArrQueueItem>());
}

/// <summary>
/// The upstream queue document as it is PARSED — an internal deserialisation shape that never
/// reaches the wire. <see cref="ArrQueueReader"/> projects it onto <see cref="ArrQueueItem"/>
/// immediately; the two types are separate so that adding a field here (to read it) can never be the
/// same edit as serving it.
/// </summary>
internal sealed class UpstreamQueueDocument
{
    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonPropertyName("records")]
    public List<UpstreamQueueRecord>? Records { get; set; }
}

/// <summary>
/// One upstream queue record, carrying only the members this codebase reads. Fields Sonarr and
/// Radarr also send — <c>outputPath</c>, <c>path</c>, <c>rootFolderPath</c>, <c>folderName</c> — are
/// deliberately ABSENT rather than present-and-ignored: a member that does not exist cannot be
/// picked up by a later projection edit, and <see cref="System.Text.Json"/> discards unmapped
/// properties without complaint.
/// </summary>
internal sealed class UpstreamQueueRecord
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("trackedDownloadStatus")]
    public string? TrackedDownloadStatus { get; set; }

    [JsonPropertyName("trackedDownloadState")]
    public string? TrackedDownloadState { get; set; }

    [JsonPropertyName("size")]
    public double? Size { get; set; }

    [JsonPropertyName("sizeleft")]
    public double? SizeLeft { get; set; }

    [JsonPropertyName("timeleft")]
    public string? TimeLeft { get; set; }

    [JsonPropertyName("estimatedCompletionTime")]
    public DateTimeOffset? EstimatedCompletionTime { get; set; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("downloadClient")]
    public string? DownloadClient { get; set; }

    [JsonPropertyName("indexer")]
    public string? Indexer { get; set; }

    [JsonPropertyName("statusMessages")]
    public List<UpstreamStatusMessage>? StatusMessages { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// An upstream <c>statusMessages</c> entry. Both members are operator-facing text the *arr already
/// renders in its own UI ("Not a preferred word upgrade for existing episode file"), which is why
/// this one nested shape is carried through at all while every other nested object is dropped.
/// </summary>
internal sealed class UpstreamStatusMessage
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("messages")]
    public List<string>? Messages { get; set; }
}
