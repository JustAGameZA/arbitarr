namespace Arbitarr.Core.Releases;

/// <summary>
/// arb-tps: durable storage for the release lookup — the tier that lets
/// <c>/download/{proxyGuid}</c> resolve a guid after a restart, or after the in-memory tier's
/// 30-minute TTL has elapsed.
///
/// <para>Defined in Core, implemented in Arbitarr.Data (<c>ReleaseLookupStore</c>), for the same
/// reason <c>IVerdictCacheReader</c> is: Core carries no reference to the persistence layer (AC6),
/// so the contract lives here and EF Core stays on the other side of it.</para>
///
/// <para>This deals in <see cref="ReleaseCandidate"/> plus a source name rather than in the
/// Api layer's <c>RenderedRelease</c>, because Core cannot see Arbitarr.Api and because the
/// candidate plus the source name is exactly what resolving a download needs.</para>
/// </summary>
public interface IReleaseLookupStore
{
    /// <summary>
    /// Records every release in <paramref name="releases"/>, replacing any row already held for the
    /// same proxy guid rather than adding a second one (the guid is uniquely indexed). Called once
    /// per search with the whole post-filter set, never on the download hot path.
    /// </summary>
    Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves <paramref name="proxyGuid"/> to its stored release, or <see langword="null"/> when
    /// no row is held or the row it holds has expired. An expired row is treated as absent here and
    /// deleted later by the maintenance pass — a read must never depend on the prune having run.
    /// </summary>
    Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default);
}

/// <summary>
/// One stored release: the upstream source that produced it, and the candidate needed to fetch its
/// download. The pairing <see cref="IReleaseLookupStore"/> reads and writes.
/// </summary>
/// <param name="ProxyGuid">The proxy guid this release is resolved by.</param>
/// <param name="SourceName">Name of the upstream source that produced it.</param>
/// <param name="Candidate">The release payload, carried byte-exact through persistence.</param>
public sealed record StoredRelease(string ProxyGuid, string SourceName, ReleaseCandidate Candidate);
