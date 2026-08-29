namespace ArrSearcher.Core.Settings;

/// <summary>
/// Thrown when a proposed setting value violates its floor or ceiling. Validation always
/// rejects out-of-bounds values with a clear error — it never silently clamps (plan lines
/// ~1034-1041).
/// </summary>
public sealed class SettingsValidationException : Exception
{
    public SettingKey Key { get; }

    public SettingsValidationException(SettingKey key, string message)
        : base(message)
    {
        Key = key;
    }
}
