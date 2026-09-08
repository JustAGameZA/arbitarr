namespace Arbitarr.Core.Sources;

/// <summary>
/// Which indexer protocol family an inbound search arrived on — the <c>/torznab/api</c> route or
/// the <c>/newznab/api</c> route — and therefore which upstream endpoint the search must be issued
/// against. NZBHydra2 treats its <c>/torznab/api</c> endpoint as a torrent search and excludes
/// every usenet indexer from it, so this is not a cosmetic distinction: sending a Newznab caller's
/// search to the torznab endpoint returns zero usenet results (#99).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT <see cref="Arbitarr.Core.Releases.ProtocolKind"/>, which is reused nowhere in
/// this file's sense. That enum answers "what IS this release" about a result already returned, and
/// carries an <c>Unknown</c> member for exactly that reason — an upstream item whose protocol
/// attribute and enclosure type are both silent is genuinely unknown, and
/// <c>ClassificationPrompt</c>/<c>IndexerXmlWriter</c> both branch on that third case. An inbound
/// request has no such case: it arrived on one of two routes and the answer is always known. Reusing
/// <see cref="Arbitarr.Core.Releases.ProtocolKind"/> would therefore put an unrepresentable
/// <c>Unknown</c> at every construction site and force a meaningless arm into every endpoint-
/// selection switch. A two-member enum keeps the choice total by construction.
/// </para>
/// <para>
/// It also has no zero-valued member, so a defaulted or unset field cannot silently mean
/// <see cref="Torznab"/> — that silent default is the shape of the bug this type exists to close.
/// </para>
/// </remarks>
public enum SearchProtocol
{
    /// <summary>The <c>/torznab/api</c> family: torrent-oriented; upstream path <c>{base}/torznab/api</c>.</summary>
    Torznab = 1,

    /// <summary>The <c>/newznab/api</c> family: Usenet-oriented; upstream path <c>{base}/api</c>.</summary>
    Newznab = 2,
}
