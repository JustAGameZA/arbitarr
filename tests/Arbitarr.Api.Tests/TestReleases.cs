using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;

namespace Arbitarr.Api.Tests;

/// <summary>Shared deterministic <see cref="RenderedRelease"/> fixtures for golden-XML tests.</summary>
internal static class TestReleases
{
    public static readonly DateTimeOffset FixedPubDate = new(2026, 8, 23, 10, 55, 15, TimeSpan.Zero);

    public static RenderedRelease Torrent(
        string sourceName = "eztv",
        string guid = "2215981299181082891",
        string title = "Bleach S17E45 DEFEND YOU 1080p DSNP WEB-DL AAC2 0 H 264-playWEB") =>
        new(sourceName, new ReleaseCandidate
        {
            Title = title,
            Guid = guid,
            PubDate = FixedPubDate,
            Size = 1138166333,
            Link = new Uri("http://192.0.2.21:5076/gettorrent/api/2215981299181082891?apikey=REDACTED"),
            Category = new[] { 5000 },
            Protocol = ProtocolKind.Torrent,
            InfoHash = "332afa1fd16fc0a5fd8d54e18d62e57f60a06764",
            Seeders = 182,
            Peers = 182,
        });

    public static RenderedRelease Usenet(
        string sourceName = "usenetsrc",
        string guid = "abc123",
        string title = "Some.Album.2026.FLAC") =>
        new(sourceName, new ReleaseCandidate
        {
            Title = title,
            Guid = guid,
            PubDate = FixedPubDate,
            Size = 500_000_000,
            Link = new Uri("http://192.0.2.30:8080/getnzb/abc123"),
            Category = new[] { 3000 },
            Protocol = ProtocolKind.Usenet,
        });
}
