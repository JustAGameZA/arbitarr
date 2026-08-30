using System.Globalization;
using System.Xml.Linq;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Which of the two RSS-based indexer protocol families to render as. *arr clients treat
/// Torznab and Newznab as distinct indexer classes: they differ by XML namespace prefix and
/// by the enclosure MIME type used for downloads, but are otherwise structurally identical
/// and are both served as <c>application/rss+xml</c>. Internal — external callers use the
/// family-specific <see cref="TorznabXmlWriter"/>/<see cref="NewznabXmlWriter"/> facades.
/// </summary>
internal enum IndexerFamily
{
    Torznab,
    Newznab,
}

/// <summary>
/// Shared rendering engine for <see cref="TorznabXmlWriter"/> and <see cref="NewznabXmlWriter"/>.
/// Renders <see cref="RenderedRelease"/> sets, a merged <see cref="SourceCaps"/>, or an error
/// condition as Torznab/Newznab-family RSS XML. Rendering is a pure passthrough: size,
/// category, and guid are byte-exact from upstream, and title is the original upstream string
/// with no normalization applied (no normalizer exists yet in this milestone).
/// </summary>
internal static class IndexerXmlWriter
{
    /// <summary>Namespace URI shared by both Torznab and Newznab feeds (Newznab reuses the Torznab schema).</summary>
    public static readonly XNamespace SchemaNs = "http://torznab.com/schemas/2015/feed";

    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    public const string ContentType = "application/rss+xml";

    private static string Prefix(IndexerFamily family) => family == IndexerFamily.Torznab ? "torznab" : "newznab";

    private static string EnclosureMimeType(IndexerFamily family, ReleaseCandidate candidate) =>
        family == IndexerFamily.Torznab || candidate.Protocol == ProtocolKind.Torrent
            ? "application/x-bittorrent"
            : "application/x-nzb";

    /// <summary>Renders a search result set (t=search|tvsearch|movie|music).</summary>
    public static XDocument WriteSearchResults(
        IndexerFamily family,
        IReadOnlyList<RenderedRelease> releases,
        Func<RenderedRelease, Uri> downloadLinkFactory)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(downloadLinkFactory);

        var prefix = Prefix(family);
        var ns = SchemaNs;

        var channel = new XElement("channel",
            new XElement("title", "Arbitarr"),
            new XElement("description", "Arbitarr merged search results"),
            releases.Select(r => WriteItem(family, ns, prefix, r, downloadLinkFactory(r))));

        var rss = new XElement("rss",
            new XAttribute("version", "2.0"),
            new XAttribute(XNamespace.Xmlns + "atom", AtomNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName),
            channel);

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), rss);
    }

    private static XElement WriteItem(
        IndexerFamily family,
        XNamespace ns,
        string prefix,
        RenderedRelease release,
        Uri downloadLink)
    {
        var candidate = release.Candidate;
        var mimeType = EnclosureMimeType(family, candidate);

        var item = new XElement("item",
            new XElement("title", candidate.Title),
            new XElement("guid", new XAttribute("isPermaLink", "false"), release.ProxyGuid),
            new XElement("link", downloadLink.ToString()),
            new XElement("pubDate", candidate.PubDate.ToString("R", CultureInfo.InvariantCulture)),
            new XElement("size", candidate.Size),
            new XElement("enclosure",
                new XAttribute("url", downloadLink.ToString()),
                new XAttribute("length", candidate.Size),
                new XAttribute("type", mimeType)));

        foreach (var categoryId in candidate.Category)
        {
            item.Add(new XElement(ns + "attr",
                new XAttribute("name", "category"),
                new XAttribute("value", categoryId.ToString(CultureInfo.InvariantCulture))));
        }

        item.Add(new XElement(ns + "attr",
            new XAttribute("name", "size"),
            new XAttribute("value", candidate.Size.ToString(CultureInfo.InvariantCulture))));

        var protocolValue = candidate.Protocol switch
        {
            ProtocolKind.Torrent => "torrent",
            ProtocolKind.Usenet => "usenet",
            _ => null,
        };
        if (protocolValue is not null)
        {
            item.Add(new XElement(ns + "attr", new XAttribute("name", "protocol"), new XAttribute("value", protocolValue)));
        }

        if (candidate.InfoHash is not null)
        {
            item.Add(new XElement(ns + "attr", new XAttribute("name", "infohash"), new XAttribute("value", candidate.InfoHash)));
        }

        if (candidate.Seeders is int seeders)
        {
            item.Add(new XElement(ns + "attr", new XAttribute("name", "seeders"), new XAttribute("value", seeders)));
        }

        if (candidate.Peers is int peers)
        {
            item.Add(new XElement(ns + "attr", new XAttribute("name", "peers"), new XAttribute("value", peers)));
        }

        return item;
    }

    /// <summary>Renders a t=caps response for the merged <see cref="SourceCaps"/>.</summary>
    public static XDocument WriteCaps(IndexerFamily family, SourceCaps caps)
    {
        ArgumentNullException.ThrowIfNull(caps);

        var searching = new XElement("searching",
            new XElement("search", new XAttribute("available", "yes"), new XAttribute("supportedParams", string.Join(",", caps.SupportedParams ?? Array.Empty<string>()))),
            new XElement("tv-search", new XAttribute("available", caps.SupportsTvSearch ? "yes" : "no"), new XAttribute("supportedParams", string.Join(",", caps.SupportedParams ?? Array.Empty<string>()))),
            new XElement("movie-search", new XAttribute("available", caps.SupportsMovieSearch ? "yes" : "no"), new XAttribute("supportedParams", string.Join(",", caps.SupportedParams ?? Array.Empty<string>()))));

        var categories = new XElement("categories",
            caps.SupportedCategories.Select(id => new XElement("category",
                new XAttribute("id", id),
                new XAttribute("name", CategoryName(id)))));

        var limits = new XElement("limits", new XAttribute("max", caps.MaxPageSize ?? CapsAggregator.EnforcedMaxPageSize), new XAttribute("default", caps.MaxPageSize ?? CapsAggregator.EnforcedMaxPageSize));

        var caps_ = new XElement("caps",
            new XElement("server", new XAttribute("version", "1.0"), new XAttribute("title", "Arbitarr")),
            limits,
            searching,
            categories);

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), caps_);
    }

    private static string CategoryName(int id) => id switch
    {
        >= 5000 and < 6000 => "TV",
        >= 2000 and < 3000 => "Movies",
        >= 3000 and < 4000 => "Audio",
        >= 6000 and < 7000 => "XXX",
        _ => "Other",
    };

    /// <summary>
    /// Renders a Torznab/Newznab error element (error code 100 family). Used for both
    /// unexpected-failure rendering and RequestLimitReachedException (rate-limit) handling —
    /// callers select the code/description; this never emits a 5xx status itself.
    /// </summary>
    public static XDocument WriteError(int code, string description)
    {
        var error = new XElement("error",
            new XAttribute("code", code),
            new XAttribute("description", description));

        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), error);
    }
}
