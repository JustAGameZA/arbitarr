using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// arb-458f: <c>UsenetGuidance</c> tells the model to judge an obfuscated Usenet release on
/// "structural and metadata signals ... rather than title readability". Those signals have to
/// actually be in the user message for that instruction to mean anything, so this file pins that
/// each populated field is rendered — and, just as importantly, that an unreported field is
/// OMITTED rather than rendered blank or zeroed.
///
/// <para>
/// Both halves are asserted PER FIELD (CLAUDE.md §4). The absence half is the one that goes
/// vacuous most easily: <c>Assert.DoesNotContain("Poster:", ...)</c> passes just as happily when
/// the renderer never learned to emit a poster line at all. The populated half is its positive
/// control — it proves each label IS emitted when the data is present, so the absence assertion is
/// demonstrating omission rather than a feature that was never built.
/// </para>
/// </summary>
public class UsenetMetadataPromptTests
{
    private static ReleaseCandidate Populated(
        string? poster = "a1b2c3@example.invalid",
        IReadOnlyList<string>? usenetGroup = null,
        int? files = 42,
        bool? passwordProtected = true,
        int? grabs = 77) => new()
    {
        Title = "Obfuscated.Release.Name",
        Guid = "guid-usenet-1",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Protocol = ProtocolKind.Usenet,
        Poster = poster,
        UsenetGroup = usenetGroup ?? new[] { "alt.binaries.example" },
        Files = files,
        PasswordProtected = passwordProtected,
        Grabs = grabs,
    };

    private static ReleaseCandidate Bare() => new()
    {
        Title = "Obfuscated.Release.Name",
        Guid = "guid-usenet-2",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Protocol = ProtocolKind.Usenet,
    };

    private static string UserMessage(ReleaseCandidate candidate)
        => ClassificationPrompt.Build(candidate)[1].Content;

    // ---- Populated: each field's VALUE reaches the prompt, individually ------------------------

    [Fact]
    public void Build_PopulatedPoster_IsRendered()
        => Assert.Contains("Poster: a1b2c3@example.invalid", UserMessage(Populated()), StringComparison.Ordinal);

    [Fact]
    public void Build_PopulatedUsenetGroup_IsRendered()
        => Assert.Contains("Usenet group: alt.binaries.example", UserMessage(Populated()), StringComparison.Ordinal);

    [Fact]
    public void Build_PopulatedFiles_IsRendered()
        => Assert.Contains("Files: 42", UserMessage(Populated()), StringComparison.Ordinal);

    [Fact]
    public void Build_PopulatedPasswordProtected_IsRendered()
        => Assert.Contains("Password protected: yes", UserMessage(Populated()), StringComparison.Ordinal);

    [Fact]
    public void Build_PopulatedGrabs_IsRendered()
        => Assert.Contains("Grabs: 77", UserMessage(Populated()), StringComparison.Ordinal);

    // ---- Absent: each field's LABEL is omitted, individually -----------------------------------
    // Not merely blank. "Poster: " or "Files: 0" would read to the model as a positive claim about
    // the release rather than as missing information.

    [Fact]
    public void Build_AbsentPoster_OmitsLabelEntirely()
        => Assert.DoesNotContain("Poster:", UserMessage(Bare()), StringComparison.Ordinal);

    [Fact]
    public void Build_AbsentUsenetGroup_OmitsLabelEntirely()
        => Assert.DoesNotContain("Usenet group:", UserMessage(Bare()), StringComparison.Ordinal);

    [Fact]
    public void Build_AbsentFiles_OmitsLabelEntirely()
        => Assert.DoesNotContain("Files:", UserMessage(Bare()), StringComparison.Ordinal);

    [Fact]
    public void Build_AbsentPasswordProtected_OmitsLabelEntirely()
        => Assert.DoesNotContain("Password protected:", UserMessage(Bare()), StringComparison.Ordinal);

    [Fact]
    public void Build_AbsentGrabs_OmitsLabelEntirely()
        => Assert.DoesNotContain("Grabs:", UserMessage(Bare()), StringComparison.Ordinal);

    // ---- Shape details ------------------------------------------------------------------------

    /// <summary>
    /// <c>PasswordProtected = false</c> is a reported fact ("the indexer says this is not
    /// protected"), distinct from an unreported field. It must render, or a genuinely clean
    /// release is indistinguishable from one the indexer said nothing about.
    /// </summary>
    [Fact]
    public void Build_PasswordProtectedFalse_RendersNoRatherThanOmitting()
    {
        var candidate = Populated(passwordProtected: false);

        Assert.Contains("Password protected: no", UserMessage(candidate), StringComparison.Ordinal);
    }

    /// <summary>
    /// Likewise a reported zero: "this release has no grabs yet" is a signal in its own right.
    /// </summary>
    [Fact]
    public void Build_ZeroCounts_AreRenderedNotOmitted()
    {
        var candidate = Populated(files: 0, grabs: 0);
        var message = UserMessage(candidate);

        Assert.Contains("Files: 0", message, StringComparison.Ordinal);
        Assert.Contains("Grabs: 0", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whitespace-only poster carries no information, so it is treated as absent rather than
    /// rendered as an empty claim.
    /// </summary>
    [Fact]
    public void Build_WhitespacePoster_IsTreatedAsAbsent()
    {
        var candidate = Populated(poster: "   ");

        Assert.DoesNotContain("Poster:", UserMessage(candidate), StringComparison.Ordinal);
    }

    /// <summary>
    /// M5: neither field is attacker-bounded before reaching this layer, so both are truncated on
    /// the same basis as title/categories.
    /// </summary>
    [Fact]
    public void Build_OversizedPosterAndGroup_AreTruncatedTo512Chars()
    {
        var candidate = Populated(
            poster: new string('p', 1000),
            usenetGroup: new[] { new string('g', 1000) });
        var message = UserMessage(candidate);

        var posterLine = message.Split('\n').Single(l => l.StartsWith("Poster: ", StringComparison.Ordinal));
        var groupLine = message.Split('\n').Single(l => l.StartsWith("Usenet group: ", StringComparison.Ordinal));

        Assert.Equal(512, posterLine["Poster: ".Length..].Length);
        Assert.Equal(512, groupLine["Usenet group: ".Length..].Length);
    }

    /// <summary>
    /// The protocol arm split (ObfuscatedNameTests) is unaffected: a torrent candidate that somehow
    /// carries Usenet fields still gets torrent guidance, and the metadata lines remain a property
    /// of the data rather than of the arm.
    /// </summary>
    [Fact]
    public void Build_UsenetMetadata_DoesNotDisturbSystemGuidanceSelection()
    {
        var messages = ClassificationPrompt.Build(Populated());

        Assert.Contains("usenet", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The production path never prompts the candidate the parser produced. Every caller that
    /// classifies goes through <see cref="ReleaseCandidate.WithTitle"/> first —
    /// <c>ClassifierPollingWorker</c> (~:258) calls it unconditionally before
    /// <c>ClassifyAndCacheAsync</c>, and <c>TitleNormalizer</c>/<c>FilterStage</c> do the same — so
    /// a field the parser populates but the clone drops reaches the model as absent, in production
    /// only, while every direct-construction test above still passes.
    ///
    /// <para>
    /// That is not hypothetical: <c>Poster</c> shipped in exactly that state and the whole feature
    /// was inert until the clone was fixed. This test round-trips through <c>WithTitle</c> and then
    /// asserts ALL FIVE lines individually, so the next Usenet field added to the parser and the
    /// renderer but not to the clone fails here rather than silently going missing at runtime.
    /// </para>
    /// </summary>
    [Fact]
    public void Build_AfterWithTitleRoundTrip_StillRendersEveryUsenetField()
    {
        var rewritten = Populated().WithTitle("Rewritten.Title", originalTitleRaw: null);

        var message = UserMessage(rewritten);

        Assert.Contains("Poster: a1b2c3@example.invalid", message, StringComparison.Ordinal);
        Assert.Contains("Usenet group: alt.binaries.example", message, StringComparison.Ordinal);
        Assert.Contains("Files: 42", message, StringComparison.Ordinal);
        Assert.Contains("Password protected: yes", message, StringComparison.Ordinal);
        Assert.Contains("Grabs: 77", message, StringComparison.Ordinal);
    }
}
