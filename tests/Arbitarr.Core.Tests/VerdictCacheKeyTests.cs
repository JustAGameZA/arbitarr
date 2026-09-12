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

        var keyA = VerdictCacheKey.Compute(candidateA, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidateB, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");

        Assert.NotEqual(candidateA.Guid, candidateB.Guid);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelName_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-y", "digest-1", "prompt-v1", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentModelDigest_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-2", "prompt-v1", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_DifferentPromptVersion_ChangesKey()
    {
        var candidate = MakeCandidate(Guid.NewGuid().ToString());

        var keyA = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v2", "t0-s42");

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void Compute_TitleCasingAndWhitespaceDifferences_StillCollide()
    {
        var candidateA = MakeCandidate(Guid.NewGuid().ToString(), "Movie.Title.2024.1080p.BluRay.x264");
        var candidateB = MakeCandidate(Guid.NewGuid().ToString(), "  movie.title.2024.1080p.bluray.x264  ");

        var keyA = VerdictCacheKey.Compute(candidateA, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");
        var keyB = VerdictCacheKey.Compute(candidateB, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");

        Assert.Equal(keyA, keyB);
    }

    /// <summary>
    /// arb-2jti: pins <see cref="VerdictCacheKey.Compute"/> for fixed inputs to a known digest, so a
    /// silent change to the field separator (or any other part of the hashed input) is caught. The
    /// expected digest below was captured by running this same computation against the UNCHANGED
    /// implementation (the literal U+001F separator, before it was named as a documented const) —
    /// it is not recomputed from the current code, so a regression here means the cache key actually
    /// changed. No <c>InternalsVisibleTo</c> exists from Arbitarr.Core to this test project, so the
    /// new separator const is kept <c>private</c> and this digest pin is the sole guard.
    /// Updating the pinned literal is only correct when the key inputs deliberately changed (as
    /// #244 did) — never repaste a fresh digest just to make this test green, because a changed
    /// digest means every cached verdict misses once on deploy.
    ///
    /// arb-a7ll updated it for exactly that reason: poster and Usenet group became key components,
    /// so every key changed and the whole verdict cache is invalidated once on that deploy. This
    /// candidate reports no poster and no groups, so the two appended components are their
    /// "absent" encodings — which is the point, since even a release carrying neither metadata
    /// field re-keys.
    /// </summary>
    [Fact]
    public void Compute_FixedInputs_MatchesKnownDigest()
    {
        var candidate = MakeCandidate("fixed-guid", "Movie.Title.2024.1080p.BluRay.x264");

        var key = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");

        Assert.Equal("FEB371C1ECC23CEFCFE37CB863D6469D0380419B253860700C1BFAC7CE8D5325", key);
    }
}
