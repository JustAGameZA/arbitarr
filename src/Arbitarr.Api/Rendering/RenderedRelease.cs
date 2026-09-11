using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Pairs a merged <see cref="ReleaseCandidate"/> with the upstream source name that produced
/// it, so the renderer can compute a stable <see cref="ReleaseGuid"/> (source name + upstream
/// guid) without the pipeline needing to thread source identity through the candidate itself.
/// </summary>
/// <param name="SourceName">Name of the upstream source that produced this release.</param>
/// <param name="Candidate">The release payload, rendered byte-exact (pure passthrough).</param>
/// <param name="SuppressionAnnotation">
/// Additive metadata only (M4-7) — never mutates <paramref name="Candidate"/>'s title, size,
/// category, or guid (M1-4). Null for a release that was never suppressed. When shadow mode
/// re-admits a would-be-suppressed release, this carries a human-readable reason so the response
/// still shows the release was matched by a suppression source, just not enforced.
/// </param>
public sealed record RenderedRelease(string SourceName, ReleaseCandidate Candidate, string? SuppressionAnnotation = null)
{
    /// <summary>
    /// The stable proxy guid for this release, used by DownloadProxyEndpoint.
    ///
    /// <para><b>MATERIALISED ONCE PER INSTANCE, NOT A COMPUTED PROPERTY (arb-0hd0).</b> This was
    /// <c>=> ReleaseGuid.Compute(...)</c>, re-evaluated on every access — five times per search
    /// request (FilterStage, InMemoryReleaseLookup's memory key, SearchEndpoint's persisted key,
    /// the URL handed to the client, IndexerXmlWriter). <see cref="ReleaseGuid"/>'s HMAC secret is
    /// a mutable process-global that <c>ReleaseGuid.Configure</c> rewrites, so those five
    /// evaluations were not guaranteed to agree: a <c>Configure</c> landing between the lookup-key
    /// evaluation and the URL evaluation issued a link matching NEITHER tier, and the download came
    /// back 404 with no exception and no log entry (arb-agh, four occurrences). Computing it once
    /// makes all five sites share one value BY CONSTRUCTION rather than by the secret happening to
    /// hold still, and drops four HMAC computations per release as a side effect.</para>
    ///
    /// <para>Initialised in the initialiser rather than via a lazily-populated backing field, and
    /// that choice is load-bearing for <c>with</c>-expressions. A record's <c>with</c> copies
    /// fields and then runs the initialisers for what it sets; a cached-on-first-access field would
    /// be COPIED, so <c>release with { Candidate = other }</c> would carry the original's guid
    /// while reporting a different candidate. Here the initialiser re-runs on every copy, so the
    /// value always tracks the current <see cref="SourceName"/> and <see cref="Candidate"/>.
    /// FilterStage rewrites titles with exactly that expression (<c>with { Candidate = ... }</c>);
    /// it happens to preserve the upstream guid today, but the correctness of this type must not
    /// rest on that staying true.</para>
    ///
    /// <para>Record equality is unaffected: <c>ProxyGuid</c> is not a positional member, so it is
    /// not part of the generated <c>Equals</c>/<c>GetHashCode</c> — two instances with the same
    /// three components remain equal, exactly as before.</para>
    /// </summary>
    public string ProxyGuid { get; } = ReleaseGuid.Compute(new ReleaseIdentity(SourceName, Candidate.Guid));
}
