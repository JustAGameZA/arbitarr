using Arbitarr.Core.Diagnostics;

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

    /// <summary>
    /// Renders every member in full except the key, which is
    /// <see cref="CredentialPatterns.Replacement"/> (arb-1ox9).
    /// </summary>
    /// <remarks>
    /// <para><b>THIS IS THE MECHANISM BEHIND <see cref="ApiKey"/>'s "Never logged, never echoed".</b>
    /// A comment cannot fail, and this type's whole job is divergence comparison at startup — the
    /// exact shape that attracts a <c>$"seed config {configuration} differs from the stored row"</c>
    /// diagnostic. The synthesised <c>ToString</c> on a positional record prints every member by name
    /// and value, so that line would have written the environment-supplied key into the persistent
    /// log store served at <c>/api/admin/logs</c> (#65).</para>
    ///
    /// <para>The <c>IHttpClientFactory</c> URI redaction does not cover it (that is scoped to an
    /// outbound request's query string), and while <c>LogMessageCleanser</c> does scrub a rendered
    /// <c>ApiKey = …</c>, <b>it runs only in the LOG SINK</b> (CLAUDE.md §1) — an exception message
    /// or console line carrying this record never meets it. Its coverage is name-dependent too: see
    /// <c>Arbitarr.Data.Security.CreatedApiKey.ToString</c> for the sibling spelling that matches no
    /// arm of it.</para>
    ///
    /// <para>The two <c>WasSupplied</c> flags are rendered because they are what a divergence line is
    /// actually about — whether the operator set a value — and they carry no secret.</para>
    /// </remarks>
    public override string ToString() =>
        $"{nameof(EnvironmentSourceConfiguration)} {{ {nameof(BaseUrl)} = {BaseUrl}, "
        + $"{nameof(ApiKey)} = {CredentialPatterns.Replacement}, "
        + $"{nameof(SourceName)} = {SourceName}, "
        + $"{nameof(BaseUrlWasSupplied)} = {BaseUrlWasSupplied}, "
        + $"{nameof(SourceNameWasSupplied)} = {SourceNameWasSupplied} }}";
}
