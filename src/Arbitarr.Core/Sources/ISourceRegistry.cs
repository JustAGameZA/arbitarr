namespace Arbitarr.Core.Sources;

/// <summary>
/// The sources a search fans out to, resolved when they are asked for rather than fixed when the
/// process started (arb-x7w8.4).
/// </summary>
/// <remarks>
/// <para><b>WHY CONSUMERS TAKE THIS AND NOT <c>IReadOnlyList&lt;IUpstreamSource&gt;</c>.</b> Until
/// arb-x7w8.4 the composition root registered exactly ONE source, built at startup from a single
/// resolved configuration, so a list injected by constructor was as live as anything could be. With
/// the source set stored in the database and editable from the admin UI, it no longer is: adding,
/// removing or disabling an indexer must take effect on the next request, not the next restart.</para>
///
/// <para><b>The resolution is ASYNC because it reads the database, and that is what forces this
/// shape.</b> A constructor-injected list has to be produced by a DI factory delegate, which cannot
/// be <c>async</c> — so the only way to keep the list shape would be to block a thread-pool thread
/// inside that factory, on every scope and therefore on every search. That is banned outright in
/// <c>Arbitarr.Host</c> by <c>HostBlockingAsyncCallTests</c>, for a measured reason: it starved the
/// pool and surfaced as the arb-agh flake, an intermittent unattributable timeout. Moving the
/// <c>await</c> to the point of use is what removes the block rather than relocating it.</para>
///
/// <para><b>ONE RESOLUTION PER SCOPE.</b> Implementations memoise within the scope, so two
/// consumers in one request (the merge stage and the caps aggregator, say) resolve the same
/// instances and do not read every source's API key twice. Across scopes the memo is gone, which is
/// what keeps an operator's edit visible on the next request.</para>
///
/// <para><b>THIS IS THE DECORATION BOUNDARY.</b> arb-x7w8.10's per-indexer budgets and durable
/// backoff wrap HERE — a decorator over this interface can drop a source that is at its limit, or
/// one whose auth has failed permanently, without any consumer knowing it was ever a candidate.
/// Keep the interface at one method for that reason.</para>
/// </remarks>
public interface ISourceRegistry
{
    /// <summary>
    /// The enabled sources, in the order a search should present them. Empty is an ordinary answer:
    /// an install with nothing configured, or with everything disabled, searches nothing.
    /// </summary>
    Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A fixed set of sources, for callers that already have their instances — tests, and any composition
/// that genuinely has nothing to resolve.
/// </summary>
/// <remarks>
/// It exists so that "the sources are fixed" stays expressible after the interface change, rather
/// than every such caller having to write its own one-line implementation. Production uses the
/// database-backed registry in <c>Arbitarr.Host</c>; this is deliberately not that.
/// </remarks>
public sealed class StaticSourceRegistry(IReadOnlyList<IUpstreamSource> sources) : ISourceRegistry
{
    private readonly IReadOnlyList<IUpstreamSource> _sources =
        sources ?? throw new ArgumentNullException(nameof(sources));

    /// <summary>An empty set — the shape an unconfigured install resolves to.</summary>
    public static readonly StaticSourceRegistry Empty = new(Array.Empty<IUpstreamSource>());

    public Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_sources);
}
