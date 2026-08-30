using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// M5-9/R17: a model-name (or digest/prompt-version) change must invalidate previously cached
/// verdicts by construction — <see cref="VerdictCacheKey.Compute"/> must produce a different key,
/// so a stale verdict computed under an old model is never served as if it came from the new one.
/// </summary>
public class VerdictCacheKeyModelIdentityTests
{
    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Movie.2024.1080p.WEB-DL",
        Guid = "guid-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Size = 123456789,
        Protocol = ProtocolKind.Torrent,
    };

    [Fact]
    public void Compute_DifferentModelName_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-b", "digest-1", "v1");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelDigest_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-2", "v1");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentPromptVersion_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v2");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_SameModelIdentity_ProducesStableKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1");

        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Compute_DoesNotDependOnGuid_SameReleaseDifferentGuid_ProducesSameKey()
    {
        var candidateA = Candidate();
        var candidateB = new ReleaseCandidate
        {
            Title = candidateA.Title,
            Guid = "a-completely-different-rotating-guid",
            PubDate = candidateA.PubDate,
            Link = candidateA.Link,
            Size = candidateA.Size,
            Protocol = candidateA.Protocol,
        };

        var keyA = VerdictCacheKey.Compute(candidateA, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidateB, "TestSource", "model-a", "digest-1", "v1");

        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Compute_SameNormalizedTitleDifferentOriginalTitle_ProducesDistinctKeys()
    {
        // M5 security review (LOW): two releases can normalize/noise-strip to the same Title (e.g.
        // a stripped "RARBG" suffix) while their OriginalTitle differs. Keying on Title instead of
        // OriginalTitle would collide these to one cache entry and silently reuse one release's
        // verdict for a different one; keying on OriginalTitle keeps them distinct.
        var candidateA = new ReleaseCandidate
        {
            Title = "Movie.2024.1080p.WEB-DL",
            OriginalTitleRaw = "Movie.2024.1080p.WEB-DL-RARBG",
            Guid = "guid-a",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("https://example.invalid/r"),
            Size = 123456789,
            Protocol = ProtocolKind.Torrent,
        };
        var candidateB = new ReleaseCandidate
        {
            Title = "Movie.2024.1080p.WEB-DL",
            OriginalTitleRaw = "Movie.2024.1080p.WEB-DL-OtherGroup",
            Guid = "guid-b",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("https://example.invalid/r"),
            Size = 123456789,
            Protocol = ProtocolKind.Torrent,
        };

        var keyA = VerdictCacheKey.Compute(candidateA, "TestSource", "model-a", "digest-1", "v1");
        var keyB = VerdictCacheKey.Compute(candidateB, "TestSource", "model-a", "digest-1", "v1");

        Assert.NotEqual(keyA, keyB);
    }
}
