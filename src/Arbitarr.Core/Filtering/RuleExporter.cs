using System.Text;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// One-way export of a <see cref="FilterProfile"/>'s rules to a human-readable, line-oriented text
/// format (Q3-D). Export has no counterpart that runs automatically — pairing it with
/// <see cref="RuleImporter"/> always requires an explicit, user-initiated action, never an ambient
/// or scheduled sync, so there is never more than one authority over live rule state at a time.
/// </summary>
public static class RuleExporter
{
    /// <summary>
    /// Serializes <paramref name="profile"/>'s rules, one per line, as
    /// <c>name|isAllow|precedence|pattern</c>. Field separators (<c>|</c>) inside a rule's own name
    /// or pattern are escaped as <c>\|</c> so the format round-trips exactly via
    /// <see cref="RuleImporter.Import"/>.
    /// </summary>
    public static string Export(FilterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var builder = new StringBuilder();
        foreach (var rule in profile.Rules)
        {
            if (rule is not FilterRule filterRule)
            {
                throw new NotSupportedException(
                    $"RuleExporter only supports {nameof(FilterRule)}; got '{rule.GetType().Name}'.");
            }

            builder.Append(Escape(filterRule.Name)).Append('|')
                .Append(filterRule.IsAllow).Append('|')
                .Append(filterRule.Precedence).Append('|')
                .Append(Escape(filterRule.PatternText))
                .Append('\n');
        }

        return builder.ToString();
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("|", "\\|");
}
