namespace Arbitarr.Core.Releases;

/// <summary>
/// A single search-result release, modeled as a superset of standard Torznab and Newznab
/// attributes plus torrent-specific and Usenet-specific extensions.
/// </summary>
public sealed class ReleaseCandidate
{
    // --- Standard Torznab/Newznab attributes ---

    /// <summary>Release title, as reported by the upstream source.</summary>
    public required string Title { get; init; }

    /// <summary>Source-provided unique identifier (Torznab/Newznab &lt;guid&gt;).</summary>
    public required string Guid { get; init; }

    /// <summary>Publication date of the release.</summary>
    public required DateTimeOffset PubDate { get; init; }

    /// <summary>Size of the release payload, in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Direct link to the release (torrent/NZB URL or comment page, per feed semantics).</summary>
    public required Uri Link { get; init; }

    /// <summary>Torznab/Newznab category identifiers associated with this release.</summary>
    public IReadOnlyList<int> Category { get; init; } = Array.Empty<int>();

    /// <summary>Which delivery protocol this release uses.</summary>
    public ProtocolKind Protocol { get; init; } = ProtocolKind.Unknown;

    // --- Torrent-specific attributes ---

    /// <summary>BitTorrent info hash, when <see cref="Protocol"/> is <see cref="ProtocolKind.Torrent"/>.</summary>
    public string? InfoHash { get; init; }

    /// <summary>Number of seeders reported for this torrent.</summary>
    public int? Seeders { get; init; }

    /// <summary>Number of peers (leechers) reported for this torrent.</summary>
    public int? Peers { get; init; }

    /// <summary>Minimum seed ratio required by the source's tracker rules, if specified.</summary>
    public double? MinimumRatio { get; init; }

    /// <summary>Minimum seed time (in seconds) required by the source's tracker rules, if specified.</summary>
    public long? MinimumSeedTime { get; init; }

    // --- Usenet-specific attributes ---

    /// <summary>Usenet newsgroup(s) the release was posted to.</summary>
    public IReadOnlyList<string> UsenetGroup { get; init; } = Array.Empty<string>();

    /// <summary>Whether the NZB payload is password-protected.</summary>
    public bool? PasswordProtected { get; init; }

    /// <summary>Number of files contained in the Usenet release, if reported.</summary>
    public int? Files { get; init; }

    /// <summary>Grabs/download count reported by the source, if available.</summary>
    public int? Grabs { get; init; }
}
