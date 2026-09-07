namespace Arbitarr.Data.Security;

/// <summary>
/// Thrown when a proposed <see cref="Entities.ApiKeyEntry"/> write fails validation at the
/// repository boundary. Mirrors <see cref="Sources.SourceValidationException"/> and
/// <see cref="Arbitarr.Core.Settings.SettingsValidationException"/>: reject with a clear reason,
/// never silently coerce or clamp (AC24).
///
/// The lockout refusal (#58 AC5 — revoking the last full-scope key) is raised as one of these too,
/// so the endpoint has ONE catch translating a refusal into a 400 with the repository's own
/// message. A second exception type for it would mean two floors saying the same thing.
/// </summary>
public sealed class ApiKeyValidationException : Exception
{
    public ApiKeyValidationException(string message)
        : base(message)
    {
    }
}
