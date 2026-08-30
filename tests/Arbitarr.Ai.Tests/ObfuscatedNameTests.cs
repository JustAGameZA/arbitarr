using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// AC9/R4: obfuscated Usenet release names (scrambled/hash-like titles produced by some posting
/// tools as routine, deliberate convention) must not be treated as a junk signal by the model. The
/// prompt's Usenet guidance must explicitly instruct the model away from judging junk-ness by title
/// readability alone, and this instruction must not appear in the torrent guidance (where it would
/// be actively wrong — torrent obfuscation genuinely IS a common junk signal there).
/// </summary>
public class ObfuscatedNameTests
{
    private static ReleaseCandidate Candidate(string title, ProtocolKind protocol) => new()
    {
        Title = title,
        Guid = $"guid-{title}",
        PubDate = DateTimeOffset.UtcNow,
        Link = new Uri("https://example.invalid/r"),
        Protocol = protocol,
    };

    [Fact]
    public void UsenetGuidance_InstructsModel_NotToTreatObfuscationAsJunk()
    {
        var messages = ClassificationPrompt.Build(
            Candidate("a8f3k2.9dl1xz.vol03+07", ProtocolKind.Usenet));

        Assert.Contains("obfuscat", messages[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "not",
            messages[0].Content.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? messages[0].Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsenetGuidance_DoesNotInstructRejectionBasedOnTitleReadability()
    {
        var messages = ClassificationPrompt.Build(
            Candidate("a8f3k2.9dl1xz.vol03+07", ProtocolKind.Usenet));

        Assert.Contains("Do not reject", messages[0].Content);
    }

    [Fact]
    public void TorrentGuidance_DoesNotCarryUsenetObfuscationCarveOut()
    {
        var messages = ClassificationPrompt.Build(
            Candidate("a8f3k2.9dl1xz.vol03+07", ProtocolKind.Torrent));

        Assert.DoesNotContain("Do not reject", messages[0].Content);
    }

    [Fact]
    public void Build_ObfuscatedUsenetTitle_StillPassedThroughVerbatimInUserMessage()
    {
        const string obfuscatedTitle = "a8f3k2.9dl1xz.vol03+07";
        var messages = ClassificationPrompt.Build(Candidate(obfuscatedTitle, ProtocolKind.Usenet));

        Assert.Contains(obfuscatedTitle, messages[1].Content);
    }
}
