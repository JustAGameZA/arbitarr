using Arbitarr.Ai.Normalization;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Settings;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// M5-8: <see cref="SettingKey.TitleNormalizationEnabled"/> must default OFF, and
/// <see cref="TitleNormalizer.Normalize"/> must return the candidate completely unchanged (not even
/// re-cloned) whenever the caller passes <c>normalizationEnabled: false</c> — the kill-switch is the
/// first control checked, before allow-list/deny-list/guard logic ever runs.
/// </summary>
public class KillSwitchTests
{
    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public void SettingsCatalog_TitleNormalizationEnabled_DefaultsOff()
    {
        var value = SettingsCatalog.GetDefault(SettingKey.TitleNormalizationEnabled);

        Assert.Equal(false, value);
    }

    [Fact]
    public void Normalize_KillSwitchOff_ReturnsSameCandidateInstance()
    {
        var normalizer = new TitleNormalizer();
        var candidate = Candidate("Movie.2024.RARBG.1080p");

        var result = normalizer.Normalize(candidate, normalizationEnabled: false);

        Assert.Same(candidate, result);
    }

    [Fact]
    public void Normalize_KillSwitchOff_DoesNotSetOriginalTitleRaw()
    {
        var normalizer = new TitleNormalizer();
        var candidate = Candidate("Movie.2024.RARBG.1080p");

        var result = normalizer.Normalize(candidate, normalizationEnabled: false);

        Assert.Null(result.OriginalTitleRaw);
        Assert.Equal(candidate.Title, result.OriginalTitle);
    }
}
