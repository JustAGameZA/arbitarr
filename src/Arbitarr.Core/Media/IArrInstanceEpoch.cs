namespace Arbitarr.Core.Media;

/// <summary>
/// arb-iiy: a counter bumped whenever the *arr instance configuration is written; consumers fold
/// <see cref="Current"/> into cache keys so a repoint invalidates without a settings read.
/// </summary>
/// <remarks>
/// <para><b>WHY A COUNTER RATHER THAN THE ADDRESS ITSELF.</b> The obvious alternative — folding the
/// stored base URL into the key — reintroduces exactly the cost the cache exists to avoid: the key
/// cannot be built without reading the address, so every memo HIT would pay a database round trip,
/// which is the one thing <c>SeriesTitleResolver</c>'s memo-first comment forbids. A monotonic
/// counter is readable from memory, so the hit path stays free.</para>
///
/// <para><b>NO EVICTION SWEEP.</b> A bump does not remove the entries written under the previous
/// epoch; it makes them unreachable. They expire on their own TTL exactly as they always did, so
/// there is nothing to schedule and nothing to prune. The only cost is that a handful of dead
/// entries linger for up to their remaining lifetime in a cache sized for far more than that.</para>
///
/// <para><b>NOT STATIC.</b> The value is instance state on a DI singleton rather than a static
/// field, deliberately: a static would fail <c>ProductionProcessGlobalStateTests</c> (#216), and
/// tests that build the pieces directly need to be able to hold their own epoch rather than share
/// one across a test class.</para>
/// </remarks>
public interface IArrInstanceEpoch
{
    /// <summary>
    /// The current epoch. Changes — to a value not previously seen — every time <see cref="Bump"/>
    /// is called, and never otherwise.
    /// </summary>
    long Current { get; }

    /// <summary>
    /// Advances the epoch, so every cache key folding <see cref="Current"/> in stops matching the
    /// entries written before this call. Called AFTER a successful configuration write, never
    /// before: a write that was rejected changed nothing, so invalidating on it would throw away a
    /// valid cache for no reason.
    /// </summary>
    void Bump();
}
