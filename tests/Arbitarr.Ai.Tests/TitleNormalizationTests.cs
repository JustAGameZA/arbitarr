using Arbitarr.Ai.Normalization;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// M5-5/M5-7: exercises <see cref="AllowList"/>/<see cref="DenyList"/> membership behavior and
/// <see cref="TitleNormalizer"/>'s use of them end to end (deny-listed tokens stripped, allow-listed
/// tokens never targeted for stripping, idempotence when nothing changes).
/// </summary>
public class TitleNormalizationTests
{
    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public void AllowList_DefaultContains_QualityAndCodecTokens()
    {
        var allowList = new AllowList();

        Assert.True(allowList.Contains("1080p"));
        Assert.True(allowList.Contains("x265"));
        Assert.True(allowList.Contains("HDR10"));
    }

    [Fact]
    public void AllowList_Contains_IsCaseInsensitive()
    {
        var allowList = new AllowList();

        Assert.True(allowList.Contains("1080P"));
    }

    [Fact]
    public void DenyList_DefaultContains_KnownNoiseTokens()
    {
        var denyList = new DenyList();

        Assert.True(denyList.Contains("RARBG"));
        Assert.True(denyList.Contains("yify"));
    }

    [Fact]
    public void Normalize_StripsOnlyDenyListedTokens()
    {
        var denyList = new DenyList(new[] { "RARBG" });
        var normalizer = new TitleNormalizer(allowList: null, denyList);
        var candidate = Candidate("Movie 2024 1080p RARBG");

        var result = normalizer.Normalize(candidate, normalizationEnabled: true);

        Assert.Equal("Movie 2024 1080p", result.Title);
    }

    [Fact]
    public void Normalize_NoDenyListedTokensPresent_ReturnsSameCandidateInstance()
    {
        var denyList = new DenyList(new[] { "RARBG" });
        var normalizer = new TitleNormalizer(allowList: null, denyList);
        var candidate = Candidate("Movie 2024 1080p WEB-DL");

        var result = normalizer.Normalize(candidate, normalizationEnabled: true);

        Assert.Same(candidate, result);
    }

    [Fact]
    public void Normalize_CustomAllowAndDenyLists_AreRespected()
    {
        var allowList = new AllowList(new[] { "SPECIALTAG" });
        var denyList = new DenyList(new[] { "NOISE" });
        var normalizer = new TitleNormalizer(allowList, denyList);
        var candidate = Candidate("Show SPECIALTAG NOISE");

        var result = normalizer.Normalize(candidate, normalizationEnabled: true);

        Assert.Equal("Show SPECIALTAG", result.Title);
        Assert.Equal("Show SPECIALTAG NOISE", result.OriginalTitleRaw);
    }
}
