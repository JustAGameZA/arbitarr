using Arbitarr.Core.Filtering;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves Q3-D: export/import round-trips a profile's rules exactly, and that import is a plain,
/// explicitly-invoked function — nothing in <see cref="RuleExporter"/>/<see cref="RuleImporter"/>
/// runs on a schedule or reacts to any ambient event, so applying an imported rule set is always a
/// deliberate, single call a caller chooses to make.
/// </summary>
public class RuleExportImportTests
{
    [Fact]
    public void Export_ThenImport_RoundTripsRulesExactly()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.High, "CAM|TS"),
            new FilterRule("allow-trusted", isAllow: true, Precedence.Highest, "REPACK"),
        });

        var exported = RuleExporter.Export(profile);
        var imported = RuleImporter.Import(exported, ImportIntent.UserInitiated);

        Assert.Equal(profile.Rules.Count, imported.Count);
        for (var i = 0; i < profile.Rules.Count; i++)
        {
            var original = (FilterRule)profile.Rules[i];
            var round = imported[i];
            Assert.Equal(original.Name, round.Name);
            Assert.Equal(original.IsAllow, round.IsAllow);
            Assert.Equal(original.Precedence, round.Precedence);
            Assert.Equal(original.PatternText, round.PatternText);
        }
    }

    [Fact]
    public void Export_EscapesPipeCharacters_InNameAndPattern()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny|pipe", isAllow: false, Precedence.Normal, "a|b"),
        });

        var exported = RuleExporter.Export(profile);
        var imported = RuleImporter.Import(exported, ImportIntent.UserInitiated);

        Assert.Single(imported);
        Assert.Equal("deny|pipe", imported[0].Name);
        Assert.Equal("a|b", imported[0].PatternText);
    }

    [Fact]
    public void Import_MalformedLine_ThrowsFormatException_RejectsWholeImport()
    {
        var badText = "only-two|fields\n";

        Assert.Throws<FormatException>(() => RuleImporter.Import(badText, ImportIntent.UserInitiated));
    }

    [Fact]
    public void Import_InvalidPrecedence_ThrowsFormatException()
    {
        var badText = "name|false|NotATier|pattern\n";

        Assert.Throws<FormatException>(() => RuleImporter.Import(badText, ImportIntent.UserInitiated));
    }

    [Fact]
    public void Import_WithoutExplicitUserInitiatedIntent_IsRejected()
    {
        var profile = new FilterProfile("default", new[]
        {
            new FilterRule("deny-cam", isAllow: false, Precedence.High, "CAM|TS"),
        });
        var exported = RuleExporter.Export(profile);

        Assert.Throws<ArgumentException>(() => RuleImporter.Import(exported, (ImportIntent)0));
    }

    /// <summary>
    /// M4 review finding (MEDIUM): unbounded regex pattern length. A pattern one character over the
    /// 1024-char bound (<see cref="Settings.SettingsValidator.FilterRulePatternMaxLength"/>) is
    /// rejected outright, matching the file's "reject the whole import" convention for malformed
    /// lines.
    /// </summary>
    [Fact]
    public void Import_PatternOverMaxLength_ThrowsFormatException()
    {
        var pattern = new string('a', 1025);
        var text = $"name|false|Normal|{pattern}\n";

        Assert.Throws<FormatException>(() => RuleImporter.Import(text, ImportIntent.UserInitiated));
    }

    /// <summary>Boundary companion to the above: exactly 1024 characters is accepted.</summary>
    [Fact]
    public void Import_PatternAtMaxLength_IsAccepted()
    {
        var pattern = new string('a', 1024);
        var text = $"name|false|Normal|{pattern}\n";

        var imported = RuleImporter.Import(text, ImportIntent.UserInitiated);

        var rule = Assert.Single(imported);
        Assert.Equal(pattern, rule.PatternText);
    }

    /// <summary>
    /// M4 security review (MEDIUM, follow-up): unbounded aggregate rule-evaluation time is fixed in
    /// two halves — <see cref="FilterProfile.TotalEvaluationBudget"/> bounds evaluation time at
    /// query time for any profile (including ones grandfathered before this bound existed), and
    /// this write-time bound stops a new over-large profile from being imported in the first place.
    /// A profile one rule over <see cref="Settings.SettingsValidator.MaxRulesPerProfile"/> is
    /// rejected outright, matching the file's "reject the whole import" convention.
    /// </summary>
    [Fact]
    public void Import_RuleCountOverMax_ThrowsArgumentException()
    {
        var text = string.Concat(Enumerable.Repeat(
            "name|false|Normal|pattern\n",
            Settings.SettingsValidator.MaxRulesPerProfile + 1));

        Assert.Throws<ArgumentException>(() => RuleImporter.Import(text, ImportIntent.UserInitiated));
    }

    /// <summary>Boundary companion to the above: exactly the max rule count is accepted.</summary>
    [Fact]
    public void Import_RuleCountAtMax_IsAccepted()
    {
        var text = string.Concat(Enumerable.Repeat(
            "name|false|Normal|pattern\n",
            Settings.SettingsValidator.MaxRulesPerProfile));

        var imported = RuleImporter.Import(text, ImportIntent.UserInitiated);

        Assert.Equal(Settings.SettingsValidator.MaxRulesPerProfile, imported.Count);
    }
}
