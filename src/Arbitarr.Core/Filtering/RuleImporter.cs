using Arbitarr.Core.Settings;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Marks the caller's reason for invoking <see cref="RuleImporter.Import"/>. There is no default
/// value and no "automatic" member on purpose (M4-4): a caller must explicitly assert
/// <see cref="UserInitiated"/> for every call, so an import can never happen implicitly (e.g. from
/// a scheduled job or an ambient event) without a code change at the call site.
/// </summary>
public enum ImportIntent
{
    /// <summary>The import was explicitly triggered by an operator (e.g. clicking "Import" in the UI).</summary>
    UserInitiated = 1,
}

/// <summary>
/// Parses the text format produced by <see cref="RuleExporter"/> back into <see cref="FilterRule"/>
/// instances (Q3-D). Import is strictly user-initiated: this type only ever runs when a caller
/// explicitly invokes it (e.g. an operator clicking "Import" in the UI) — nothing in Core schedules
/// or triggers an import automatically, so imported rule state can never silently race with rules
/// edited live through the API. This is enforced, not just documented: <see cref="Import"/> requires
/// an explicit <see cref="ImportIntent"/> argument with no default and throws for any value other
/// than <see cref="ImportIntent.UserInitiated"/>.
/// </summary>
public static class RuleImporter
{
    /// <summary>
    /// Parses <paramref name="text"/> (the format produced by <see cref="RuleExporter.Export"/>)
    /// into an ordered list of <see cref="FilterRule"/>s. Requires <paramref name="intent"/> to be
    /// <see cref="ImportIntent.UserInitiated"/>; any other value throws <see cref="ArgumentException"/>
    /// (M4-4). Throws <see cref="FormatException"/> for a malformed line (wrong field count) or an
    /// out-of-range <see cref="Precedence"/> value, and propagates <see cref="ArgumentException"/>
    /// from <see cref="FilterRule"/>'s own validation (blank name, invalid regex) — a partially-valid
    /// import is rejected outright rather than silently applying the rules that happened to parse.
    /// </summary>
    public static IReadOnlyList<FilterRule> Import(string text, ImportIntent intent)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (intent != ImportIntent.UserInitiated)
        {
            throw new ArgumentException($"Import is rejected unless explicitly user-initiated (got '{intent}').", nameof(intent));
        }

        var rules = new List<FilterRule>();
        var lineNumber = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var fields = SplitEscaped(line);
            if (fields.Count != 4)
            {
                throw new FormatException($"Line {lineNumber}: expected 4 fields, found {fields.Count}.");
            }

            if (!bool.TryParse(fields[1], out var isAllow))
            {
                throw new FormatException($"Line {lineNumber}: invalid boolean '{fields[1]}' for isAllow.");
            }

            if (!Enum.TryParse<Precedence>(fields[2], out var precedence) || !Enum.IsDefined(precedence))
            {
                throw new FormatException($"Line {lineNumber}: invalid precedence '{fields[2]}'.");
            }

            // M4 review finding (MEDIUM): reject an over-length pattern outright rather than accept
            // it and let it fail later at persistence — matches DbContext's HasMaxLength(1024).
            if (fields[3].Length > SettingsValidator.FilterRulePatternMaxLength)
            {
                throw new FormatException(
                    $"Line {lineNumber}: pattern must be <= {SettingsValidator.FilterRulePatternMaxLength} characters, found {fields[3].Length}.");
            }

            rules.Add(new FilterRule(fields[0], isAllow, precedence, fields[3]));
        }

        return rules;
    }

    private static List<string> SplitEscaped(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '\\' || line[i + 1] == '|'))
            {
                current.Append(line[i + 1]);
                i++;
                continue;
            }

            if (c == '|')
            {
                fields.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }
}
