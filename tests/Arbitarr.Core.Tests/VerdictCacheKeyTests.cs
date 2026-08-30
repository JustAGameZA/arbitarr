using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Step 7 / R17: the verdict cache key is a hash of (normalized title + size + source + protocol),
/// explicitly not <see cref="ReleaseCandidate.Guid"/>, carrying model name/digest/prompt version.
/// </summary>
public sealed class VerdictCacheKeyTests
{
    private static ReleaseCandidate MakeCandidate(string guid, string title = "Movie.Title.2024.1080p.BluRay.x264") => new()
    {
        Title = title,
        Guid = guid,
        PubDate = DateTimeOffset.UtcNow,
        Size = 4_000_000_000,
        Link = new Uri("https://example.invalid/release"),
        Protocol = ProtocolKind.Torrent,
    };

    [Fact]
    public void Compute_TwoDifferentGuids_SameReleaseAttributes_CollideToOneKey()
    {
        var candidateA = MakeCandidate(Guid.NewGuid().ToString());
        var candidateB = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidateA, "indexer-1", "gpt-x", "digest-1", "prompt-v1");
        var keyB = VerdictCacheKey.Compute(candidateB, "indexer-1", "gpt-x", "digest-1", "prompt-v1");

        Assert.NotEqual(candidateA.Guid, candidateB.Guid);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelName_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-y", "digest-1", "prompt-v1");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelDigest_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-2", "prompt-v1");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentPromptVersion_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v2");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_TitleCasingAndWhitespaceDifferences_StillCollide()
    {
        var candidateA = MakeCandidate(Guid.NewGuid().ToString(), "Movie.Title.2024.1080p.BluRay.x264");
        var candidateB = MakeCandidate(Guid.NewGuid().ToString(), "  movie.title.2024.1080p.bluray.x264  ");

        var keyA = VerdictCacheKey.Compute(candidateA, "indexer-1", "gpt-x", "digest-1", "prompt-v1");
        var keyB = VerdictCacheKey.Compute(candidateB, "indexer-1", "gpt-x", "digest-1", "prompt-v1");

        Assert.Equal(keyA, keyB);
    }
}
