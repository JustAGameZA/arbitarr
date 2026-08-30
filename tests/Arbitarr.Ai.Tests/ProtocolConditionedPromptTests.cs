using Arbitarr.Core.Releases;

namespace Arbitarr.Ai.Tests;

/// <summary>
/// Step 3: <see cref="ClassificationPrompt.Build"/> must condition its system-prompt guidance on
/// <see cref="ReleaseCandidate.Protocol"/> — torrent and Usenet junk signals differ (AC9/R4), so the
/// prompt sent to the model must not be identical between the two protocols.
/// </summary>
public class ProtocolConditionedPromptTests
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
    public void Build_TorrentCandidate_ProducesSystemAndUserMessages()
    {
        var messages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Torrent));

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }

    [Fact]
    public void Build_TorrentVsUsenet_ProduceDifferentSystemGuidance()
    {
        var torrentMessages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Torrent));
        var usenetMessages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Usenet));

        Assert.NotEqual(torrentMessages[0].Content, usenetMessages[0].Content);
    }

    [Fact]
    public void Build_TorrentGuidance_MentionsTorrentSpecificSignals()
    {
        var messages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Torrent));

        Assert.Contains("torrent", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UsenetGuidance_MentionsUsenetSpecificSignals()
    {
        var messages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Usenet));

        Assert.Contains("usenet", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_UserMessage_IncludesOriginalTitleNotNormalizedTitle()
    {
        var candidate = new ReleaseCandidate
        {
            Title = "Movie.2024.NORMALIZED",
            OriginalTitleRaw = "Movie.2024.RAW.RARBG",
            Guid = "guid-x",
            PubDate = DateTimeOffset.UtcNow,
            Link = new Uri("https://example.invalid/r"),
            Protocol = ProtocolKind.Torrent,
        };

        var messages = ClassificationPrompt.Build(candidate);

        Assert.Contains("Movie.2024.RAW.RARBG", messages[1].Content);
        Assert.DoesNotContain("NORMALIZED", messages[1].Content);
    }

    [Fact]
    public void Build_UnknownProtocol_FallsBackToTorrentGuidance()
    {
        var messages = ClassificationPrompt.Build(Candidate("Movie.2024.1080p", ProtocolKind.Unknown));

        Assert.Contains("torrent", messages[0].Content, StringComparison.OrdinalIgnoreCase);
    }
}
