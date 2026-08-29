using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// Applies the global shadow-mode toggle (<c>Arbitarr.Core.Settings.SettingKey.ShadowMode</c>,
/// D3) to a suppression source's output. When shadow mode is on, suppressions are recorded
/// (for inspection, P3) but never enforced: every candidate that would have been suppressed is
/// returned to the caller as a survivor instead, and each corresponding
/// <see cref="SuppressionRecord"/> is annotated so downstream audit logging
/// (<c>Arbitarr.Data.Entities.SuppressionAuditLogEntry.ShadowMode</c>) can tell recorded-only
/// decisions apart from enforced ones. This type takes the setting's already-resolved boolean
/// value rather than reading the settings store itself, keeping Core free of any dependency on
/// the persistence layer (AC6).
/// </summary>
public static class ShadowModeGate
{
    /// <summary>
    /// Given the original candidate list, a rule engine's result over it, and whether shadow mode
    /// is currently enabled, returns the effective survivors (all original candidates when shadow
    /// mode is on, since nothing is actually withheld) alongside every suppression that occurred,
    /// each tagged with the shadow-mode flag that was in effect. <paramref name="originalCandidates"/>
    /// is required (not optional) because <see cref="RuleEngineResult"/> does not retain the
    /// original <see cref="ReleaseCandidate"/> for suppressed entries — only survivors and each
    /// suppression's <see cref="ReleaseIdentity"/> — so re-admitting a suppressed candidate under
    /// shadow mode is only possible against the caller's original list.
    /// </summary>
    public static ShadowModeResult Apply(
        IReadOnlyList<ReleaseCandidate> originalCandidates,
        RuleEngineResult result,
        bool shadowModeEnabled)
    {
        ArgumentNullException.ThrowIfNull(originalCandidates);
        ArgumentNullException.ThrowIfNull(result);

        var tagged = result.Suppressions
            .Select(s => new ShadowTaggedSuppression(s, shadowModeEnabled))
            .ToList();

        var effectiveSurvivors = shadowModeEnabled ? originalCandidates : result.Survivors;
        return new ShadowModeResult(effectiveSurvivors, tagged);
    }
}

/// <summary>A <see cref="SuppressionRecord"/> annotated with whether shadow mode was active when it was produced.</summary>
/// <param name="Record">The underlying suppression record.</param>
/// <param name="ShadowMode">True if the decision was recorded but not enforced.</param>
public sealed record ShadowTaggedSuppression(SuppressionRecord Record, bool ShadowMode);

/// <summary>Result of applying <see cref="ShadowModeGate"/> to a <see cref="RuleEngineResult"/>.</summary>
/// <param name="EffectiveCandidates">Candidates that are actually returned to the caller.</param>
/// <param name="Suppressions">Every suppression that occurred, tagged with shadow-mode state.</param>
public sealed record ShadowModeResult(
    IReadOnlyList<ReleaseCandidate> EffectiveCandidates,
    IReadOnlyList<ShadowTaggedSuppression> Suppressions);
