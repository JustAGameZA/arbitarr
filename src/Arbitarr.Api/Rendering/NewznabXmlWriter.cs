using System.Xml.Linq;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Renders the Newznab indexer protocol family (Usenet-oriented): namespace prefix
/// <c>newznab</c>, enclosure MIME type <c>application/x-nzb</c>.
/// </summary>
public static class NewznabXmlWriter
{
    public const string ContentType = IndexerXmlWriter.ContentType;

    public static XDocument WriteSearchResults(IReadOnlyList<RenderedRelease> releases, Func<RenderedRelease, Uri> downloadLinkFactory) =>
        IndexerXmlWriter.WriteSearchResults(IndexerFamily.Newznab, releases, downloadLinkFactory);

    public static XDocument WriteCaps(SourceCaps caps) => IndexerXmlWriter.WriteCaps(IndexerFamily.Newznab, caps);

    public static XDocument WriteError(int code, string description) => IndexerXmlWriter.WriteError(code, description);
}
