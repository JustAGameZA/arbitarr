namespace Arbitarr.Data.Entities;

/// <summary>
/// Cached AI verdict for a release (schema created at Step 5, populated by Step 6's AI layer).
/// The cache key (<see cref="ReleaseKeyHash"/>) is deliberately a hash of (normalized title +
/// size + source + protocol) — NOT the release <c>guid</c>, which can rotate per-request for the
/// same underlying release on some indexers (R17) — carrying the model identity/prompt version so
/// a model upgrade invalidates rather than silently mixing verdicts from different models.
/// </summary>
public sealed class VerdictCacheEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// Hash of (normalized title + size + source + protocol). Explicitly NOT the release guid
    /// (R17) — see type-level remarks.
    /// </summary>
    public required string ReleaseKeyHash { get; set; }

    /// <summary>Name of the AI model that produced this verdict (e.g. "llama3.1:8b").</summary>
    public required string ModelName { get; set; }

    /// <summary>Digest/hash of the model weights, so a silent model swap invalidates the cache.</summary>
    public required string ModelDigest { get; set; }

    /// <summary>Version of the prompt template used to produce this verdict.</summary>
    public required string PromptVersion { get; set; }

    /// <summary>The verdict produced (serialized as its integer value).</summary>
    public int Verdict { get; set; }

    /// <summary>Model-reported confidence score for this verdict, in [0, 1].</summary>
    public double Confidence { get; set; }

    /// <summary>When this verdict was computed.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this row was last read (drives LRU eviction against the row ceiling).</summary>
    public DateTimeOffset LastAccessedAt { get; set; }
}
