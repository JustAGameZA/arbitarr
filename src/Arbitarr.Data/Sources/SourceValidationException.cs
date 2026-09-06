namespace Arbitarr.Data.Sources;

/// <summary>
/// Thrown when a proposed <see cref="Entities.Source"/> write fails validation at the repository
/// boundary. Mirrors <see cref="Arbitarr.Core.Settings.SettingsValidationException"/>'s posture:
/// reject with a clear reason, never silently coerce or clamp (AC24).
/// </summary>
public sealed class SourceValidationException : Exception
{
    public SourceValidationException(string message)
        : base(message)
    {
    }
}
