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

/// <summary>
/// arb-a7ll: the prompt renders the Usenet poster and newsgroup (arb-458f), so a verdict was formed
/// under a specific pair of them and must not be served for a release carrying different ones. These
/// pin that the key varies with each, and that the encoding keeps apart the two pairs of inputs a
/// shorter implementation would fold together: a null poster versus an empty one, and a single group
/// containing a separator versus two groups.
/// </summary>
public class VerdictCacheKeyMetadataTests
{
    private static ReleaseCandidate Candidate(string? poster = null, params string[] groups) => new()
    {
        Title = "Show.S01E01.1080p.WEB-DL",
        Guid = "guid-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Size = 123456789,
        Protocol = ProtocolKind.Usenet,
        Poster = poster,
        UsenetGroup = groups,
    };

    private static string Key(ReleaseCandidate candidate) =>
        VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

    [Fact]
    public void Compute_DifferentPoster_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate("poster-a@example.invalid")), Key(Candidate("poster-b@example.invalid")));
    }

    /// <summary>
    /// <see cref="ReleaseCandidate.Poster"/> documents null ("the indexer reported no poster") and ""
    /// ("the poster is blank") as different claims, and only the second is shown to the classifier, so
    /// the key must tell them apart. The positive control is below: coercing null to "" — the obvious
    /// implementation — WOULD collide these, which is what makes this assertion evidence.
    /// </summary>
    [Fact]
    public void Compute_NullPosterVersusEmptyPoster_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(poster: null)), Key(Candidate(poster: string.Empty)));
    }

    [Fact]
    public void Compute_NullVersusEmptyPoster_WouldCollideUnderNullCoercion()
    {
        // Positive control for the test above, held here rather than in the repository: the naive
        // encoding coerces null to "" and hands both candidates an identical component, so the
        // assertion above would pass vacuously if Compute did the same.
        static string NaiveEncodePoster(string? poster) => poster ?? string.Empty;

        Assert.Equal(NaiveEncodePoster(null), NaiveEncodePoster(string.Empty));
    }

    [Fact]
    public void Compute_DifferentGroupMembership_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key(Candidate(null, "alt.binaries.tv")),
            Key(Candidate(null, "alt.binaries.movies")));
    }

    /// <summary>
    /// The boundary the length-prefixed encoding exists for: one group whose name contains the
    /// separator versus two groups whose names are its halves. Any join collapses these, and the
    /// list is indexer-supplied, so the separator's absence cannot be assumed.
    /// </summary>
    [Fact]
    public void Compute_SingleGroupContainingSeparatorVersusTwoGroups_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key(Candidate(null, "alt.binaries.a,alt.binaries.b")),
            Key(Candidate(null, "alt.binaries.a", "alt.binaries.b")));
    }

    [Fact]
    public void Compute_SingleGroupContainingSeparator_WouldCollideUnderNaiveJoin()
    {
        // Positive control for the test above: the naive encoding joins on a comma, so both group
        // lists flatten to one identical string. Without this, NotEqual above would not be evidence
        // that the encoding is what keeps them apart.
        static string NaiveEncodeGroups(IReadOnlyList<string> groups) => string.Join(",", groups);

        Assert.Equal(
            NaiveEncodeGroups(new[] { "alt.binaries.a,alt.binaries.b" }),
            NaiveEncodeGroups(new[] { "alt.binaries.a", "alt.binaries.b" }));
    }

    /// <summary>
    /// Positive control for all of the above: with poster and group held equal the key is stable, so
    /// the NotEqual assertions are about the varied field and not about some other component drifting.
    /// </summary>
    [Fact]
    public void Compute_SameMetadata_ProducesStableKey()
    {
        Assert.Equal(
            Key(Candidate("poster-a@example.invalid", "alt.binaries.tv")),
            Key(Candidate("poster-a@example.invalid", "alt.binaries.tv")));
    }
}

/// <summary>
/// arb-ddhn: the four fields <c>ClassificationPrompt.Build</c> renders that the key did not cover —
/// <see cref="ReleaseCandidate.Category"/>, <see cref="ReleaseCandidate.Files"/>,
/// <see cref="ReleaseCandidate.PasswordProtected"/> and <see cref="ReleaseCandidate.Grabs"/>. Shaped
/// exactly like <see cref="VerdictCacheKeyMetadataTests"/> above, which arb-a7ll wrote for poster and
/// group: for each field, two candidates differing ONLY in it produce different keys; for each, the
/// "not reported" and "reported as the empty/zero/false value" pair the prompt itself distinguishes
/// (its arb-458f remark says why a zeroed line is not neutral) also produces different keys; and one
/// all-equal control proves the NotEqual assertions are about the varied field rather than some other
/// component drifting.
/// </summary>
public class VerdictCacheKeyPromptFieldTests
{
    private static ReleaseCandidate Candidate(
        IReadOnlyList<int>? category = null,
        int? files = null,
        bool? passwordProtected = null,
        int? grabs = null) => new()
    {
        Title = "Show.S01E01.1080p.WEB-DL",
        Guid = "guid-1",
        PubDate = DateTimeOffset.UnixEpoch,
        Link = new Uri("https://example.invalid/r"),
        Size = 123456789,
        Protocol = ProtocolKind.Usenet,
        Category = category ?? Array.Empty<int>(),
        Files = files,
        PasswordProtected = passwordProtected,
        Grabs = grabs,
    };

    private static string Key(ReleaseCandidate candidate) =>
        VerdictCacheKey.Compute(candidate, "TestSource", "model-a", "digest-1", "v1", "t0-s42");

    [Fact]
    public void Compute_DifferentCategory_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(category: new[] { 5030 })), Key(Candidate(category: new[] { 2040 })));
    }

    /// <summary>
    /// The null-versus-empty pair for a list-typed field: the prompt renders an empty category list
    /// as <c>Categories: </c> and a populated one as its members, so the two are different claims and
    /// the key must keep them apart. (<see cref="ReleaseCandidate.Category"/> is non-nullable with an
    /// empty default, so "empty versus present" is the boundary here, not "null versus empty".)
    /// </summary>
    [Fact]
    public void Compute_EmptyCategoryVersusPopulated_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(category: Array.Empty<int>())), Key(Candidate(category: new[] { 5030 })));
    }

    /// <summary>
    /// The boundary the length-prefixed category encoding exists for, expressed on the encoding
    /// rather than through <see cref="ReleaseCandidate.Category"/>'s current <c>int</c> element type
    /// (which cannot carry a separator): a reordered list is a differently-worded question, and any
    /// order-insensitive encoding would fold these into one key.
    /// </summary>
    [Fact]
    public void Compute_ReorderedCategory_ProducesDifferentKey()
    {
        Assert.NotEqual(
            Key(Candidate(category: new[] { 5030, 5040 })),
            Key(Candidate(category: new[] { 5040, 5030 })));
    }

    [Fact]
    public void Compute_DifferentFiles_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(files: 12)), Key(Candidate(files: 13)));
    }

    /// <summary>
    /// <c>Build</c> emits no <c>Files</c> line when the indexer reported none and <c>Files: 0</c>
    /// when it reported zero — its arb-458f remark says in full why the second is a positive claim
    /// about the release rather than the absence of information. Coercing null to 0 in the key, the
    /// obvious shortening, would merge them; the positive control below shows it would.
    /// </summary>
    [Fact]
    public void Compute_NullFilesVersusZeroFiles_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(files: null)), Key(Candidate(files: 0)));
    }

    [Fact]
    public void Compute_NullVersusZeroFiles_WouldCollideUnderZeroCoercion()
    {
        // Positive control for the test above, held here rather than in the repository: the naive
        // encoding coerces null to 0 and hands both candidates an identical component, so the
        // assertion above would pass vacuously if Compute did the same.
        static string NaiveEncodeCount(int? value) => (value ?? 0).ToString(CultureInfo.InvariantCulture);

        Assert.Equal(NaiveEncodeCount(null), NaiveEncodeCount(0));
    }

    /// <summary>
    /// The sharpest of the four. <c>UsenetGuidance</c> directs the model to judge on structural
    /// metadata rather than title readability, so a flipped password flag is a different question —
    /// serving the unprotected release's verdict for the protected one is arb-a7ll's exact failure
    /// mode.
    /// </summary>
    [Fact]
    public void Compute_DifferentPasswordProtected_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(passwordProtected: true)), Key(Candidate(passwordProtected: false)));
    }

    /// <summary>
    /// "Not reported" and "reported as not password-protected" are different claims, and only the
    /// second reaches the model (as <c>Password protected: no</c>). Coercing null to false would
    /// merge them.
    /// </summary>
    [Fact]
    public void Compute_NullPasswordProtectedVersusFalse_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(passwordProtected: null)), Key(Candidate(passwordProtected: false)));
    }

    [Fact]
    public void Compute_NullVersusFalsePasswordProtected_WouldCollideUnderFalseCoercion()
    {
        // Positive control for the test above: the naive encoding coerces null to false, so both
        // candidates carry an identical component and the NotEqual would not be evidence.
        static string NaiveEncodeFlag(bool? value) => (value ?? false).ToString(CultureInfo.InvariantCulture);

        Assert.Equal(NaiveEncodeFlag(null), NaiveEncodeFlag(false));
    }

    [Fact]
    public void Compute_DifferentGrabs_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(grabs: 7)), Key(Candidate(grabs: 8)));
    }

    /// <summary>
    /// "Not reported" versus "reported as zero grabs" — a release nobody has grabbed is a real
    /// signal, and the absence of the figure is not the same signal.
    /// </summary>
    [Fact]
    public void Compute_NullGrabsVersusZeroGrabs_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(grabs: null)), Key(Candidate(grabs: 0)));
    }

    /// <summary>
    /// Cross-field control: <see cref="ReleaseCandidate.Files"/> and
    /// <see cref="ReleaseCandidate.Grabs"/> are both nullable ints appended adjacently, so an
    /// implementation that encoded them into one component (or joined them without a separator)
    /// would let a value move between them unnoticed. Swapping the two must change the key.
    /// </summary>
    [Fact]
    public void Compute_FilesAndGrabsSwapped_ProducesDifferentKey()
    {
        Assert.NotEqual(Key(Candidate(files: 3, grabs: 9)), Key(Candidate(files: 9, grabs: 3)));
    }

    /// <summary>
    /// Positive control for every assertion above: with all four fields held equal the key is
    /// stable, so each <c>NotEqual</c> is about the field it varied rather than about some other
    /// component drifting between the two calls.
    /// </summary>
    [Fact]
    public void Compute_SamePromptFields_ProducesStableKey()
    {
        Assert.Equal(
            Key(Candidate(new[] { 5030 }, files: 12, passwordProtected: false, grabs: 7)),
            Key(Candidate(new[] { 5030 }, files: 12, passwordProtected: false, grabs: 7)));
    }
}
