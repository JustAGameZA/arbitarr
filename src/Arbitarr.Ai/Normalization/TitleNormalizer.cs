using System.Text.RegularExpressions;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Normalization;

/// <summary>
/// Applies title normalization to a <see cref="ReleaseCandidate"/>, gated by all four required
/// controls (M5-5/M5-7/M5-8):
/// <list type="number">
/// <item>Kill-switch: <see cref="Arbitarr.Core.Settings.SettingKey.TitleNormalizationEnabled"/>
/// (default OFF) — when disabled, the candidate is returned unchanged.</item>
/// <item><see cref="AllowList"/>: tokens normalization must never remove/alter.</item>
/// <item><see cref="DenyList"/>: tokens normalization is explicitly permitted to strip.</item>
/// <item><see cref="DifferentialParseGuard"/> (AC26): a post-hoc structural check that no
/// allow-listed token was lost; on failure, the original title is kept.</item>
/// </list>
/// Never mutates <see cref="ReleaseCandidate.Title"/> in place — it returns a new candidate with
/// <see cref="ReleaseCandidate.OriginalTitleRaw"/> set to the pre-normalization title and
/// <see cref="ReleaseCandidate.Title"/> set to the normalized form, per the Architect S3 mechanism.
/// </summary>
public sealed class TitleNormalizer
{
    private readonly AllowList _allowList;
    private readonly DenyList _denyList;

    public TitleNormalizer(AllowList? allowList = null, DenyList? denyList = null)
    {
        _allowList = allowList ?? new AllowList();
        _denyList = denyList ?? new DenyList();
    }

    /// <summary>
    /// Normalizes <paramref name="candidate"/>'s title when <paramref name="normalizationEnabled"/>
    /// is <see langword="true"/>; otherwise returns <paramref name="candidate"/> unchanged
    /// (kill-switch, default OFF). If normalization runs but the differential-parse guard fails,
    /// also returns <paramref name="candidate"/> unchanged (fail-safe: never serve a title that
    /// lost identity-relevant tokens).
    /// </summary>
    public ReleaseCandidate Normalize(ReleaseCandidate candidate, bool normalizationEnabled)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!normalizationEnabled)
        {
            return candidate;
        }

        var original = candidate.Title;
        var normalized = StripDenyListedTokens(original);

        if (!DifferentialParseGuard.Passes(original, normalized, _allowList))
        {
            return candidate;
        }

        if (string.Equals(normalized, original, StringComparison.Ordinal))
        {
            return candidate;
        }

        return CloneWithTitle(candidate, normalized, original);
    }

    private string StripDenyListedTokens(string title)
    {
        var tokens = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = tokens.Where(token => !_denyList.Contains(Regex.Replace(token, @"[\[\]().]", string.Empty)));
        return string.Join(' ', kept);
    }

    private static ReleaseCandidate CloneWithTitle(ReleaseCandidate candidate, string normalizedTitle, string originalTitle) => new()
    {
        Title = normalizedTitle,
        OriginalTitleRaw = originalTitle,
        Guid = candidate.Guid,
        PubDate = candidate.PubDate,
        Size = candidate.Size,
        Link = candidate.Link,
        Category = candidate.Category,
        Protocol = candidate.Protocol,
        InfoHash = candidate.InfoHash,
        Seeders = candidate.Seeders,
        Peers = candidate.Peers,
        MinimumRatio = candidate.MinimumRatio,
        MinimumSeedTime = candidate.MinimumSeedTime,
        UsenetGroup = candidate.UsenetGroup,
        PasswordProtected = candidate.PasswordProtected,
        Files = candidate.Files,
        Grabs = candidate.Grabs,
    };
}
