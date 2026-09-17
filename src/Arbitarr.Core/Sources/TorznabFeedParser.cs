using System.Globalization;
using System.Xml.Linq;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Sources;

/// <summary>
/// The Torznab/Newznab wire-format parser, shared by every adapter that speaks the family
/// (arb-x7w8.2). Both the item feed and the caps document are parsed here.
///
/// <para><b>Why this lives in Core rather than on an adapter.</b> The two families differ in the
/// endpoint path an adapter calls and in which attrs an indexer happens to populate — NOT in the
/// grammar of the response. It was originally written on <c>NzbHydraSource</c>, and a second
/// adapter for direct indexers would otherwise have needed a second copy: two copies of a wire
/// parser are two things to keep true, and the one that drifts is the one nobody notices, because
/// both keep returning plausible items. Adapters keep their own transport, auth and refusal
/// policy; only the parse is shared.</para>
/// </summary>
public static class TorznabFeedParser
{
    /// <summary>
    /// The schema namespace is shared by both families — a Newznab feed reuses the Torznab schema
    /// URI and differs only in its prefix (see <c>IndexerXmlWriter.SchemaNs</c>, which renders our
    /// own feeds on the same basis). Matching on the namespace URI rather than the literal prefix
    /// is therefore what makes a <c>newznab:attr</c> from a <c>/api</c> endpoint read identically
    /// to a <c>torznab:attr</c> from <c>/torznab/api</c>; there is no second namespace to register.
    /// </summary>
    private static readonly XNamespace TorznabNs = "http://torznab.com/schemas/2015/feed";

    /// <summary>
    /// SEC-M1 (SSRF): validates an upstream-supplied <c>&lt;link&gt;</c> against the configured
    /// upstream <paramref name="allowedOrigin"/> (scheme + host + port) before it is trusted as a
    /// download target. Without this, a compromised/malicious upstream feed could point
    /// <c>&lt;link&gt;</c> at an arbitrary LAN host and have Arbitarr fetch and stream it back to
    /// the caller via DownloadProxyEndpoint. Non-conforming links cause the whole item to be
    /// dropped (never defaulted to a placeholder URI, which would still be a valid, fetchable
    /// target).
    ///
    /// <para><b>The check matters MORE, not less, with direct indexers (arb-x7w8.2).</b> With N
    /// operator-entered upstream hosts instead of one the SSRF surface is strictly larger: every
    /// added source is another origin whose feed is trusted to name fetch targets, and each is
    /// pinned to its own <paramref name="allowedOrigin"/> so one indexer's feed can never name
    /// another's host — let alone an arbitrary one.</para>
    ///
    /// <para><b>Scheme and userinfo (arb-07ei).</b> "scheme + host + port" above was what this
    /// method was always meant to do; until arb-07ei the code compared only host and port, so an
    /// <c>https</c> origin accepted an <c>http</c> link and a hostile feed could downgrade the
    /// download fetch — which carries the indexer key — to cleartext. A link carrying USERINFO
    /// passed for a quieter reason: <see cref="Uri.Host"/> excludes it, so
    /// <c>http://user:pw@indexer.example:9117/x</c> looked same-origin while injecting a Basic-auth
    /// credential into a URL Arbitarr would then fetch. Both rules now live in
    /// <see cref="UpstreamOrigin.IsAtOrigin"/>, which the write boundary calls too, so the two can
    /// never disagree about what an origin is again.</para>
    /// </summary>
    public static bool TryValidateOriginPinnedLink(string? link, Uri allowedOrigin, out Uri validated)
    {
        validated = null!;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var parsedLink))
        {
            return false;
        }

        if (!UpstreamOrigin.IsAtOrigin(parsedLink, allowedOrigin))
        {
            return false;
        }

        validated = parsedLink;
        return true;
    }

    /// <summary>
    /// The prefix of the <c>xt</c> parameter's VALUE — not of the parameter name — behind which a
    /// magnet URI carries its BitTorrent info hash (<c>xt=urn:btih:&lt;hash&gt;</c>). Required for
    /// admission below: it is what distinguishes a magnet link naming a torrent from an arbitrary
    /// string that merely begins <c>magnet:</c>. Renamed from <c>...TopicPrefix</c> (codereview-492)
    /// because that name read as though it prefixed the PARAMETER, and that reading is exactly the
    /// one under which testing it against the raw <c>xt=…</c> parameter would look correct.
    /// </summary>
    private const string BitTorrentExactTopicValuePrefix = "urn:btih:";

    /// <summary>
    /// arb-x7w8.15: admits a <c>magnet:</c> link as an explicitly recognised, NON-FETCHABLE download
    /// target. This is a separate method from <see cref="TryValidateOriginPinnedLink"/> on purpose,
    /// and the separation IS the security property rather than a stylistic choice.
    ///
    /// <para><b>Why the origin pin is not loosened instead.</b> A magnet has no host, so
    /// <see cref="UpstreamOrigin.IsAtOrigin"/> refuses it and <see cref="ParseFeedResponse"/> dropped
    /// the whole item — a magnet-only tracker returned nothing, with no error and no log line. The
    /// fix is NOT to teach the pin about hostless schemes.
    /// <see cref="UpstreamOrigin.IsAtOrigin"/> is shared with the write boundary
    /// (<c>SourceRepository.ValidateBaseUrl</c>) AND is called at FETCH time by every adapter's
    /// <c>FetchDownloadAsync</c> immediately before it issues an HTTP request (SEC-M1). Relaxing it
    /// would therefore both change which base URLs an operator may store and let a hostless link
    /// through the gate standing in front of an outbound fetch. This method is consulted ONLY on the
    /// parse path, so the fetch-time pin still refuses a magnet: a code path that somehow reached an
    /// adapter carrying one cannot turn it into a request.</para>
    ///
    /// <para><b>Admission is narrow and closed by construction.</b> The scheme must be exactly
    /// <c>magnet</c> and the query must carry an <c>xt=urn:btih:</c> exact topic. Every other
    /// non-http(s) scheme — <c>javascript:</c>, <c>file:</c>, <c>ftp:</c>, <c>data:</c> — stays
    /// refused exactly as before, as does a <c>magnet:</c> carrying no info hash. Matching the parsed
    /// SCHEME rather than prefix-testing the raw string is what keeps a value such as
    /// <c>https://evil.example/#magnet:?xt=urn:btih:…</c> out: that is an http(s) URL, so it goes to
    /// the origin pin, which refuses it.</para>
    ///
    /// <para><b>Control characters are refused (sec-492).</b> <c>Uri.TryCreate</c> accepts a
    /// RAW CR/LF inside a magnet query, and <see cref="Uri.OriginalString"/> — alone among the Uri
    /// accessors — preserves it verbatim: <see cref="Uri.Query"/> and <see cref="Uri.AbsoluteUri"/>
    /// both re-encode it to <c>%0D%0A</c>. <c>OriginalString</c> is therefore what this checks,
    /// because it is also exactly what <c>DownloadProxyEndpoint</c>'s magnet arm hands to
    /// <c>Results.Redirect</c>, and so what lands in the <c>Location</c> header. Kestrel THROWS on a
    /// control character in a header value, so response splitting was never exploitable — but that
    /// guard is Kestrel's rather than ours, and the failure it produces is a guaranteed 500 on every
    /// such item, which a malicious feed can mint at will. Refusing here closes it by construction
    /// and one layer earlier: the item is dropped like any other non-conforming link instead of
    /// becoming a server error at download time. A percent-ENCODED <c>%0D%0A</c> needs no rejection
    /// and gets none: it stays encoded through <c>OriginalString</c> into the header, where it is
    /// inert text. <see cref="char.IsControl(char)"/> rather than a CR/LF pair, because nothing
    /// legitimate in a magnet URI is a control character and enumerating the harmful ones is the
    /// weaker rule.</para>
    ///
    /// <para><b>The info hash must be non-empty (codereview-492).</b> <c>magnet:?xt=urn:btih:</c>
    /// satisfied the prefix test while naming no torrent at all, and was admitted as though it did.
    /// At least one character is now required after the prefix — which is also what makes
    /// <paramref name="infoHash"/> meaningful rather than sometimes an empty string.</para>
    ///
    /// <para><b>The hash is returned, not just checked (arb-4ysc).</b> <paramref name="infoHash"/>
    /// receives the btih value EXACTLY as the feed wrote it — no case transcoding and no base32/hex
    /// conversion — because it is rendered straight back out as a <c>torznab:attr name="infohash"</c>
    /// and a value we reshaped would no longer be the one the upstream indexer published. The
    /// <see cref="Uri"/> handed back is likewise the feed's own link, trackers and all; the hash is
    /// an addition to it, never a replacement for it. It is a third <c>out</c> on the one method
    /// rather than a second overload: an overload pair made every <c>cref</c> to this name ambiguous,
    /// and a caller that does not want the hash says so explicitly with <c>out _</c>.</para>
    /// </summary>
    /// <param name="link">The upstream-supplied link value.</param>
    /// <param name="validated">The admitted magnet URI, or <see langword="null"/> when refused.</param>
    /// <param name="infoHash">
    /// The btih value verbatim from the admitted magnet, or <see langword="null"/> when refused.
    /// </param>
    public static bool TryValidateMagnetLink(string? link, out Uri validated, out string? infoHash)
    {
        validated = null!;
        infoHash = null;

        if (!Uri.TryCreate(link, UriKind.Absolute, out var parsedLink))
        {
            return false;
        }

        // sec-492: refused before anything is read out of the URI, so no downstream reader — the
        // redirect's Location header above all — can ever see a control character from this method.
        if (parsedLink.OriginalString.Any(char.IsControl))
        {
            return false;
        }

        // OrdinalIgnoreCase, never culture-sensitive: schemes are ASCII and case-insensitive
        // (RFC 3986), and a culture-sensitive comparison on a security decision is the classic
        // Turkish-I hazard. Uri lower-cases a well-formed scheme; this deliberately does not rely
        // on that.
        if (!string.Equals(parsedLink.Scheme, "magnet", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // A magnet is hostless, so Uri exposes the whole "?xt=…&dn=…" as Query. Requiring the
        // BitTorrent exact topic is what makes this an info-hash link rather than any string that
        // happens to carry the magnet scheme.
        //
        // Every xt is considered, not just the first: a magnet may legitimately carry several exact
        // topics (a btih beside a urn:sha1 or a urn:btmh), in any order, and only one of them has to
        // be the BitTorrent hash. MagnetLinkAdmissionTests pins a second-position btih so a rewrite
        // that looked only at the first xt cannot pass.
        foreach (var parameter in parsedLink.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!parameter.StartsWith("xt=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = parameter["xt=".Length..];
            if (!value.StartsWith(BitTorrentExactTopicValuePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateHash = value[BitTorrentExactTopicValuePrefix.Length..];

            // codereview-492: a bare "xt=urn:btih:" carries no hash. Requiring a character here is
            // what keeps it out; keep scanning rather than returning, so an empty first btih does
            // not mask a real one later in the query.
            if (candidateHash.Length == 0)
            {
                continue;
            }

            // Verbatim: whatever case and encoding (hex or base32) the feed published, because this
            // value is rendered straight back out as the infohash attr.
            infoHash = candidateHash;
            validated = parsedLink;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parses a Torznab/Newznab search response into release candidates, dropping every item whose
    /// <c>&lt;link&gt;</c> is neither origin-pinned to <paramref name="allowedOrigin"/> nor an
    /// admissible magnet URI (see <see cref="TryValidateMagnetLink"/>).
    /// </summary>
    public static List<ReleaseCandidate> ParseFeedResponse(string xml, Uri allowedOrigin)
    {
        var doc = XDocument.Parse(xml);
        var items = doc.Descendants("item");
        var results = new List<ReleaseCandidate>();

        foreach (var item in items)
        {
            var title = item.Element("title")?.Value ?? string.Empty;
            var guid = item.Element("guid")?.Value ?? title;
            var link = item.Element("link")?.Value;
            var pubDateRaw = item.Element("pubDate")?.Value;

            // arb-x7w8.15: a magnet is admitted by its own branch, which never consults the origin
            // pin. It cannot: a magnet is hostless, so the pin refuses it and the item was dropped —
            // a magnet-only tracker returned nothing and said nothing about why. The pin itself is
            // deliberately untouched (see TryValidateMagnetLink's doc for why loosening it would
            // weaken the fetch-time SSRF gate as well as the write boundary). Ordering is
            // load-bearing only in that an http(s) link never reaches this branch: the magnet check
            // matches on the parsed scheme, so every http(s) value still goes to the pin below and
            // is refused or admitted exactly as before.
            //
            // arb-4ysc: the admission also hands back the btih it had to parse anyway. Taking it
            // HERE rather than re-parsing the link further down is the point: a second parse is a
            // second place for "what counts as an info hash" to be decided, and the copy that
            // drifts is the one nobody notices, because both keep producing plausible hashes. The
            // pinned arm leaves magnetInfoHash null — an http(s) link has no btih to give.
            if (!TryValidateMagnetLink(link, out var linkUri, out var magnetInfoHash)
                && !TryValidateOriginPinnedLink(link, allowedOrigin, out linkUri))
            {
                // Drop the item rather than defaulting to a placeholder URI: a placeholder would
                // still be a well-formed, fetchable target, defeating the point of the check.
                //
                // arb-iub9: dropping is silent, and it stays silent — this is a static parser with
                // no logger, and threading one in would change the signature of a method both
                // adapters call. The "source configured, search returns nothing, nothing says why"
                // shape that bead describes is closed at the OTHER end instead: a trailing-dot host
                // was the one accepted BaseUrl that could fail its own pin, and ValidateBaseUrl now
                // refuses it, so no stored origin can reach this line for every item it is sent.
                // UpstreamOrigin's doc states that agreement property; SourceRepositoryTests asserts
                // it over the shared corpus. Reaching here now means the UPSTREAM named a foreign
                // target, which is the case this drop exists for.
                continue;
            }

            var pubDate = TryParseDate(pubDateRaw) ?? DateTimeOffset.UtcNow;

            long size = 0;
            var sizeAttr = item.Elements(TorznabNs + "attr")
                .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, "size", StringComparison.OrdinalIgnoreCase));
            if (sizeAttr is not null && long.TryParse(sizeAttr.Attribute("value")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize))
            {
                size = parsedSize;
            }
            else if (long.TryParse(item.Element("size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fallbackSize))
            {
                size = fallbackSize;
            }

            var categories = item.Elements(TorznabNs + "attr")
                .Where(a => string.Equals(a.Attribute("name")?.Value, "category", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Attribute("value")?.Value)
                .Where(v => v is not null)
                .Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cat) ? cat : (int?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToArray();

            var protocolAttr = ReadAttr(item, "protocol");

            // arb-x7w8.15: the magnet arm sits in the FALLBACK, beside the enclosure sniff, not ahead
            // of the declared attribute. A magnet link is definitionally a torrent, and this arm's
            // pre-existing default is Usenet — so without it a magnet-bearing item from a feed that
            // omits the attribute (the common case for a magnet-only tracker) would be labelled
            // Usenet. That mislabelling is not a download hazard, because the download route detects
            // a magnet on the LINK and never on this value, but it would misreport the release to
            // every ranking and rendering surface that reads Protocol.
            var protocol = protocolAttr?.ToLowerInvariant() switch
            {
                "torrent" => ProtocolKind.Torrent,
                "usenet" => ProtocolKind.Usenet,
                _ when linkUri.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase) => ProtocolKind.Torrent,
                _ => item.Element("enclosure")?.Attribute("type")?.Value?.Contains("torrent", StringComparison.OrdinalIgnoreCase) == true
                    ? ProtocolKind.Torrent
                    : ProtocolKind.Usenet,
            };

            // arb-458f: the Usenet-side attrs. ClassificationPrompt tells the model to judge an
            // obfuscated Usenet title on "structural and metadata signals" instead of readability,
            // so these are the signals that sentence refers to — dropping them here left that
            // instruction pointing at nothing. Each is independently optional: an attr the upstream
            // indexer does not carry leaves its field null/empty rather than defaulting, because a
            // fabricated zero ("0 files", "not password-protected") is a claim the wire never made
            // and the model would read it as one.
            var poster = ReadAttr(item, "poster");

            // "group" is multi-valued: a crosspost lists one attr per newsgroup, so taking only the
            // first would silently narrow a crossposted release to a single group.
            var usenetGroup = item.Elements(TorznabNs + "attr")
                .Where(a => string.Equals(a.Attribute("name")?.Value, "group", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Attribute("value")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToArray();

            var files = int.TryParse(ReadAttr(item, "files"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFiles)
                ? parsedFiles
                : (int?)null;

            var grabs = int.TryParse(ReadAttr(item, "grabs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGrabs)
                ? parsedGrabs
                : (int?)null;

            // arb-4ysc: the info hash IndexerXmlWriter re-emits as torznab:attr name="infohash".
            // Before this, nothing on any production path set it, so that writer branch was dead and
            // Sonarr/Radarr never saw a hash from a real feed.
            //
            // The declared ATTR WINS over the magnet's btih when both are present and disagree. The
            // attr is what the indexer chose to publish as this release's identity, whereas the btih
            // is inferred from a link that also carries trackers and a display name; where they
            // differ the feed is telling us something about the release the link is not. This is the
            // same precedence the protocol switch above uses, for the same reason — a declared
            // attribute beats a value read out of the link. Both are taken VERBATIM: neither is
            // case-folded nor converted between hex and base32, because this value is rendered
            // straight back out and a reshaped hash is no longer the one the indexer published.
            var infoHash = ReadAttr(item, "infohash") is { } declaredInfoHash
                && !string.IsNullOrWhiteSpace(declaredInfoHash)
                    ? declaredInfoHash
                    : magnetInfoHash;

            results.Add(new ReleaseCandidate
            {
                Title = title,
                Guid = guid,
                PubDate = pubDate,
                Size = size,
                Link = linkUri,
                Category = categories,
                Protocol = protocol,
                InfoHash = infoHash,
                Poster = poster,
                UsenetGroup = usenetGroup,
                PasswordProtected = TryParsePasswordProtected(ReadAttr(item, "password")),
                Files = files,
                Grabs = grabs,
            });
        }

        return results;
    }

    /// <summary>Parses a Torznab/Newznab <c>t=caps</c> document into <see cref="SourceCaps"/>.</summary>
    public static SourceCaps ParseCapsResponse(string xml)
    {
        var doc = XDocument.Parse(xml);

        var categories = doc.Descendants()
            .Where(element => element.Name.LocalName is "category" or "subcat")
            .Select(element => new
            {
                Id = int.TryParse(element.Attribute("id")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : (int?)null,
                Name = element.Attribute("name")?.Value,
            })
            .Where(category => category.Id.HasValue)
            .ToArray();

        var categoryIds = categories
            .Select(category => category.Id!.Value)
            .Distinct()
            .ToArray();

        var categoryNames = categories
            .Where(category => string.IsNullOrWhiteSpace(category.Name) is false)
            .GroupBy(category => category.Id!.Value)
            .ToDictionary(group => group.Key, group => group.First().Name!);

        var searchingElement = doc.Descendants("searching").FirstOrDefault();
        var supportsTv = string.Equals(
            searchingElement?.Element("tv-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);
        var supportsMovie = string.Equals(
            searchingElement?.Element("movie-search")?.Attribute("available")?.Value,
            "yes",
            StringComparison.OrdinalIgnoreCase);

        var supportedParams = searchingElement?
            .Elements()
            .SelectMany(search => (search.Attribute("supportedParams")?.Value ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(parameter => parameter, StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        var limitsElement = doc.Descendants("limits").FirstOrDefault();
        int? maxPageSize = null;
        if (limitsElement is not null
            && int.TryParse(limitsElement.Attribute("max")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max))
        {
            maxPageSize = max;
        }

        return new SourceCaps(categoryIds, supportsTv, supportsMovie, maxPageSize, supportedParams, CategoryNames: categoryNames);
    }

    /// <summary>
    /// Reads the first single-valued <c>attr</c> with <paramref name="name"/>, or null when absent.
    /// </summary>
    private static string? ReadAttr(XElement item, string name) =>
        item.Elements(TorznabNs + "attr")
            .FirstOrDefault(a => string.Equals(a.Attribute("name")?.Value, name, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("value")?.Value;

    /// <summary>
    /// Newznab reports <c>password</c> as an integer severity (0 = none, non-zero = protected),
    /// not a boolean, so <c>bool.TryParse</c> would reject every real value and silently yield
    /// null. "true"/"false" are still accepted because some indexers emit them.
    /// </summary>
    private static bool? TryParsePasswordProtected(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return numeric != 0;
        }

        return bool.TryParse(raw, out var flag) ? flag : null;
    }

    private static DateTimeOffset? TryParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
