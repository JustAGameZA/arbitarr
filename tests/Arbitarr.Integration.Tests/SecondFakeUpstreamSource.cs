using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// A second, independent fake <see cref="IUpstreamSource"/> implementation, scoped to
/// Arbitarr.Integration.Tests. This exists specifically to prove (M1-10) that
/// <see cref="Arbitarr.Api.Search.UpstreamMergeStage"/> unions results across multiple distinct
/// upstream sources with zero renderer changes — Arbitarr.Api.Tests' own
/// internal FakeUpstreamSource lives in a different assembly and cannot be reused directly.
/// </summary>
internal sealed class SecondFakeUpstreamSource : IUpstreamSource
{
    private readonly IReadOnlyList<ReleaseCandidate> _searchResults;
    private readonly bool _throwsRequestLimitReached;
    private readonly Action<SearchQuery>? _onSearch;
    private readonly Exception? _downloadException;
    private readonly Exception? _searchException;
    private readonly TimeSpan? _searchBudget;
    private readonly TimeSpan? _searchDelay;

    /// <param name="downloadException">
    /// Thrown by <see cref="FetchDownloadAsync"/> instead of returning a payload (arb-apj), so a
    /// test can drive the download proxy's refusal path through the REAL host composition rather
    /// than calling the endpoint directly. Null keeps the original behaviour: an empty stream.
    /// </param>
    /// <param name="searchException">
    /// Thrown by <see cref="SearchAsync"/> instead of returning results (arb-x7w8.7), so a test can
    /// drive the merge stage's "any other failure" leg with a real transport-shaped exception rather
    /// than only the rate-limit one <paramref name="throwsRequestLimitReached"/> covers.
    /// </param>
    /// <param name="searchBudget">
    /// This source's OWN time budget, the stand-in for the <c>HttpClient.Timeout</c> the real adapter
    /// is constructed with from its <c>TimeoutSeconds</c> row (arb-x7w8.7). When it elapses before
    /// <paramref name="searchDelay"/> does, the search throws the same
    /// <see cref="TaskCanceledException"/> that <c>HttpClient</c> throws on its own timeout —
    /// cancelled by a token that is NOT the caller's, which is exactly the distinction the merge
    /// stage's timeout clause turns on. Null: no budget of its own.
    /// </param>
    /// <param name="searchDelay">
    /// How long this source takes to answer. Set it far longer than <paramref name="searchBudget"/>
    /// to model an indexer that has hung: the budget fires first and the delay is still pending when
    /// the merge returns, which is what makes "the merge did not wait for the slow source" an
    /// assertion about elapsed time rather than about ordering.
    /// </param>
    public SecondFakeUpstreamSource(
        string name,
        IReadOnlyList<ReleaseCandidate>? searchResults = null,
        bool throwsRequestLimitReached = false,
        Action<SearchQuery>? onSearch = null,
        Exception? downloadException = null,
        Exception? searchException = null,
        TimeSpan? searchBudget = null,
        TimeSpan? searchDelay = null)
    {
        Name = name;
        _searchResults = searchResults ?? Array.Empty<ReleaseCandidate>();
        _throwsRequestLimitReached = throwsRequestLimitReached;
        _onSearch = onSearch;
        _downloadException = downloadException;
        _searchException = searchException;
        _searchBudget = searchBudget;
        _searchDelay = searchDelay;
    }

    public string Name { get; }

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        _onSearch?.Invoke(query);

        if (_throwsRequestLimitReached)
        {
            throw new RequestLimitReachedException(Name);
        }

        if (_searchException is not null)
        {
            throw _searchException;
        }

        if (_searchDelay is { } delay)
        {
            // The budget is this source's own, linked to the caller's token only so that abandoning
            // the whole request still tears the delay down. Whichever fires, the throw below reports
            // the one that did: TaskCanceledException for the budget (what HttpClient throws), and
            // the caller's OperationCanceledException straight through for a real cancellation.
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_searchBudget is { } window)
            {
                budget.CancelAfter(window);
            }

            try
            {
                await Task.Delay(delay, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested is false)
            {
                // The budget token is passed to the exception ON PURPOSE. HttpClient's real timeout
                // throw carries the token that cancelled it, and the merge stage classifies a
                // timeout by the IDENTITY of that token (is it the caller's, or not?). A
                // TaskCanceledException constructed WITHOUT one carries CancellationToken.None,
                // which compares EQUAL to the caller's token in the common case where the caller
                // passed CancellationToken.None — so the stage would read this source's own timeout
                // as a caller cancellation and classify it Failed rather than TimedOut. Dropping
                // this argument makes the fake stop modelling HttpClient.
                throw new TaskCanceledException(
                    $"The source '{Name}' exceeded its configured timeout.",
                    innerException: null,
                    budget.Token);
            }
        }

        return _searchResults;
    }

    public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SourceCaps(
            SupportedCategories: Array.Empty<int>(),
            SupportsTvSearch: false,
            SupportsMovieSearch: false,
            MaxPageSize: null));
    }

    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
    {
        if (_downloadException is not null)
        {
            throw _downloadException;
        }

        return Task.FromResult<Stream>(new MemoryStream());
    }
}
