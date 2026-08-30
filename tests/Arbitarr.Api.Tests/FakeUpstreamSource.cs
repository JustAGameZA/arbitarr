using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Tests;

/// <summary>Minimal in-memory <see cref="IUpstreamSource"/> for Api-layer tests that need a real source instance.</summary>
internal sealed class FakeUpstreamSource : IUpstreamSource
{
    private readonly IReadOnlyList<ReleaseCandidate> _searchResults;
    private readonly Func<Stream>? _downloadFactory;
    private readonly Exception? _searchException;
    private readonly Exception? _downloadException;

    public FakeUpstreamSource(
        string name,
        IReadOnlyList<ReleaseCandidate>? searchResults = null,
        Func<Stream>? downloadFactory = null,
        Exception? searchException = null,
        Exception? downloadException = null)
    {
        Name = name;
        _searchResults = searchResults ?? Array.Empty<ReleaseCandidate>();
        _downloadFactory = downloadFactory;
        _searchException = searchException;
        _downloadException = downloadException;
    }

    public string Name { get; }

    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (_searchException is not null)
        {
            throw _searchException;
        }

        return Task.FromResult(_searchResults);
    }

    public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceCaps(Array.Empty<int>(), false, false, null));

    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
    {
        if (_downloadException is not null)
        {
            throw _downloadException;
        }

        return Task.FromResult(_downloadFactory?.Invoke() ?? new MemoryStream());
    }
}
