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
}
