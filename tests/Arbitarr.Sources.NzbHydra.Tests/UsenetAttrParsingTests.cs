using System.Net;
using System.Text;
using Arbitarr.Core.Releases;
using Arbitarr.Core.Sources;

namespace Arbitarr.Sources.NzbHydra.Tests;

/// <summary>
/// arb-458f: the Usenet-side <c>attr</c> names (poster, group, files, password, grabs) must reach
/// <see cref="ReleaseCandidate"/>. They were modelled but never populated, which left
/// <c>ClassificationPrompt</c>'s instruction to judge obfuscated Usenet releases by "structural and
/// metadata signals" pointing at fields that were always empty.
///
/// <para>
/// Every assertion here is made PER FIELD (CLAUDE.md §4). A single "the candidate has some Usenet
/// metadata" assertion passes while four of the five parse lines are missing, which is the failure
/// this file exists to prevent — so each field gets its own named assertion in the populated case
/// AND its own assertion in the absent case.
/// </para>
///
/// <para>
/// Fixture note: the repository's captured NZBHydra2 corpus under <c>docs/fixtures/nzbhydra/</c> is
/// torrent-only (its README records that no Usenet item was returned by any capture query), so
/// there is no captured Usenet item to drive these from. The XML here is therefore hand-built to
/// the Newznab attr shape, using the same <c>example.test</c> origin and planted shapes the
/// neighbouring parser tests already use — no real host or key is introduced.
/// </para>
/// </summary>
public class UsenetAttrParsingTests
{
    private static NzbHydraSourceOptions MakeOptions() => new(
        BaseUrl: new Uri("http://hydra.example.test:5076/"),
        ApiKey: "secret-api-key",
        SourceName: "test-hydra",
        RequestTimeout: TimeSpan.FromSeconds(2),
        MaxUpstreamPageSize: 100,
        MaxUpstreamCallsPerSearch: 1,
        RateLimitMaxCalls: 1000,
        RateLimitInterval: TimeSpan.FromMilliseconds(1));

    private static async Task<ReleaseCandidate> ParseSingleAsync(string itemBody, string prefix = "torznab")
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
            + $"<rss xmlns:{prefix}=\"http://torznab.com/schemas/2015/feed\"><channel>"
            + "<item>"
            + "<title>Obfuscated.Release.Name</title>"
            + "<guid>guid-usenet-1</guid>"
            + "<link>http://hydra.example.test:5076/download/1</link>"
            + "<pubDate>Thu, 27 Aug 2026 12:00:00 +0000</pubDate>"
            + $"<{prefix}:attr name=\"size\" value=\"12345\" />"
            + $"<{prefix}:attr name=\"category\" value=\"5000\" />"
            + $"<{prefix}:attr name=\"protocol\" value=\"usenet\" />"
            + itemBody
            + "</item>"
            + "</channel></rss>";

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
        });
        var source = new NzbHydraSource(MakeOptions(), new HttpClient(handler), new FakeCircuitBreaker());

        var results = await source.SearchAsync(
            new SearchQuery("obfuscated", Array.Empty<int>(), Protocol: SearchProtocol.Newznab, Limit: 1));

        return Assert.Single(results);
    }

    private const string AllAttrs =
        "<torznab:attr name=\"poster\" value=\"a1b2c3@example.invalid\" />"
        + "<torznab:attr name=\"group\" value=\"alt.binaries.example\" />"
        + "<torznab:attr name=\"files\" value=\"42\" />"
        + "<torznab:attr name=\"password\" value=\"0\" />"
        + "<torznab:attr name=\"grabs\" value=\"77\" />";

    // ---- Populated: each field individually ---------------------------------------------------

    [Fact]
    public async Task Parse_WithAttrs_PopulatesPoster()
        => Assert.Equal("a1b2c3@example.invalid", (await ParseSingleAsync(AllAttrs)).Poster);

    [Fact]
    public async Task Parse_WithAttrs_PopulatesUsenetGroup()
        => Assert.Equal(new[] { "alt.binaries.example" }, (await ParseSingleAsync(AllAttrs)).UsenetGroup);

    [Fact]
    public async Task Parse_WithAttrs_PopulatesFiles()
        => Assert.Equal(42, (await ParseSingleAsync(AllAttrs)).Files);

    [Fact]
    public async Task Parse_WithAttrs_PopulatesPasswordProtected()
        => Assert.False((await ParseSingleAsync(AllAttrs)).PasswordProtected);

    [Fact]
    public async Task Parse_WithAttrs_PopulatesGrabs()
        => Assert.Equal(77, (await ParseSingleAsync(AllAttrs)).Grabs);

    // ---- Absent: each field individually stays null/empty, never a fabricated zero -------------

    [Fact]
    public async Task Parse_WithoutAttrs_LeavesPosterNull()
        => Assert.Null((await ParseSingleAsync(string.Empty)).Poster);

    [Fact]
    public async Task Parse_WithoutAttrs_LeavesUsenetGroupEmpty()
        => Assert.Empty((await ParseSingleAsync(string.Empty)).UsenetGroup);

    [Fact]
    public async Task Parse_WithoutAttrs_LeavesFilesNull()
        => Assert.Null((await ParseSingleAsync(string.Empty)).Files);

    [Fact]
    public async Task Parse_WithoutAttrs_LeavesPasswordProtectedNull()
        => Assert.Null((await ParseSingleAsync(string.Empty)).PasswordProtected);

    [Fact]
    public async Task Parse_WithoutAttrs_LeavesGrabsNull()
        => Assert.Null((await ParseSingleAsync(string.Empty)).Grabs);

    // ---- Shape details --------------------------------------------------------------------

    /// <summary>
    /// A Newznab feed reuses the Torznab schema URI under a different prefix, so matching on the
    /// namespace URI (not the literal prefix) is what makes the <c>/api</c> endpoint's
    /// <c>newznab:attr</c> parse identically. Driven through the real prefix rather than asserted
    /// on the constant, so a regression to prefix-matching fails here.
    /// </summary>
    [Fact]
    public async Task Parse_NewznabPrefixedAttrs_ReadIdenticallyToTorznabPrefixed()
    {
        var candidate = await ParseSingleAsync(
            "<newznab:attr name=\"poster\" value=\"a1b2c3@example.invalid\" />"
            + "<newznab:attr name=\"files\" value=\"42\" />",
            prefix: "newznab");

        Assert.Equal("a1b2c3@example.invalid", candidate.Poster);
        Assert.Equal(42, candidate.Files);
    }

    /// <summary>
    /// Newznab reports <c>password</c> as an integer severity, not a boolean — a non-zero value
    /// means protected. <c>bool.TryParse</c> would reject "1" outright and report null (unknown)
    /// for a release the indexer explicitly flagged.
    /// </summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public async Task Parse_PasswordAttr_ReadsIntegerSeverityAndBooleanForms(string raw, bool expected)
    {
        var candidate = await ParseSingleAsync($"<torznab:attr name=\"password\" value=\"{raw}\" />");

        Assert.Equal(expected, candidate.PasswordProtected);
    }

    /// <summary>
    /// A crossposted release carries one <c>group</c> attr per newsgroup; taking only the first
    /// would silently narrow it.
    /// </summary>
    [Fact]
    public async Task Parse_MultipleGroupAttrs_KeepsEveryNewsgroup()
    {
        var candidate = await ParseSingleAsync(
            "<torznab:attr name=\"group\" value=\"alt.binaries.example\" />"
            + "<torznab:attr name=\"group\" value=\"alt.binaries.example.two\" />");

        Assert.Equal(
            new[] { "alt.binaries.example", "alt.binaries.example.two" },
            candidate.UsenetGroup);
    }

    /// <summary>
    /// A non-numeric <c>files</c>/<c>grabs</c> value is unknown, not zero: defaulting it to 0 would
    /// put a claim the wire never made in front of the classifier.
    /// </summary>
    [Fact]
    public async Task Parse_NonNumericCountAttrs_AreNullNotZero()
    {
        var candidate = await ParseSingleAsync(
            "<torznab:attr name=\"files\" value=\"not-a-number\" />"
            + "<torznab:attr name=\"grabs\" value=\"\" />");

        Assert.Null(candidate.Files);
        Assert.Null(candidate.Grabs);
    }

    /// <summary>
    /// The pre-existing fields must keep parsing exactly as before — this change adds fields, it
    /// does not touch how title/size/category/protocol are read.
    /// </summary>
    [Fact]
    public async Task Parse_WithUsenetAttrs_LeavesCoreFieldsUnchanged()
    {
        var candidate = await ParseSingleAsync(AllAttrs);

        Assert.Equal("Obfuscated.Release.Name", candidate.Title);
        Assert.Equal(12345, candidate.Size);
        Assert.Equal(new[] { 5000 }, candidate.Category);
        Assert.Equal(ProtocolKind.Usenet, candidate.Protocol);
    }
}
