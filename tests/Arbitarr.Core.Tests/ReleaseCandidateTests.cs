using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Tests;

public class ReleaseCandidateTests
{
    [Fact]
    public void Construction_SetsRequiredStandardFields()
    {
        var pubDate = new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);
        var link = new Uri("https://example.invalid/release/1");

        var candidate = new ReleaseCandidate
        {
            Title = "Example.Release.S01E01.1080p",
            Guid = "guid-123",
            PubDate = pubDate,
            Size = 123_456_789L,
            Link = link,
            Category = new[] { 5000, 5040 },
            Protocol = ProtocolKind.Torrent,
        };

        Assert.Equal("Example.Release.S01E01.1080p", candidate.Title);
        Assert.Equal("guid-123", candidate.Guid);
        Assert.Equal(pubDate, candidate.PubDate);
        Assert.Equal(123_456_789L, candidate.Size);
        Assert.Equal(link, candidate.Link);
        Assert.Equal(new[] { 5000, 5040 }, candidate.Category);
        Assert.Equal(ProtocolKind.Torrent, candidate.Protocol);
    }

    [Fact]
    public void Construction_DefaultsOptionalFieldsToNullOrEmpty()
    {
        var candidate = new ReleaseCandidate
        {
            Title = "Minimal.Release",
            Guid = "guid-minimal",
            PubDate = DateTimeOffset.UnixEpoch,
            Link = new Uri("https://example.invalid/release/minimal"),
        };

        // Standard/optional fields not set explicitly should have sane defaults.
        Assert.Equal(0, candidate.Size);
        Assert.Empty(candidate.Category);
        Assert.Equal(ProtocolKind.Unknown, candidate.Protocol);

        // Torrent-specific fields default to null/unset when not populated.
        Assert.Null(candidate.InfoHash);
        Assert.Null(candidate.Seeders);
        Assert.Null(candidate.Peers);
        Assert.Null(candidate.MinimumRatio);
        Assert.Null(candidate.MinimumSeedTime);

        // Usenet-specific fields default to null/empty when not populated.
        Assert.Empty(candidate.UsenetGroup);
        Assert.Null(candidate.PasswordProtected);
        Assert.Null(candidate.Files);
        Assert.Null(candidate.Grabs);
    }

    [Fact]
    public void Construction_SetsTorrentSpecificFields()
    {
        var candidate = new ReleaseCandidate
        {
            Title = "Torrent.Release",
            Guid = "guid-torrent",
            PubDate = DateTimeOffset.UnixEpoch,
            Link = new Uri("https://example.invalid/release/torrent"),
            Protocol = ProtocolKind.Torrent,
            InfoHash = "0123456789abcdef0123456789abcdef01234567",
            Seeders = 42,
            Peers = 7,
            MinimumRatio = 1.0,
            MinimumSeedTime = 172_800,
        };

        Assert.Equal("0123456789abcdef0123456789abcdef01234567", candidate.InfoHash);
        Assert.Equal(42, candidate.Seeders);
        Assert.Equal(7, candidate.Peers);
        Assert.Equal(1.0, candidate.MinimumRatio);
        Assert.Equal(172_800, candidate.MinimumSeedTime);
    }

    [Fact]
    public void Construction_SetsUsenetSpecificFields()
    {
        var candidate = new ReleaseCandidate
        {
            Title = "Usenet.Release",
            Guid = "guid-usenet",
            PubDate = DateTimeOffset.UnixEpoch,
            Link = new Uri("https://example.invalid/release/usenet"),
            Protocol = ProtocolKind.Usenet,
            UsenetGroup = new[] { "alt.binaries.example" },
            PasswordProtected = false,
            Files = 12,
            Grabs = 3,
        };

        Assert.Equal(new[] { "alt.binaries.example" }, candidate.UsenetGroup);
        Assert.False(candidate.PasswordProtected);
        Assert.Equal(12, candidate.Files);
        Assert.Equal(3, candidate.Grabs);
    }

    /// <summary>
    /// <see cref="ReleaseCandidate.WithTitle"/> is documented as the single clone site for title
    /// rewrites precisely so callers "cannot drift on which attributes survive a rewrite" — but
    /// nothing asserted that, and <c>Poster</c> was added to the parser and the prompt while the
    /// clone kept dropping it. Since every classification path rewrites the title first
    /// (<c>ClassifierPollingWorker</c>, <c>TitleNormalizer</c>, <c>FilterStage</c>), the field was
    /// silently absent in production while direct-construction tests passed.
    ///
    /// <para>
    /// Asserted PER FIELD rather than as one "the clone kept the Usenet data" check: a single
    /// assertion passes while four of five fields are dropped, which is the failure mode this
    /// exists to catch.
    /// </para>
    /// </summary>
    [Fact]
    public void WithTitle_CarriesEveryUsenetFieldThroughTheClone()
    {
        var original = new ReleaseCandidate
        {
            Title = "Obfuscated.Release.Name",
            Guid = "guid-usenet",
            PubDate = DateTimeOffset.UnixEpoch,
            Link = new Uri("https://example.invalid/release/usenet"),
            Protocol = ProtocolKind.Usenet,
            Poster = "a1b2c3@example.invalid",
            UsenetGroup = new[] { "alt.binaries.example" },
            PasswordProtected = true,
            Files = 12,
            Grabs = 3,
        };

        var rewritten = original.WithTitle("Rewritten.Title", originalTitleRaw: original.Title);

        Assert.Equal("Rewritten.Title", rewritten.Title);
        Assert.Equal("Obfuscated.Release.Name", rewritten.OriginalTitle);

        Assert.Equal("a1b2c3@example.invalid", rewritten.Poster);
        Assert.Equal(new[] { "alt.binaries.example" }, rewritten.UsenetGroup);
        Assert.True(rewritten.PasswordProtected);
        Assert.Equal(12, rewritten.Files);
        Assert.Equal(3, rewritten.Grabs);
    }
}
