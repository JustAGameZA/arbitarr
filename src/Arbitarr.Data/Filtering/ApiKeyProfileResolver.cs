using Arbitarr.Core.Filtering;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Filtering;

/// <summary>
/// Resolves the resolved client name (from <c>ClientKeyContext.Name</c>, surfaced by Host's
/// <c>ConfiguredClientApiKeyResolver</c>) to the <see cref="FilterProfile"/> that should apply to
/// that client's searches, via <see cref="Entities.ApiKeyProfileEntry"/> (M4-3, A3). A null, blank,
/// or unknown client name — or a client name with no matching <see cref="Entities.ApiKeyProfileEntry"/>
/// row — falls back to <see cref="FilterProfileLoader.LoadDefaultProfileAsync"/>, the same
/// pass-through-when-unconfigured default used before per-client resolution existed. Lives in
/// Arbitarr.Data rather than Arbitarr.Core because Core has zero references to the persistence
/// layer (AC6); mirrors <see cref="FilterProfileLoader"/>.
/// </summary>
public sealed class ApiKeyProfileResolver
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly FilterProfileLoader _profileLoader;

    public ApiKeyProfileResolver(ArbitarrDbContext dbContext, FilterProfileLoader profileLoader)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _profileLoader = profileLoader ?? throw new ArgumentNullException(nameof(profileLoader));
    }

    /// <summary>
    /// Resolves <paramref name="clientName"/> to its mapped <see cref="FilterProfile"/>, or the
    /// default profile when <paramref name="clientName"/> is null/blank or has no mapping.
    /// </summary>
    public async Task<FilterProfile> ResolveAsync(string? clientName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientName))
        {
            return await _profileLoader.LoadDefaultProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        // M4 review finding (LOW): FirstOrDefaultAsync rather than SingleOrDefaultAsync — the
        // ApiKeyName unique index should prevent duplicates, but if one somehow exists (e.g. a
        // manual DB edit) this fails open (picks one) instead of 500ing the whole search request.
        var mapping = await _dbContext.ApiKeyProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ApiKeyName == clientName, cancellationToken)
            .ConfigureAwait(false);

        if (mapping is null)
        {
            return await _profileLoader.LoadDefaultProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        var profileEntry = await _dbContext.FilterProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == mapping.FilterProfileId, cancellationToken)
            .ConfigureAwait(false);

        if (profileEntry is null)
        {
            return await _profileLoader.LoadDefaultProfileAsync(cancellationToken).ConfigureAwait(false);
        }

        var ruleEntries = await _dbContext.FilterRules
            .AsNoTracking()
            .Where(r => r.FilterProfileId == profileEntry.Id && r.Enabled)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rules = ruleEntries
            .Select(r => (IFilterRule)new FilterRule(r.Name, r.IsAllow, (Precedence)r.Precedence, r.Pattern))
            .ToList();

        return new FilterProfile(profileEntry.Name, rules, profileEntry.IsDefault);
    }
}
