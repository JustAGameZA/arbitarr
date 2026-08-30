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

    public SecondFakeUpstreamSource(
        string name,
        IReadOnlyList<ReleaseCandidate>? searchResults = null,
        bool throwsRequestLimitReached = false,
        Action<SearchQuery>? onSearch = null)
    {
        Name = name;
        _searchResults = searchResults ?? Array.Empty<ReleaseCandidate>();
        _throwsRequestLimitReached = throwsRequestLimitReached;
        _onSearch = onSearch;
    }

    public string Name { get; }

    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        _onSearch?.Invoke(query);

        if (_throwsRequestLimitReached)
        {
            throw new RequestLimitReachedException(Name);
        }

        return Task.FromResult(_searchResults);
    }

    public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new SourceCaps(
            SupportedCategories: Array.Empty<int>(),
            SupportsTvSearch: false,
            SupportsMovieSearch: false,
            MaxPageSize: null));
    }

    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<Stream>(new MemoryStream());
    }
}
