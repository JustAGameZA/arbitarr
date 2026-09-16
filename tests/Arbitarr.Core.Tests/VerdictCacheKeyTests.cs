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
    ///
    /// arb-ddhn updated it once more, on exactly the same terms: Category, Files, PasswordProtected
    /// and Grabs became key components because ClassificationPrompt.Build renders all four, so every
    /// key changes and the verdict cache is invalidated once on that deploy. The four landed as ONE
    /// such invalidation rather than one bead at a time, because each addition costs the same single
    /// round of re-classification and four staggered ones would cost four. This candidate reports
    /// none of the four either — its Category is the empty default and the three nullables are null
    /// — so the appended components are their "absent" encodings, which is again the point: a
    /// release carrying no such metadata still re-keys, because the absent encodings are themselves
    /// new hashed input. The literal below was NOT read back from a failing run's output; it was
    /// derived independently from the documented encoding rules (the U+001F join over the fourteen
    /// components, each nullable's "0" sentinel, the category count prefix) and then found to agree
    /// with what Compute produces, so the pin still checks the implementation rather than recording
    /// it.
    /// </summary>
    [Fact]
    public void Compute_FixedInputs_MatchesKnownDigest()
    {
        var candidate = MakeCandidate("fixed-guid", "Movie.Title.2024.1080p.BluRay.x264");

        var key = VerdictCacheKey.Compute(candidate, "indexer-1", "gpt-x", "digest-1", "prompt-v1", "t0-s42");

        Assert.Equal("7F045CFD01DEC4724FB50AE85F2AFC48095BC5563D7C5B44348B832EEE4B9369", key);
    }
}
