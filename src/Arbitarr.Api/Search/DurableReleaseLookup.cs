using System.Text.Json;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Search;

/// <summary>
/// Main-database-backed download-proxy registry. Entries have a fixed lifetime so a copied URL
/// does not remain usable indefinitely, while surviving process restarts and in-memory eviction.
/// </summary>
public sealed class DurableReleaseLookup : IProxyGuidReleaseRegistry
{
    /// <summary>Fixed retention for a release link issued by search.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public DurableReleaseLookup(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RecordRangeAsync(IEnumerable<RenderedRelease> releases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);

        var expiresAt = _timeProvider.GetUtcNow().Add(Retention);
        foreach (var release in releases)
        {
            var entry = await _dbContext.ProxyGuidReleaseEntries
                .SingleOrDefaultAsync(e => e.ProxyGuid == release.ProxyGuid, cancellationToken)
                .ConfigureAwait(false);

            if (entry is null)
            {
                entry = new ProxyGuidReleaseEntry { ProxyGuid = release.ProxyGuid, SourceName = release.SourceName, CandidateJson = string.Empty };
                _dbContext.ProxyGuidReleaseEntries.Add(entry);
            }

            entry.SourceName = release.SourceName;
            entry.CandidateJson = JsonSerializer.Serialize(release.Candidate, SerializerOptions);
            entry.ExpiresAt = expiresAt;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RenderedRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken = default)
    {
        var entry = await _dbContext.ProxyGuidReleaseEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.ProxyGuid == proxyGuid, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null || entry.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        var candidate = JsonSerializer.Deserialize<ReleaseCandidate>(entry.CandidateJson, SerializerOptions);
        return candidate is null ? null : new RenderedRelease(entry.SourceName, candidate);
    }
}