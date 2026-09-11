namespace Arbitarr.Data.Entities;

/// <summary>
/// arb-tps: the durable half of the release lookup — one row per rendered release, keyed by the
/// proxy guid <c>SearchEndpoint</c> emitted, so <c>/download/{proxyGuid}</c> can still resolve it
/// after the process restarts or after the in-memory tier's 30-minute TTL has elapsed. Before this
/// table existed the lookup was process-lifetime memory only, so every restart (and every grab a
/// delay profile deferred past half an hour) answered 404.
///
/// <para><see cref="PayloadJson"/> carries the serialized <c>ReleaseCandidate</c> ONLY — the whole
/// candidate, because <c>IUpstreamSource.FetchDownloadAsync</c> takes one and NzbHydraSource
/// re-validates its <c>Link</c> against the configured origin at fetch time. It deliberately does
/// not carry the suppression annotation: <c>DownloadProxyEndpoint</c> reads only the source name and
/// the candidate, and an annotation is presentation metadata with no bearing on resolving a
/// download.</para>
///
/// <para><see cref="SourceName"/> is its own column rather than a field inside the payload so
/// resolving a guid can pick the source without deserializing, and so an operator reading the table
/// can see which source a row came from.</para>
///
/// <para>NO SOURCE API KEY IS STORED HERE. The candidate's <c>Link</c> is the upstream release URL
/// exactly as the source reported it, and is persisted VERBATIM, re-validated against the pinned
/// origin at fetch time (see <see cref="PayloadJson"/> above); a source API key lives only in the
/// write-only <c>source:{id}:api_key</c> settings rows and is attached at fetch time by the
/// source's own client, never persisted on this row. <c>ReleaseLookupPayloadSecretTests</c> holds
/// that line with a planted-secret positive control.</para>
/// </summary>
public sealed class ReleaseLookupEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The proxy guid emitted for this release (<c>ReleaseGuid.Compute</c> over source name +
    /// upstream guid). Uniquely indexed: it is the resolution key, and two rows answering one guid
    /// would make "which release is this?" unanswerable.
    /// </summary>
    public required string ProxyGuid { get; set; }

    /// <summary>Name of the upstream source that produced this release.</summary>
    public required string SourceName { get; set; }

    /// <summary>The serialized <c>ReleaseCandidate</c>. See the type-level remarks on what it carries.</summary>
    public required string PayloadJson { get; set; }

    /// <summary>When this row was last written by a search.</summary>
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>When this row stops resolving, and becomes prunable. Derived at write time from the release lookup TTL setting.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
