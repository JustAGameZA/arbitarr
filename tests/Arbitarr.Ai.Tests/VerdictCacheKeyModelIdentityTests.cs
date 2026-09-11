using System.Globalization;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// arb-qg3o: the decoding identity token itself — the Ai-layer half of the fix. The token is what
/// travels into <see cref="AiModelIdentity"/>, so these pin both its exact shape (a change to it is
/// a cache invalidation and must be a deliberate edit, not a drive-by reformat) and the fact that it
/// is genuinely DERIVED from <see cref="OllamaOptions"/>' constants rather than a literal that could
/// silently stop tracking them.
/// </summary>
public class OllamaDecodingIdentityTests
{
    [Fact]
    public void DecodingIdentity_MatchesTheSamplingConstantsInForce()
    {
        Assert.Equal("t0-s42", OllamaOptions.DecodingIdentity);
    }

    /// <summary>
    /// The assertion above is a literal, which would keep passing if the token were hardcoded and
    /// the constants moved on. This one rebuilds the expected token FROM the constants, so the pair
    /// fails whichever side drifts: change a constant without the token following and this fails;
    /// change the format without intent and the literal above fails.
    /// </summary>
    [Fact]
    public void DecodingIdentity_IsDerivedFromBothConstants()
    {
        var expected = string.Create(
            CultureInfo.InvariantCulture,
            $"t{OllamaOptions.SamplingTemperature}-s{OllamaOptions.SamplingSeed}");

        Assert.Equal(expected, OllamaOptions.DecodingIdentity);
        Assert.Contains(OllamaOptions.SamplingSeed.ToString(CultureInfo.InvariantCulture), OllamaOptions.DecodingIdentity);
    }
}

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

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-b", "digest-1", "v1", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelDigest_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-2", "v1", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentPromptVersion_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v2", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_SameModelIdentity_ProducesStableKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

        Assert.Equal(keyA, keyB);
    }

    /// <summary>
    /// arb-qg3o: the whole point of the fourth component. A decoding change (temperature or seed)
    /// must invalidate cached verdicts on its own, with every other component held identical — so
    /// no configuration value, including a pinned <c>Arbitarr:Ai:PromptVersion</c>, can keep a
    /// verdict decoded under the old sampling in service.
    /// </summary>
    [Fact]
    public void Compute_DifferentDecodingIdentity_ProducesDifferentKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0.7-s42");

        Assert.NotEqual(keyA, keyB);
    }

    /// <summary>
    /// Positive control for the test above: with the decoding identity ALSO held equal, these same
    /// arguments produce one key. Without this, <c>NotEqual</c> above would still pass if some other
    /// component were accidentally varying, and the test would not be evidence about decoding at all.
    /// </summary>
    [Fact]
    public void Compute_SameDecodingIdentity_ProducesStableKey()
    {
        var candidate = Candidate();

        var keyA = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

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

        var keyA = VerdictCacheKey.Compute(candidateA, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidateB, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

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

        var keyA = VerdictCacheKey.Compute(candidateA, "TestSource", "model-a", "digest-1", "v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidateB, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }
}
