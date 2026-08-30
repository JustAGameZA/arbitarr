using Arbitarr.Api.Rendering;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Filtering;
using Arbitarr.Data.Settings;

namespace Arbitarr.Api.Search;

/// <summary>
/// Wires the M4 deterministic filter engine into the live search path: resolves the default
/// <see cref="FilterProfile"/> and current <see cref="Core.Settings.SettingKey.ShadowMode"/>/
/// <see cref="Core.Settings.SettingKey.AiConfidenceThreshold"/> settings, runs every merged release
/// through <see cref="SuppressionPrecedenceChain.Evaluate"/> one at a time (not
/// <c>EvaluateBatch</c> — <see cref="ReleaseCandidate.Guid"/> is only unique per upstream source,
/// so per-release evaluation keyed by list position avoids misattributing a suppression across two
/// different sources that happen to reuse the same source-provided guid), applies
/// <see cref="ShadowModeGate"/> per release, persists one <see cref="Entities.SuppressionAuditLogEntry"/>
/// per suppression (M4-5: zero suppressions go unrecorded, keyed by <see cref="RenderedRelease.ProxyGuid"/>
/// for audit identity), and returns the annotated <see cref="RenderedRelease"/> set for rendering.
///
/// Invariants preserved: title/size/category/guid are never rewritten (M1-4) — only inclusion and
/// the additive <see cref="RenderedRelease.SuppressionAnnotation"/> are decided here. Precedence is
/// always allow &gt; deny &gt; AI &gt; pass via <see cref="SuppressionPrecedenceChain"/> — never
/// reordered, never bypassed.
/// </summary>
public sealed class FilterStage
{
    private readonly ApiKeyProfileResolver _profileResolver;
    private readonly SettingsReader _settingsReader;
    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public FilterStage(
        ApiKeyProfileResolver profileResolver,
        SettingsReader settingsReader,
        ArbitarrDbContext dbContext,
        TimeProvider timeProvider)
    {
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _settingsReader = settingsReader ?? throw new ArgumentNullException(nameof(settingsReader));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Applies the filter engine to <paramref name="releases"/> for one search request
    /// (<paramref name="queryKey"/> identifies it in the audit trail). <paramref name="clientName"/>
    /// is the resolved caller identity (M4-3, A3) — null/blank/unknown resolves to the default
    /// profile via <see cref="ApiKeyProfileResolver"/>. Returns the releases that should be
    /// rendered, in original order, each annotated when a suppression source matched it.
    /// </summary>
    public async Task<IReadOnlyList<RenderedRelease>> ApplyAsync(
        IReadOnlyList<RenderedRelease> releases,
        string queryKey,
        string? clientName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releases);

        if (releases.Count == 0)
        {
            return releases;
        }

        var profile = await _profileResolver.ResolveAsync(clientName, cancellationToken).ConfigureAwait(false);
        var shadowMode = await _settingsReader.GetShadowModeAsync(cancellationToken).ConfigureAwait(false);
        var aiConfidenceThreshold = await _settingsReader.GetAiConfidenceThresholdAsync(cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        var output = new List<RenderedRelease>(releases.Count);
        var auditEntries = new List<SuppressionAuditLogEntry>();

        foreach (var release in releases)
        {
            var chainResult = SuppressionPrecedenceChain.Evaluate(profile, release.Candidate, aiConfidenceThreshold);

            if (chainResult.Verdict != Verdict.Reject)
            {
                output.Add(release with { SuppressionAnnotation = null });
                continue;
            }

            // Audit identity uses ProxyGuid (source name + upstream guid) as the release's globally
            // unique identifier — Candidate.Guid alone is only unique within one upstream source (M1).
            var identity = new ReleaseIdentity(release.SourceName, release.ProxyGuid);
            // M4 review finding (LOW): `reason` (and `queryKey`, embedded below) contains raw
            // user-supplied search query text. This is fine for the local audit DB, but neither
            // value must ever be forwarded to an external log sink verbatim.
            var reason = chainResult.RuleName is not null
                ? $"Suppressed by {chainResult.Source} '{chainResult.RuleName}' (profile '{profile.Name}', query '{queryKey}')."
                : $"Suppressed by {chainResult.Source} (profile '{profile.Name}', query '{queryKey}').";
            var record = new SuppressionRecord(identity, reason, now);
            var tagged = new ShadowTaggedSuppression(record, shadowMode);

            var ruleName = chainResult.RuleName ?? chainResult.Source.ToString().ToLowerInvariant();
            auditEntries.Add(SuppressionAuditLogMapper.ToEntry(tagged, queryKey, ruleName));

            if (shadowMode)
            {
                // AC12/D3: shadow mode re-admits the release, annotated, rather than withholding it.
                output.Add(release with { SuppressionAnnotation = reason });
            }
            // else: enforced — the release is withheld entirely (M4-7 "with shadow OFF, absent").
        }

        if (auditEntries.Count > 0)
        {
            _dbContext.SuppressionAuditLogEntries.AddRange(auditEntries);
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return output;
    }
}
