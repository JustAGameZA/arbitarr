using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Sources;

/// <summary>
/// Contract for an upstream indexer/adapter capable of searching for releases,
/// reporting its capabilities, and fetching a release's download payload.
/// Implementations live outside Core (e.g. Arbitarr.Sources.NzbHydra) — this
/// project defines the contract only, per the pluggability guarantee (AC6).
/// </summary>
public interface IUpstreamSource
{
    /// <summary>A stable, unique name identifying this source instance.</summary>
    string Name { get; }

    /// <summary>Executes a search against the upstream source.</summary>
    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the capabilities advertised by the upstream source for one protocol family.
    /// <paramref name="protocol"/> is required for the same reason it is on
    /// <see cref="SearchQuery"/> (#99): an upstream may answer caps differently per endpoint —
    /// NZBHydra2's torznab endpoint advertises only its torrent indexers' categories — so the caps
    /// a caller is shown must come from the endpoint that caller's searches will actually hit.
    /// </summary>
    Task<SourceCaps> GetCapsAsync(SearchProtocol protocol, CancellationToken cancellationToken = default);

    /// <summary>Fetches the raw download payload (torrent file or NZB) for a given release.</summary>
    Task<Stream> FetchDownloadAsync(ReleaseCandidate release, CancellationToken cancellationToken = default);
}
