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
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken)
    {
        var caps = await aggregator.AggregateAsync(sources, cancellationToken).ConfigureAwait(false);
        var xml = TorznabXmlWriter.WriteCaps(caps);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), TorznabXmlWriter.ContentType);
    }

    public static async Task<IResult> HandleNewznabAsync(
        CapsAggregator aggregator,
        IReadOnlyList<IUpstreamSource> sources,
        CancellationToken cancellationToken)
    {
        var caps = await aggregator.AggregateAsync(sources, cancellationToken).ConfigureAwait(false);
        var xml = NewznabXmlWriter.WriteCaps(caps);
        return Results.Text(XmlDocumentRendering.ToXmlString(xml), NewznabXmlWriter.ContentType);
    }
}
