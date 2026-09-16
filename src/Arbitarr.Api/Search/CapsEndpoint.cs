using Arbitarr.Api.Rendering;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Search;

/// <summary>
/// Serves <c>t=caps</c> for both the Torznab and Newznab indexer endpoints: aggregates every
/// configured <see cref="IUpstreamSource"/>'s capabilities via <see cref="CapsAggregator"/>
/// (union categories minus book categories, intersection supported params, enforced max page
/// size, last-known-good fallback for a currently-unreachable source) and renders the merged
/// result as the appropriate protocol family's caps XML.
/// </summary>
public static class CapsEndpoint
{
    public static async Task<IResult> HandleTorznabAsync(
        CapsAggregator aggregator,
        ISourceRegistry registry,
        CancellationToken cancellationToken)
    {
        // arb-x7w8.4: resolved per request rather than injected as a fixed list, so a source added,
        // removed or disabled since startup is reflected in the caps this answers with.
        var sources = await registry.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var caps = await aggregator.AggregateAsync(sources, SearchProtocol.Torznab, cancellationToken).ConfigureAwait(false);
        var xml = TorznabXmlWriter.WriteCaps(caps);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), TorznabXmlWriter.ContentType);
    }

    public static async Task<IResult> HandleNewznabAsync(
        CapsAggregator aggregator,
        ISourceRegistry registry,
        CancellationToken cancellationToken)
    {
        // arb-x7w8.4: resolved per request rather than injected as a fixed list, so a source added,
        // removed or disabled since startup is reflected in the caps this answers with.
        var sources = await registry.ResolveAsync(cancellationToken).ConfigureAwait(false);
        var caps = await aggregator.AggregateAsync(sources, SearchProtocol.Newznab, cancellationToken).ConfigureAwait(false);
        var xml = NewznabXmlWriter.WriteCaps(caps);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), NewznabXmlWriter.ContentType);
    }
}
