namespace Arbitarr.Ai.Normalization;

/// <summary>
/// Tokens that must survive title normalization unchanged, even if a normalization rule would
/// otherwise touch them (e.g. scene-tag/quality markers, season/episode markers, codec/audio
/// tags). Checked case-insensitively as whole tokens.
/// </summary>
public sealed class AllowList
{
    private readonly HashSet<string> _tokens;

    public AllowList(IEnumerable<string>? tokens = null)
    {
        _tokens = new HashSet<string>(tokens ?? Default, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Default protected tokens: common scene/quality/codec markers that must never be altered.</summary>
    public static readonly IReadOnlyList<string> Default = new[]
    {
        "1080p", "2160p", "720p", "480p", "4K", "HDR", "HDR10", "DV",
        "WEB-DL", "WEBRip", "BluRay", "REMUX", "HDTV", "PROPER", "REPACK",
        "x264", "x265", "HEVC", "AVC", "AAC", "AC3", "DTS", "TrueHD", "Atmos",
    };

    /// <summary>Whether <paramref name="token"/> is protected and must not be altered/removed by normalization.</summary>
    public bool Contains(string token) => _tokens.Contains(token);
}
