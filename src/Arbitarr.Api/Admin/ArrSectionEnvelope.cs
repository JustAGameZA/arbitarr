using Arbitarr.Core.Media;

namespace Arbitarr.Api.Admin;

/// <summary>
/// The one wire shape every *arr section on the Library screen answers with — the queue reads
/// (arb-6l9b.3) and the library reads (arb-6l9b.4) alike, differing only in the element type of
/// <paramref name="Records"/>.
///
/// <para><b>THE TOP LEVEL IS ALWAYS 200, AND THE VERDICT LIVES IN <paramref name="Status"/>.</b> An
/// unconfigured or unreachable instance is not an error in THIS request — the admin call itself
/// succeeded and is reporting, faithfully, what it found. Mapping those onto HTTP status codes would
/// make the client unable to tell "Arbitarr is broken" from "the *arr you pointed at is", and would
/// put a 502-shaped answer on a route whose caller is already authenticated and only asking a
/// question.</para>
///
/// <para><b>WHY THIS IS A SHARED GENERIC RATHER THAN FOUR RECORDS THAT LOOK ALIKE.</b>
/// <c>ArrQueueResponse</c>'s own doc left this open deliberately: at arb-6l9b.3 there was ONE
/// implementor, and a generic introduced for a single use would be an abstraction invented ahead of
/// its second case. arb-6l9b.4 arrives holding the other three at once, and they do vary only in
/// <paramref name="Records"/> — so the generic is now the thing that makes the identity CHECKABLE BY
/// THE COMPILER instead of by a reflection test comparing member lists. One Library screen renders all
/// four sections; a client that had to switch on the envelope differently per section would be four
/// renderers rather than one, and four hand-maintained copies of five members is exactly how that
/// divergence arrives.</para>
///
/// <para>The four sections' element types are the only difference and they are genuinely different:
/// <see cref="ArrQueueItem"/>, <see cref="ArrSeriesItem"/>, <see cref="ArrMovieItem"/> — each its own
/// slim projection, each excluding the operator's filesystem layout structurally.</para>
/// </summary>
/// <typeparam name="T">The projected row type this section serves.</typeparam>
/// <param name="Status">
/// One of <see cref="ArrSectionStatus"/>, as a stable string the UI switches on — the enum's
/// <c>ToString()</c> and never the enum itself, so the wire contract does not depend on member order.
/// </param>
/// <param name="Message">
/// Fixed, human-readable wording chosen from <paramref name="Status"/> ALONE. Never derived from the
/// upstream response body, an exception message, the configured URL, or the key — see
/// <see cref="AdminArrQueueEndpoints"/>'s and <see cref="AdminArrLibraryEndpoints"/>'s
/// <c>DescribeStatus</c> for why that is a security property and not a style preference. The two
/// surfaces word it differently on purpose; what they share is that neither can interpolate anything
/// upstream said.
/// </param>
/// <param name="Page">The page served, after clamping.</param>
/// <param name="PageSize">The page size served, after clamping.</param>
/// <param name="TotalRecords">
/// The total across all pages; 0 for every non-Ok status. Its PROVENANCE differs by section and the
/// difference is invisible from here: the queue's comes from upstream, which pages that endpoint
/// itself, while the library's is counted after filtering and before paging because
/// <c>/api/v3/series</c> and <c>/api/v3/movie</c> are unpaged.
/// </param>
/// <param name="Records">The projected rows; empty for every non-Ok status.</param>
public sealed record ArrSectionEnvelope<T>(
    string Status,
    string Message,
    int Page,
    int PageSize,
    int TotalRecords,
    IReadOnlyList<T> Records);
