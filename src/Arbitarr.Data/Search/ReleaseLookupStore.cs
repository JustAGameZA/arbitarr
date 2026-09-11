using System.Text.Json;
using Arbitarr.Core.Releases;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Search;

/// <summary>
/// arb-tps: EF Core-backed <see cref="IReleaseLookupStore"/> over the
/// <see cref="ReleaseLookupEntry"/> table. Kept outside Arbitarr.Core so Core stays free of any
/// reference to Arbitarr.Data (AC6); Core defines only the persistence-agnostic contract.
///
/// <para>The connection string is NOT formatted here — this takes an
/// <see cref="ArbitarrDbContext"/> whose options the composition root built from
/// <c>DatabaseConnectionStrings</c> (docs/standards/data.md:59, enforced by
/// <c>NoInlineDatabaseConnectionStringsTests</c>), exactly as every other store in this project
/// does.</para>
/// </summary>
public sealed class ReleaseLookupStore : IReleaseLookupStore
{
    /// <summary>
    /// Matches <c>Arbitarr.Api.Search.SearchResultCacheStage</c>'s options, so a candidate
    /// serialized by either path deserializes by the other. A round trip must preserve the
    /// candidate byte-exactly enough that the source's fetch-time origin re-validation still sees
    /// the link it issued.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _ttl;

    public ReleaseLookupStore(ArbitarrDbContext dbContext, TimeSpan ttl, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ttl = ttl;
    }

    public async Task UpsertRangeAsync(IEnumerable<StoredRelease> releases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var batch = releases.ToList();
        if (batch.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + _ttl;

        // One query for the whole batch rather than one per release: a search renders up to a
        // page of results and this runs on every search, so a per-row round trip would be the
        // dominant cost of the write.
        var guids = batch.Select(r => r.ProxyGuid).ToList();
        var existing = await _dbContext.ReleaseLookupEntries
            .Where(e => guids.Contains(e.ProxyGuid))
            .ToDictionaryAsync(e => e.ProxyGuid, cancellationToken)
            .ConfigureAwait(false);

        foreach (var release in batch)
        {
            var payloadJson = JsonSerializer.Serialize(release.Candidate, SerializerOptions);

            if (existing.TryGetValue(release.ProxyGuid, out var entry))
            {
                // Re-recording an already-known guid REFRESHES the row rather than adding a second
                // one. The unique index makes a duplicate insert an error rather than a silent
                // second answer, and extending the expiry is the point: a release that is still
                // being returned by searches is still one an *arr may grab.
                entry.SourceName = release.SourceName;
                entry.PayloadJson = payloadJson;
                entry.RecordedAt = now;
                entry.ExpiresAt = expiresAt;
            }
            else
            {
                _dbContext.ReleaseLookupEntries.Add(new ReleaseLookupEntry
                {
                    ProxyGuid = release.ProxyGuid,
                    SourceName = release.SourceName,
                    PayloadJson = payloadJson,
                    RecordedAt = now,
                    ExpiresAt = expiresAt,
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proxyGuid);

        var entry = await _dbContext.ReleaseLookupEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.ProxyGuid == proxyGuid, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return null;
        }

        // Expiry is evaluated on every read rather than trusted to the prune, for the same reason
        // SessionRepository evaluates session expiry on lookup: a maintenance pass running late, or
        // not at all, must never make an expired row resolvable again.
        if (entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        var candidate = JsonSerializer.Deserialize<ReleaseCandidate>(entry.PayloadJson, SerializerOptions);
        return candidate is null ? null : new StoredRelease(entry.ProxyGuid, entry.SourceName, candidate);
    }
}
