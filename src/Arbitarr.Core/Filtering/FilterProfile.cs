namespace Arbitarr.Core.Filtering;

/// <summary>
/// A named, ordered collection of <see cref="IFilterRule"/>s (Step 5). Distinct API keys can
/// resolve to distinct profiles (A3); a profile flagged <see cref="IsDefault"/> applies when no
/// API key mapping exists. Purely an in-memory rule set — persistence is owned by
/// <c>Arbitarr.Data.Entities.FilterProfileEntry</c>/<c>FilterRuleEntry</c> outside Core.
/// </summary>
public sealed class FilterProfile
{
    /// <summary>
    /// Constructs a profile from a name and its rules. Rules are copied into a fixed-order list;
    /// mutating the caller's original collection after construction has no effect on this profile.
    /// </summary>
    public FilterProfile(string name, IEnumerable<IFilterRule> rules, bool isDefault = false)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Profile name must not be blank.", nameof(name));
        }

        Name = name;
        IsDefault = isDefault;
        Rules = rules.ToList();
    }

    /// <summary>Unique, human-readable profile name.</summary>
    public string Name { get; }

    /// <summary>Whether this is the fallback profile used when no API key mapping matches.</summary>
    public bool IsDefault { get; }

    /// <summary>The rules belonging to this profile, in the order they were supplied.</summary>
    public IReadOnlyList<IFilterRule> Rules { get; }
}
