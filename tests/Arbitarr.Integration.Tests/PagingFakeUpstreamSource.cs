using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// An <see cref="IUpstreamSource"/> whose <see cref="SearchAsync"/> is a PAGE LOOP: a sequence of
/// short waits that each complete normally, modelling the real adapters' behaviour rather than a
/// single long hang (arb-4cso).
///
/// <para><b>Why <see cref="SecondFakeUpstreamSource"/> could not be used for the fan-out ceiling.</b>
/// That fake takes a <c>searchBudget</c> and throws when it elapses — it models one HTTP request
/// under <c>HttpClient.Timeout</c>, so it ends its own leg. A test built on it would prove nothing
/// about a whole-fan-out ceiling: the leg would have stopped anyway, and the assertion would pass
/// identically against a stage with NO ceiling at all. That is the vacuous shape CLAUDE.md §4 warns
/// about, and it is why this type exists instead of another parameter on that one.</para>
///
/// <para><b>This source has NO budget of its own, structurally — there is no constructor parameter
/// for one.</b> Each page's wait is short and completes, so no page ever times out, and
/// <see cref="SearchAsync"/> returns only after every page has been walked. The ONLY thing that can
/// end this leg early is a token from outside: the stage's ceiling, or the caller's own
/// cancellation. That is precisely what makes "the source was named in <c>TimedOutSources</c>" an
/// assertion about the CEILING rather than about the source giving up.</para>
///
/// <para>It is also the honest model of why the ceiling was needed. A real adapter issues up to
/// <c>MaxUpstreamCallsPerSearch</c> sequential requests per search, each one getting the full
/// per-request timeout afresh — so a leg outlives any single request's clock by a multiple, exactly
/// as it does here.</para>
/// </summary>
internal sealed class PagingFakeUpstreamSource : IUpstreamSource
{
    private readonly IReadOnlyList<ReleaseCandidate> _searchResults;
    private readonly int _pageCount;
    private readonly TimeSpan _perPageDelay;

    /// <param name="pageCount">
    /// How many pages the loop walks. With <paramref name="perPageDelay"/> this sets the leg's total
    /// duration; set the product comfortably above the ceiling under test so the ceiling fires with
    /// pages still to go.
    /// </param>
    /// <param name="perPageDelay">
    /// How long one page takes. Kept SHORT relative to the whole leg on purpose: it is the stand-in
    /// for one HTTP request, and the point of the fixture is that no single one of them is long
    /// enough to trip a per-request timeout while their sum is unbounded.
    /// </param>
    public PagingFakeUpstreamSource(
        string name,
        IReadOnlyList<ReleaseCandidate>? searchResults = null,
        int pageCount = 1,
        TimeSpan? perPageDelay = null)
    {
        Name = name;
        _searchResults = searchResults ?? Array.Empty<ReleaseCandidate>();
        _pageCount = pageCount;
        _perPageDelay = perPageDelay ?? TimeSpan.Zero;
    }

    public string Name { get; }

    /// <summary>
    /// How many pages this instance actually walked before the leg ended. Recorded so a test can
    /// show the ceiling CUT the loop short rather than the loop having finished early on its own —
    /// "it returned quickly" alone would also hold for a source that had nothing to do.
    /// </summary>
    public int PagesWalked { get; private set; }

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        for (var page = 0; page < _pageCount; page++)
        {
            // The token is honoured per page, which is what a real adapter does between sequential
            // requests. Task.Delay throws OperationCanceledException carrying the token that
            // cancelled it — the stage's ceiling token here — so the leg surfaces the cancellation
            // the same way HttpClient's own timeout does, and the stage's existing timeout clause
            // classifies it without needing a new arm.
            await Task.Delay(_perPageDelay, cancellationToken).ConfigureAwait(false);
            PagesWalked++;
        }

        return _searchResults;
    }

    public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceCaps(
            SupportedCategories: Array.Empty<int>(),
            SupportsTvSearch: false,
            SupportsMovieSearch: false,
            MaxPageSize: null));

    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream>(new MemoryStream());
}
