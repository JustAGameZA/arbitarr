using Arbitarr.Ai.Normalization;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// AC26: <see cref="DifferentialParseGuard"/> must fail whenever an allow-listed (identity-relevant)
/// token present in the original title is missing from the normalized title, and
/// <see cref="TitleNormalizer.Normalize"/> must fall back to the original, untouched title whenever
/// the guard fails — never serving a title that silently lost a protected token.
/// </summary>
public class DifferentialParseTests
{
    private static ReleaseCandidate Candidate(string title) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
    };

    [Fact]
    public void Passes_AllowListedTokenSurvives_ReturnsTrue()
    {
        var allowList = new AllowList();

        var result = DifferentialParseGuard.Passes(
            "Movie.2024.1080p.RARBG", "Movie.2024.1080p", allowList);

        Assert.True(result);
    }

    [Fact]
    public void Passes_AllowListedTokenDropped_ReturnsFalse()
    {
        var allowList = new AllowList();

        var result = DifferentialParseGuard.Passes(
            "Movie.2024.1080p.RARBG", "Movie.2024.RARBG", allowList);

        Assert.False(result);
    }

    [Fact]
    public void Passes_NonAllowListedTokenDropped_StillReturnsTrue()
    {
        var allowList = new AllowList();

        var result = DifferentialParseGuard.Passes(
            "Movie.2024.1080p.RARBG", "Movie.2024.1080p", allowList);

        Assert.True(result);
    }

    [Fact]
    public void Normalize_GuardFails_FallsBackToOriginalTitle()
    {
        // A deny-listed token positioned so its removal would also remove an adjacent allow-listed
        // token's exact form is not needed here: instead, use an allow list that protects the
        // deny-listed token itself, forcing a guard failure via conflicting controls.
        var allowList = new AllowList(new[] { "RARBG" });
        var denyList = new DenyList(new[] { "RARBG" });
        var normalizer = new TitleNormalizer(allowList, denyList);
        var candidate = Candidate("Movie.2024.1080p.RARBG");

        var result = normalizer.Normalize(candidate, normalizationEnabled: true);

        Assert.Same(candidate, result);
        Assert.Null(result.OriginalTitleRaw);
        Assert.Equal("Movie.2024.1080p.RARBG", result.Title);
    }

    [Fact]
    public void Normalize_GuardPasses_ProducesNormalizedTitleWithOriginalPreserved()
    {
        var denyList = new DenyList(new[] { "RARBG" });
        var normalizer = new TitleNormalizer(allowList: null, denyList);
        var candidate = Candidate("Movie 2024 1080p RARBG");

        var result = normalizer.Normalize(candidate, normalizationEnabled: true);

        Assert.NotSame(candidate, result);
        Assert.Equal("Movie 2024 1080p", result.Title);
        Assert.Equal("Movie 2024 1080p RARBG", result.OriginalTitleRaw);
        Assert.Equal("Movie 2024 1080p RARBG", result.OriginalTitle);
    }
}
