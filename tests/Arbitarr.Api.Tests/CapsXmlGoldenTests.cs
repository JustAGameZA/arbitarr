using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Sources;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// Golden-XML tests for t=caps rendering: both protocol families, book categories never
/// present (M1-12), and enforced max page size.
/// </summary>
public class CapsXmlGoldenTests
{
    private static readonly SourceCaps SampleCaps = new(
        SupportedCategories: new[] { 5000, 2000, 3000 },
        SupportsTvSearch: true,
        SupportsMovieSearch: true,
        MaxPageSize: 100,
        SupportedParams: new[] { "q", "season", "ep" });

    [Fact]
    public void Torznab_caps_render_expected_namespace_and_categories()
    {
        var xml = TorznabXmlWriter.WriteCaps(SampleCaps);
        var rendered = xml.ToString();

        Assert.Contains("<caps>", rendered);
        Assert.Contains("<category id=\"5000\" name=\"TV\" />", rendered);
        Assert.Contains("<category id=\"2000\" name=\"Movies\" />", rendered);
        Assert.Contains("<category id=\"3000\" name=\"Audio\" />", rendered);
        Assert.Contains("<limits max=\"100\" default=\"100\" />", rendered);
    }

    [Fact]
    public void Newznab_caps_render_same_shape_as_torznab()
    {
        var torznabXml = TorznabXmlWriter.WriteCaps(SampleCaps).ToString();
        var newznabXml = NewznabXmlWriter.WriteCaps(SampleCaps).ToString();

        // Caps rendering carries no family-specific namespace prefix (only search-result items
        // do), so both families render byte-identical caps XML for the same SourceCaps input.
        Assert.Equal(torznabXml, newznabXml);
    }

    [Fact]
    public async Task Torznab_caps_render_exact_whole_document()
    {
        // Whole-document golden test (torznab caps for the fixed SampleCaps aggregator input),
        // exact bytes, rendered through the real production path (CapsEndpoint.HandleTorznabAsync
        // -> XmlDocumentRendering.ToXmlString) rather than a bare XDocument.ToString() — this
        // previously bypassed the production renderer entirely (no XML declaration, host-OS-
        // dependent newlines). Newlines are "\n" only: the production renderer pins NewLineChars
        // explicitly so the wire format is byte-identical regardless of host OS.
        var source = new SingleCapsUpstreamSource("eztv", SampleCaps);
        var aggregator = new CapsAggregator(new NoOpCapsCacheStore());

        var services = new ServiceCollection();
        services.AddLogging();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };

        var result = await CapsEndpoint.HandleTorznabAsync(
            aggregator,
            new[] { (IUpstreamSource)source },
            CancellationToken.None);

        using var body = new MemoryStream();
        httpContext.Response.Body = body;
        await result.ExecuteAsync(httpContext);
        body.Seek(0, SeekOrigin.Begin);
        var rendered = new StreamReader(body).ReadToEnd();

        const string expected =
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" +
            "<caps>\n" +
            "  <server version=\"1.0\" title=\"Arbitarr\" />\n" +
            "  <limits max=\"100\" default=\"100\" />\n" +
            "  <searching>\n" +
            "    <search available=\"yes\" supportedParams=\"ep,q,season\" />\n" +
            "    <tv-search available=\"yes\" supportedParams=\"ep,q,season\" />\n" +
            "    <movie-search available=\"yes\" supportedParams=\"ep,q,season\" />\n" +
            "  </searching>\n" +
            "  <categories>\n" +
            "    <category id=\"2000\" name=\"Movies\" />\n" +
            "    <category id=\"3000\" name=\"Audio\" />\n" +
            "    <category id=\"5000\" name=\"TV\" />\n" +
            "  </categories>\n" +
            "</caps>";

        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void Caps_writer_renders_exactly_the_categories_it_is_given()
    {
        // M1-12's "book categories never appear in caps" guarantee is enforced upstream by
        // CapsAggregator (see CapsAggregatorTests), which never includes SourceCaps.BookCategoryIds
        // in the SourceCaps it hands to this writer. The writer itself is a pure passthrough over
        // whatever SupportedCategories it receives — this asserts that passthrough contract.
        var rendered = TorznabXmlWriter.WriteCaps(SampleCaps).ToString();

        foreach (var categoryId in SampleCaps.SupportedCategories)
        {
            Assert.Contains($"id=\"{categoryId}\"", rendered);
        }
    }

    /// <summary>Minimal <see cref="IUpstreamSource"/> double that returns a fixed <see cref="SourceCaps"/>.</summary>
    private sealed class SingleCapsUpstreamSource : IUpstreamSource
    {
        private readonly SourceCaps _caps;

        public SingleCapsUpstreamSource(string name, SourceCaps caps)
        {
            Name = name;
            _caps = caps;
        }

        public string Name { get; }

        public Task<IReadOnlyList<Arbitarr.Core.Releases.ReleaseCandidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Arbitarr.Core.Releases.ReleaseCandidate>>(Array.Empty<Arbitarr.Core.Releases.ReleaseCandidate>());

        public Task<SourceCaps> GetCapsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_caps);

        public Task<Stream> FetchDownloadAsync(Arbitarr.Core.Releases.ReleaseCandidate release, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());
    }

    /// <summary>No-op <see cref="ICapsCacheStore"/> double — this test never exercises the fallback path.</summary>
    private sealed class NoOpCapsCacheStore : ICapsCacheStore
    {
        public Task<SourceCaps?> GetLastKnownGoodAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceCaps?>(null);

        public Task SaveAsync(string sourceName, SourceCaps caps, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
