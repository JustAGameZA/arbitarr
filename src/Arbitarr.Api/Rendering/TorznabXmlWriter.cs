using System.Xml.Linq;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Renders the Torznab indexer protocol family (torrent-oriented): namespace prefix
/// <c>torznab</c>, enclosure MIME type <c>application/x-bittorrent</c>.
/// </summary>
public static class TorznabXmlWriter
{
    public const string ContentType = IndexerXmlWriter.ContentType;

    public static XDocument WriteSearchResults(IReadOnlyList<RenderedRelease> releases, Func<RenderedRelease, Uri> downloadLinkFactory) =>
        IndexerXmlWriter.WriteSearchResults(IndexerFamily.Torznab, releases, downloadLinkFactory);

    public static XDocument WriteCaps(SourceCaps caps) => IndexerXmlWriter.WriteCaps(IndexerFamily.Torznab, caps);

    public static XDocument WriteError(int code, string description) => IndexerXmlWriter.WriteError(code, description);
}
