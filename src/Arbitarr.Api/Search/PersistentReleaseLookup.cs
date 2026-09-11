using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Search;

/// <summary>
/// arb-tps: the two-tier <see cref="IReleaseLookup"/> the download proxy resolves through —
/// <see cref="InMemoryReleaseLookup"/> first, the durable <see cref="IReleaseLookupStore"/> on a
/// miss, repopulating memory from a store hit so a second download of the same release is answered
/// without touching the database again.
///
/// <para>WHY BOTH TIERS, RATHER THAN REPLACING THE FIRST. The memory tier remains the hot path and
/// the download proxy's zero-DB promise still holds for the common case (a grab shortly after the
/// search that produced it). The store exists for the two cases memory cannot cover, and they are
/// the defect this type was written for: the process restarts — 29 times in 25 hours on the
/// reporting instance, each restart emptying the dictionary — and *arr delay profiles routinely
/// defer a grab past the memory tier's 30-minute TTL. Either one answered 404 on a link this
/// application had itself issued.</para>
///
/// <para>The miss path is not the hot path, which is what makes the extra round trip acceptable:
/// by definition it runs only when memory has already failed, and the alternative outcome there is
/// a failed download rather than a faster one.</para>
///
/// <para>A store failure must never be worse than the old behaviour, so a miss that throws is
/// answered as a miss (a 404, exactly as before this type existed) rather than propagating as a 500
/// out of the download proxy.</para>
/// </summary>
public sealed class PersistentReleaseLookup : IReleaseLookup
{
    private readonly InMemoryReleaseLookup _memory;
    private readonly Func<string, CancellationToken, Task<StoredRelease?>> _findInStore;

    /// <param name="memory">The in-process fast path, and the tier a store hit repopulates.</param>
    /// <param name="findInStore">
    /// Performs one durable lookup, owning whatever scope it needs for the duration of the call.
    /// A delegate over the whole operation rather than an injected <see cref="IReleaseLookupStore"/>
    /// because this type is a SINGLETON (the download proxy resolves one instance for the life of
    /// the process) while the store holds a scoped <c>ArbitarrDbContext</c>: capturing a store at
    /// construction would hand every later download the first request's context, and handing back a
    /// store without its scope would leak the scope or dispose the context out from under it. The
    /// caller creating and disposing the scope around its own query is the only shape that keeps
    /// both lifetimes correct.
    /// </param>
    public PersistentReleaseLookup(
        InMemoryReleaseLookup memory,
        Func<string, CancellationToken, Task<StoredRelease?>> findInStore)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _findInStore = findInStore ?? throw new ArgumentNullException(nameof(findInStore));
    }

    public async Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default)
    {
        var fromMemory = await _memory.FindAsync(proxyGuid, cancellationToken).ConfigureAwait(false);
        if (fromMemory is not null)
        {
            return fromMemory;
        }

        StoredRelease? stored;
        try
        {
            stored = await _findInStore(proxyGuid, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // See the type-level remarks: a store that cannot answer leaves the caller exactly where
            // the memory-only implementation left it, rather than turning a 404 into a 500.
            return null;
        }

        if (stored is null)
        {
            return null;
        }

        // The suppression annotation is deliberately not persisted (see ReleaseLookupEntry), so a
        // release rebuilt from the store carries none. Nothing downstream of the download proxy
        // reads it — it is presentation metadata on the search response, and this release is being
        // rebuilt to fetch a file, not to render one.
        var release = new RenderedRelease(stored.SourceName, stored.Candidate);

        // Repopulate the fast path, so a retry (an *arr that failed the first fetch) and the second
        // half of a multi-file grab do not each pay the database round trip.
        //
        // Keyed by the guid the row was STORED under, not by re-deriving one from the rebuilt
        // release. The two agree whenever the row was written by this application at its current
        // release-guid secret, and that is the normal case — but they stop agreeing the moment the
        // secret is rotated, and re-deriving would then file the entry under a key nothing ever
        // looks up. The fast path would silently never hit, sending every download back to the
        // database, which is the kind of failure that shows up as a latency mystery rather than as
        // an error. The stored guid is the one the caller asked for, so it is the correct key.
        _memory.RecordAs(proxyGuid, release);

        return release;
    }
}
