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
/// Keep the interface NARROW for that reason: every method added here is one every decorator has to
/// forward correctly, and a forwarding mistake is invisible at the call site.</para>
///
/// <para><b>arb-x7w8.14 added the SECOND method, and the cost is stated rather than hidden.</b>
/// <see cref="ResolveNzbAccessModeAsync"/> widened an interface whose own doc asked to stay at one
/// method. Three alternatives were weighed and rejected: a property on
/// <see cref="IUpstreamSource"/> (that contract is deliberately minimal, and an access mode is a
/// ROW concern rather than an adapter one); a second <c>SourceRepository</c> read keyed by display
/// name at the download route (arb-kfe9 records display-name keying as the wrong key, and it would
/// be a SECOND read of the table that could disagree with the one the adapter came from); and
/// widening <see cref="IUpstreamSource.FetchDownloadAsync"/> into a result type (arb-ywcj: redirect
/// mode branches BEFORE the adapter is called, so a redirect field could never be populated by a
/// proxy-mode call). What remains is this. The registry is already the authority that pairs an
/// adapter with the <c>Source</c> row it was built from — by row id, not by name — so it is the one
/// place that can answer this question from the SAME resolution that produced the adapter.</para>
/// </remarks>
public interface ISourceRegistry
{
    /// <summary>
    /// The enabled sources, in the order a search should present them. Empty is an ordinary answer:
    /// an install with nothing configured, or with everything disabled, searches nothing.
    /// </summary>
    Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The <c>NzbAccessMode</c> of the row behind the resolved source named
    /// <paramref name="sourceName"/>: <c>"Proxy"</c> (Arbitarr fetches the payload upstream and
    /// serves the bytes, so the indexer key never leaves the server) or <c>"Redirect"</c> (Arbitarr
    /// answers with a <c>Location</c> pointing at the upstream URL, which carries the indexer key to
    /// the client).
    /// </summary>
    /// <remarks>
    /// <para><b>IT RETURNS THE ONE STRING, NOT THE ROW, AND THAT NARROWNESS IS THE POINT.</b> Handing
    /// back a <c>Source</c> would put every operator-supplied field on that row in reach of a route
    /// whose only question is which of two answers to give. The download route is
    /// <c>PublicRead</c> and un-gated beyond the client key, so the less of the row it can touch the
    /// smaller the surface through which something could reach a response.</para>
    ///
    /// <para><b>An unmatched name answers <c>"Proxy"</c> — never null, never a throw.</b> Proxy is
    /// the mode that does NOT expose the key, so the fallback fails CLOSED: a source that cannot be
    /// matched to a row is served by proxying, not by redirecting with a credential attached. The
    /// download route has already resolved the source and answered 404 if it could not, so this
    /// branch should be unreachable from there — and an unreachable branch that would leak a key if
    /// it ever did execute is not one to leave to chance.</para>
    ///
    /// <para>Keyed by <paramref name="sourceName"/> because that is the identity the download route
    /// already holds — a persisted release carries its source's NAME, not its row id — and it is the
    /// same value the route just matched its <see cref="IUpstreamSource"/> on. Implementations must
    /// answer from the SAME resolution <see cref="ResolveAsync"/> served, so the mode and the adapter
    /// can never come from two different reads of the table.</para>
    /// </remarks>
    Task<string> ResolveNzbAccessModeAsync(string sourceName, CancellationToken cancellationToken);
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
/// <param name="sources">The fixed adapter set.</param>
/// <param name="nzbAccessModes">
/// Per-source-name overrides for <see cref="ISourceRegistry.ResolveNzbAccessModeAsync"/>, or null
/// for the default. A caller that says nothing gets <c>"Proxy"</c> for every source — see the
/// remarks on <see cref="ResolveNzbAccessModeAsync"/> for why that default, and not "unset", is the
/// right one for a type with no rows behind it.
/// </param>
public sealed class StaticSourceRegistry(
    IReadOnlyList<IUpstreamSource> sources,
    IReadOnlyDictionary<string, string>? nzbAccessModes = null) : ISourceRegistry
{
    /// <summary>
    /// The mode a source with no explicit override is served in. Spelled out here rather than
    /// referenced from <c>SourceRepository.ProxyAccessMode</c> because <c>Arbitarr.Core</c> does not
    /// depend on <c>Arbitarr.Data</c>; the two are pinned equal by
    /// <c>SourceRepositoryTests.The_proxy_access_mode_constant_matches_the_registry_default</c>.
    /// </summary>
    public const string DefaultNzbAccessMode = "Proxy";

    private readonly IReadOnlyList<IUpstreamSource> _sources =
        sources ?? throw new ArgumentNullException(nameof(sources));

    private readonly IReadOnlyDictionary<string, string> _nzbAccessModes =
        nzbAccessModes ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An empty set — the shape an unconfigured install resolves to.</summary>
    public static readonly StaticSourceRegistry Empty = new(Array.Empty<IUpstreamSource>());

    public Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_sources);

    /// <inheritdoc />
    /// <remarks>
    /// <para>Defaults to <see cref="DefaultNzbAccessMode"/> for a name with no override, which is the
    /// interface's own fail-closed rule applied to a type that has no rows to consult. It also keeps
    /// every existing caller — the ~20 integration tests that construct this with an adapter list
    /// alone — meaning exactly what they meant before: proxy mode, the only mode that existed.</para>
    /// </remarks>
    public Task<string> ResolveNzbAccessModeAsync(string sourceName, CancellationToken cancellationToken) =>
        Task.FromResult(
            _nzbAccessModes.TryGetValue(sourceName, out var mode) ? mode : DefaultNzbAccessMode);
}
