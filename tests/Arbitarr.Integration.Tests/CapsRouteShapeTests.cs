using System.Net;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-gme: pins the two behaviours the bead's investigation needed distinguished — a bad CLIENT
/// KEY on a correctly-shaped search route (answered by <c>ApiKeyValidator</c>, HTTP 200 with a
/// Torznab/Newznab error-100 body) versus a bad ROUTE SHAPE (answered by the terminal 404 fallback
/// at <c>Program.cs</c>'s <c>"/api/{*rest}"</c>, before any apikey is ever read). The bead's title
/// symptom — Sonarr's audit reporting <c>[404:NotFound] ... t=caps</c> — matches the second case,
/// not the first: <c>/api?t=caps</c> is not <c>/torznab/api</c> or <c>/newznab/api</c>, so it never
/// reaches <see cref="Arbitarr.Api.Search.ApiKeyValidator"/> at all.
///
/// <para><see cref="MintedClientApiKeyTests"/> already pins a WRONG key's shape (200 + error 100)
/// on <c>t=caps</c> for both protocol routes; what it does not cover, and what this class adds, is
/// (a) an ABSENT <c>apikey</c> parameter altogether (as opposed to a present-but-wrong one) and (b)
/// the same pairing on <c>t=search</c>, plus (c) the wrong-URL shapes that plausibly produce the
/// bead's reported 404 for comparison against (a)/(b).</para>
/// </summary>
public sealed class CapsRouteShapeTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public CapsRouteShapeTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// An absent <c>apikey</c> on the real, correctly-shaped caps route answers exactly like a
    /// present-but-wrong one: HTTP 200 carrying an error-100 body, never a 404. Status alone would
    /// pass just as happily if the route silently stopped requiring a key, so the body is asserted
    /// too — see <see cref="MintedClientApiKeyTests"/>'s remark on the same pitfall for the accepted
    /// case (that class's private <c>AssertAcceptedAsync</c> helper).
    /// </summary>
    [Theory]
    [InlineData("/torznab/api", "t=caps")]
    [InlineData("/newznab/api", "t=caps")]
    [InlineData("/torznab/api", "t=search&q=x")]
    [InlineData("/newznab/api", "t=search&q=x")]
    public async Task A_correctly_shaped_search_route_with_no_apikey_answers_200_with_an_error_100_body(
        string route, string query)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"{route}?{query}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("code=\"100\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The plausible wrong-URL shapes an operator or an *arr client could paste into an indexer's
    /// base URL field: the bare <c>/api</c> prefix (no <c>/torznab</c> or <c>/newznab</c> segment at
    /// all — the shape the bead's title names), the same with a trailing slash, and a nonexistent
    /// protocol segment. All three MISS every registered search route and fall through to the
    /// terminal fallback at <c>Program.cs</c>'s <c>app.MapFallback("/api/{*rest}", ...)</c>, which
    /// answers a bare 404 regardless of any apikey — explaining the bead's reported symptom without
    /// requiring the search routes' own 200-plus-error-100 behaviour to change.
    /// </summary>
    [Theory]
    [InlineData("/api?t=caps")]
    [InlineData("/api/?t=caps")]
    [InlineData("/api/torznab?t=caps")]
    public async Task A_wrong_url_shape_for_caps_answers_a_bare_404_regardless_of_apikey(string pathAndQuery)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"{pathAndQuery}&apikey=irrelevant");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
