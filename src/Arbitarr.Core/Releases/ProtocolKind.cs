namespace Arbitarr.Core.Releases;

/// <summary>
/// Distinguishes the delivery protocol of a release, mirroring the Torznab/Newznab
/// distinction between torrent and Usenet (NZB) results.
/// </summary>
public enum ProtocolKind
{
    Unknown = 0,
    Torrent = 1,
    Usenet = 2,
}
