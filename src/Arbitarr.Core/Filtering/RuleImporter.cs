namespace Arbitarr.Core.Filtering;

/// <summary>
/// Parses the text format produced by <see cref="RuleExporter"/> back into <see cref="FilterRule"/>
/// instances (Q3-D). Import is strictly user-initiated: this type only ever runs when a caller
/// explicitly invokes it (e.g. an operator clicking "Import" in the UI) — nothing in Core schedules
/// or triggers an import automatically, so imported rule state can never silently race with rules
/// edited live through the API.
/// </summary>
public static class RuleImporter
{
    /// <summary>
    /// Parses <paramref name="text"/> (the format produced by <see cref="RuleExporter.Export"/>)
    /// into an ordered list of <see cref="FilterRule"/>s. Throws <see cref="FormatException"/> for
    /// a malformed line (wrong field count) or an out-of-range <see cref="Precedence"/> value, and
    /// propagates <see cref="ArgumentException"/> from <see cref="FilterRule"/>'s own validation
    /// (blank name, invalid regex) — a partially-valid import is rejected outright rather than
    /// silently applying the rules that happened to parse.
    /// </summary>
    public static IReadOnlyList<FilterRule> Import(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

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
