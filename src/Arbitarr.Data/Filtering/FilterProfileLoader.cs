using Arbitarr.Core.Filtering;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Filtering;

/// <summary>
/// Loads the persisted default <see cref="Entities.FilterProfileEntry"/> (plus its
/// <see cref="Entities.FilterRuleEntry"/> rows) into Core's in-memory <see cref="FilterProfile"/>
/// so <see cref="SuppressionPrecedenceChain"/>/<see cref="RuleEngine"/> can evaluate against it.
/// API-key-to-profile resolution (M4-3) is out of scope here — this loader always resolves the
/// profile flagged <see cref="Entities.FilterProfileEntry.IsDefault"/>, falling back to an empty,
/// pass-through profile ("Default", no rules) when nothing is configured yet, so the live search
/// path never breaks on a fresh install with zero filter rows. Lives in Arbitarr.Data rather than
/// Arbitarr.Core because Core has zero references to the persistence layer (AC6); mirrors the
/// reverse-direction <see cref="SuppressionAuditLogMapper"/>.
/// </summary>
public sealed class FilterProfileLoader
{
    private readonly ArbitarrDbContext _dbContext;

    public FilterProfileLoader(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <summary>
    /// Loads the default <see cref="FilterProfile"/> (enabled rules only), or an empty pass-through
    /// profile named "Default" if no profile is flagged <see cref="Entities.FilterProfileEntry.IsDefault"/>.
    /// </summary>
    public async Task<FilterProfile> LoadDefaultProfileAsync(CancellationToken cancellationToken = default)
    {
        var profileEntry = await _dbContext.FilterProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.IsDefault, cancellationToken)
            .ConfigureAwait(false);

        if (profileEntry is null)
        {
            return new FilterProfile("Default", Array.Empty<IFilterRule>(), isDefault: true);
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
