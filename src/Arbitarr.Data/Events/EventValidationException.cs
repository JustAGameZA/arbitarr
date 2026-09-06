namespace Arbitarr.Data.Events;

/// <summary>
/// Thrown when a proposed <see cref="Entities.EventEntry"/> write fails validation at the
/// repository boundary. Mirrors <see cref="Arbitarr.Core.Settings.SettingsValidationException"/>'s
/// posture (also followed by #53's SourceRepository): reject with a clear reason, never silently
/// coerce or clamp (AC24).
/// </summary>
public sealed class EventValidationException : Exception
{
    public EventValidationException(string message)
        : base(message)
    {
    }
}
