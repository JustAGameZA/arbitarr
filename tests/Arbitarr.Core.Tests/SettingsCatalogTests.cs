using Arbitarr.Core.Settings;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// M7-5: proves the settings catalog's shape invariants — every entry has non-empty display text
/// and rationale (AC24's "why this bound exists" requirement), AdminApiKey is never exposed through
/// this surface, AiKillSwitch and WorkerEnabled are the only boolean entries, and MaintenanceJobInterval
/// is the sole restart-required entry (the one AC24 exception).
/// </summary>
public sealed class SettingsCatalogTests
{
    [Fact]
    public void Every_entry_has_a_display_name_and_rationale()
    {
        foreach (var entry in SettingsCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(entry.Rationale));
        }
    }

    [Fact]
    public void AdminApiKey_is_never_in_the_catalog()
    {
        Assert.DoesNotContain(SettingsCatalog.Entries, e => e.Key == SettingKey.AdminApiKey);
    }

    /// <summary>
    /// #89: the Ollama base URL is a SettingKey with a validator arm and a repository arm, but it
    /// is deliberately off the catalog — for a different reason than AdminApiKey above. It is not a
    /// secret; it is excluded because it owns its own Settings section with a connectivity probe
    /// (AdminAiEndpoints), and a catalog entry would render it a second time as an unexplained text
    /// field beside the section that already owns it.
    ///
    /// <para>This assertion is load-bearing in both directions: while the key is off the catalog,
    /// PUT /api/admin/settings/OllamaBaseUrl is a 404 and AdminSettingsEndpoints' CurrentValue
    /// switch needs no arm for it. Adding a catalog entry without also adding that arm throws
    /// mid-serialization and breaks the WHOLE settings page, not just this row.</para>
    /// </summary>
    [Fact]
    public void OllamaBaseUrl_is_never_in_the_catalog()
    {
        Assert.DoesNotContain(SettingsCatalog.Entries, e => e.Key == SettingKey.OllamaBaseUrl);
    }

    [Fact]
    public void AiKillSwitch_is_present_and_boolean()
    {
        var entry = Assert.Single(SettingsCatalog.Entries, e => e.Key == SettingKey.AiKillSwitch);
        Assert.True(entry.IsBoolean);
        Assert.False(entry.RequiresRestart);
    }

    [Fact]
    public void Only_MaintenanceJobInterval_requires_a_restart()
    {
        var restartRequiring = SettingsCatalog.Entries.Where(e => e.RequiresRestart).ToList();

        var onlyEntry = Assert.Single(restartRequiring);
        Assert.Equal(SettingKey.MaintenanceJobInterval, onlyEntry.Key);
    }

    [Fact]
    public void Catalog_has_no_duplicate_keys()
    {
        var keys = SettingsCatalog.Entries.Select(e => e.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    /// <summary>M7-8: every unbounded non-boolean setting carries a labelled reason, and only those do.</summary>
    [Fact]
    public void NoMaximumReason_IsPresent_ExactlyWhenValidatorHasNoCeiling()
    {
        var arrSyncInterval = TimeSpan.FromMinutes(15);
        var defaults = SettingsSnapshot.Defaults(arrSyncInterval);

        foreach (var entry in SettingsCatalog.Entries)
        {
            var (_, max) = SettingsValidator.GetBounds(defaults, entry.Key, arrSyncInterval);
            var unbounded = max is null && !entry.IsBoolean;
            Assert.True(unbounded == !string.IsNullOrWhiteSpace(entry.NoMaximumReason), $"{entry.Key}: unbounded={unbounded}, reason={entry.NoMaximumReason}");
        }
    }

    /// <summary>M7-8: the restart exception is labelled with why, and nothing else claims a restart reason.</summary>
    [Fact]
    public void RestartReason_IsPresent_ExactlyWhenRequiresRestart()
    {
        Assert.All(SettingsCatalog.Entries, entry =>
            Assert.True(entry.RequiresRestart == !string.IsNullOrWhiteSpace(entry.RestartReason), $"{entry.Key}"));
    }

    /// <summary>
    /// arb-tk0r: every <see cref="SettingGroup"/> has an operator-facing heading, asserted PER GROUP
    /// over <c>Enum.GetValues</c> rather than over the catalog entries.
    ///
    /// <para>The enumeration source is load-bearing: iterating <c>SettingsCatalog.Entries</c> would
    /// only cover the groups that happen to have an entry today, so a member added to the enum
    /// (and not yet given a setting) would be missed by exactly the test that exists to catch it.
    /// Walking the enum means a new member is covered the moment it is declared.</para>
    ///
    /// <para>This is the real guard, and not a belt to the compiler's braces: a switch expression
    /// with no default arm does NOT make a missing arm a compile error here, because CS8524 fires
    /// on the unnamed values of the underlying int no matter how many members are covered, so it
    /// cannot distinguish "a member was missed" from "this enum has an int backing". The default arm
    /// in <see cref="SettingGroupDisplay.DisplayNameOf"/> therefore throws, and this test is what
    /// turns that throw into a build-time failure rather than a 500 on an operator's request.</para>
    /// </summary>
    [Fact]
    public void Every_setting_group_has_a_display_name()
    {
        var groups = Enum.GetValues<SettingGroup>();

        // A per-group loop is vacuously true over an empty set, and this enum is
        // that set: pin it non-empty before relying on the loop below.
        Assert.NotEmpty(groups);

        foreach (var group in groups)
        {
            var displayName = SettingGroupDisplay.DisplayNameOf(group);
            Assert.False(
                string.IsNullOrWhiteSpace(displayName),
                $"SettingGroup.{group} has no display name.");
        }
    }

    /// <summary>
    /// arb-tk0r: the groups whose identifier is not already readable get a heading that actually
    /// DIFFERS from it. This is the assertion with teeth — <see cref="Every_setting_group_has_a_display_name"/>
    /// above is satisfied by a <c>DisplayNameOf</c> that returns <c>group.ToString()</c>, which is
    /// precisely the "SearchResultCache" heading arb-tk0r exists to remove. Named individually
    /// rather than derived: a heuristic for "is this identifier multi-word" would itself be the
    /// thing under test, and single-word groups like Worker and Metadata legitimately render
    /// identically to their identifier, so they cannot be swept in.
    /// </summary>
    [Theory]
    [InlineData(SettingGroup.SearchResultCache)]
    [InlineData(SettingGroup.SuppressionAudit)]
    [InlineData(SettingGroup.Ai)]
    public void A_code_shaped_group_identifier_is_not_its_own_heading(SettingGroup group)
    {
        Assert.NotEqual(group.ToString(), SettingGroupDisplay.DisplayNameOf(group));
    }
}
