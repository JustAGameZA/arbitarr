namespace Arbitarr.Host.Sources;

/// <summary>
/// The NZBHydra2 source configuration as read from <c>Arbitarr:Sources:NzbHydra</c>, i.e. the
/// environment/appsettings input that was the sole configuration mechanism before #53 stage 53b.
///
/// After 53b this type is *only* seed material and divergence-comparison material. It is read once
/// at startup and is never a fallback: see <see cref="SourceSeeder"/> for why the DB is the sole
/// authority once any source row exists.
/// </summary>
/// <param name="BaseUrl">Raw base URL string, defaulted by the caller when the setting is absent.</param>
/// <param name="ApiKey">Upstream credential. Never logged, never echoed — see AC2 and #43.</param>
/// <param name="SourceName">Stable source name, used as the seeded row's display name.</param>
/// <param name="BaseUrlWasSupplied">
/// Whether <c>BaseUrl</c> came from configuration rather than the compiled-in default. The
/// divergence warning must not fire for a default that merely happens to differ from a DB row, so
/// this distinguishes "operator set nothing" from "operator set this value".
/// </param>
/// <param name="SourceNameWasSupplied">As above, for <c>SourceName</c>.</param>
public sealed record EnvironmentSourceConfiguration(
    string BaseUrl,
    string ApiKey,
    string SourceName,
    bool BaseUrlWasSupplied,
    bool SourceNameWasSupplied)
{
    /// <summary>
    /// True when the operator supplied any part of the NZBHydra2 configuration. A container with no
    /// environment variables at all yields false, which is precisely the 2026-09-07 incident shape:
    /// under 53b that state seeds nothing and, crucially, breaks nothing.
    /// </summary>
    public bool AnySettingSupplied =>
        BaseUrlWasSupplied || SourceNameWasSupplied || !string.IsNullOrWhiteSpace(ApiKey);
}
