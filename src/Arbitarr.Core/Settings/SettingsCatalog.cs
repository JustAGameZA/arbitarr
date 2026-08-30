namespace Arbitarr.Core.Settings;

/// <summary>
/// Fixed, code-defined defaults for settings that must have a known value on a fresh install
/// before any row exists in the persisted settings store (M4-8) — most notably
/// <see cref="SettingKey.ShadowMode"/>, which D3 requires to default **ON** so a brand-new
/// deployment never silently enforces suppression before the operator has reviewed it. Pure and
/// reference-free of persistence (AC6): the settings repository (owned elsewhere) is expected to
/// fall back to <see cref="GetDefault"/> when no <c>Arbitarr.Data.Entities.SettingEntry</c> row
/// exists for a key, rather than this type reading the database itself.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>
    /// Returns the code-defined default for <paramref name="key"/>, as the exact CLR type the
    /// caller is expected to interpret the setting's value as (see each <see cref="SettingKey"/>
    /// member's own XML doc for the type/semantics). Throws <see cref="ArgumentOutOfRangeException"/>
    /// for a key with no declared default — every settings key must have one.
    /// </summary>
    public static object GetDefault(SettingKey key) => key switch
    {
        SettingKey.ShadowMode => true,
        SettingKey.AiConfidenceThreshold => 0.9,
        SettingKey.TitleNormalizationEnabled => false,
        SettingKey.ClassifierPollInterval => TimeSpan.FromMinutes(1),
        _ => throw new ArgumentOutOfRangeException(
            nameof(key), key, $"No default declared in {nameof(SettingsCatalog)} for '{key}'."),
    };
}
