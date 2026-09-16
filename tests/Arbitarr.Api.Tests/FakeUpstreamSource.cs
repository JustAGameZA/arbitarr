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

    /// <summary>
    /// How many times <see cref="FetchDownloadAsync"/> was called on THIS instance, and the
    /// candidate each call was given.
    /// </summary>
    /// <remarks>
    /// Recorded rather than inferred from the bytes that came back, so a test over N sources can
    /// assert the other N-1 were never ASKED. "The right payload was returned" alone would also pass
    /// against an implementation that fetched from every source and let the first answer win — which
    /// would have sent each of those sources' keys upstream for a release none of them produced.
    /// </remarks>
    public List<ReleaseCandidate> DownloadRequests { get; } = new();

    public Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (_searchException is not null)
        {
            throw _searchException;
        }

        return Task.FromResult(_searchResults);
    }

    public Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceCaps(Array.Empty<int>(), false, false, null));

    public Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default)
    {
        DownloadRequests.Add(release);

        if (_downloadException is not null)
        {
            throw _downloadException;
        }

        return Task.FromResult(_downloadFactory?.Invoke() ?? new MemoryStream());
    }
}
