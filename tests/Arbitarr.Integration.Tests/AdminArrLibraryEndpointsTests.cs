using System.Net;
using System.Text;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Media;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-6l9b.4: <c>GET /api/admin/arr/sonarr/series</c> and <c>GET /api/admin/arr/radarr/movies</c>
/// end to end against the real Host, with only the upstream TRANSPORT replaced.
///
/// <para><b>WHAT IS REAL HERE AND WHY THAT MATTERS.</b> Each test replaces the primary handler of one
/// registered typed client and nothing else — the credential lookup, the status classification, the
/// projection, the server-side filter/sort/page, the envelope and the route's admin gate all remain
/// the code <c>Program.cs</c> composed. That distinction is the reason #57's webhook leak shipped
/// green: a test that drives a path bypassing the real composition proves only that the code it
/// hand-wired works.</para>
///
/// <para><b>EVERY ABSENCE ASSERTION HERE CARRIES A POSITIVE CONTROL (CLAUDE.md §4).</b>
/// <c>Assert.DoesNotContain(secret, body)</c> passes just as happily when the secret was never in
/// play — an empty set contains nothing, and three leaks have shipped in this repository behind
/// exactly that shape. So each test below first demonstrates that the planted value WOULD be found by
/// the same search over the same body, and only then asserts the real response carries none of it.
/// Asserting merely that the fixture was created proves the value EXISTS; it does not prove it would
/// be DETECTABLE if it leaked.</para>
///
/// <para>All addresses are documentation forms (<c>sonarr.example:8989</c>,
/// <c>radarr.example:7878</c>) and every credential-shaped string carries the <c>placeholder-</c>
/// prefix: no real address or secret enters committed content.</para>
/// </summary>
public sealed class AdminArrLibraryEndpointsTests
{
    private const string AdminKey = "the-real-admin-key";

    /// <summary>
    /// Distinctive enough that a substring search over a whole response body cannot match it by
    /// accident, which is what makes the "it is not in here" assertions meaningful.
    /// </summary>
    private const string SonarrKey = "placeholder-sonarr-key-7c2d48fa";

    private const string RadarrKey = "placeholder-radarr-key-8d3e59gb";

    private const string SonarrBaseUrl = "http://sonarr.example:8989";
    private const string RadarrBaseUrl = "http://radarr.example:7878";

    /// <summary>
    /// A marker planted in the UPSTREAM body. The fixed-message tests assert it is absent from the
    /// whole response, which is what proves <c>DescribeStatus</c> chose its wording from the status
    /// alone rather than interpolating anything the upstream said.
    /// </summary>
    private const string UpstreamMarker = "UPSTREAM-BODY-MARKER-3b81e5";

    // ---------------------------------------------------------------------------------------------
    // Gating, by name. The sweep in AdminApiKeyRouteEnumerationTests covers these routes generically
    // (both are concrete), but that coverage is a property of the route SHAPE rather than of this
    // feature. Pinned here so neither route is gated only by assumption.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both library routes refuse a request carrying no admin key.
    ///
    /// <para>The request deliberately sends NO body, for the same reason
    /// <c>AdminApiKeyRouteEnumerationTests</c>' sweep does: a required body is model-bound BEFORE the
    /// endpoint filter and would short-circuit to 400 without the gate running, letting an
    /// unauthenticated caller tell a malformed body from a well-formed one and enumerate which admin
    /// routes exist. These routes take no body at all, which is what keeps the gate strictly first.
    /// Do not "fix" the bodilessness.</para>
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task Every_library_route_rejects_a_request_without_the_admin_key(string path)
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await client.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable,
            $"Expected GET {path} to be admin-gated, but it returned {(int)response.StatusCode}.");
    }

    /// <summary>
    /// The gate is by PATH PREFIX rather than by verb: these are GETs, and they are gated exactly as
    /// the writes on the same prefix are, because a read that makes an authenticated outbound call
    /// with a stored credential is admin surface and not a dashboard fact.
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task The_gate_is_by_path_prefix_so_a_GET_is_gated_exactly_like_a_write(string path)
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        using var client = factory.CreateClient();

        using var unkeyed = await client.GetAsync(path);
        Assert.True(unkeyed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ServiceUnavailable);

        using var keyed = await GetAsync(client, path);
        Assert.Equal(HttpStatusCode.OK, keyed.StatusCode);
    }

    // ---------------------------------------------------------------------------------------------
    // Status branches, BOTH KINDS. One assertion per case.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// NOT CONFIGURED, HALF ONE: nothing is stored at all. The top level is still 200 — the admin call
    /// succeeded and is reporting faithfully what it found — and the verdict is in the envelope.
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task An_unconfigured_instance_is_NotConfigured(string path)
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.NotConfigured)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// NOT CONFIGURED, HALF TWO: an address IS stored, but no key. The credential provider reports
    /// that half-configured state as null, and reading it as NotConfigured rather than attempting the
    /// call is what stops the surface reporting AuthenticationFailed against an *arr that is not
    /// actually broken — the operator's fix is "supply a key", not "correct the key you supplied".
    ///
    /// <para>The positive control is the stored address: without it this test would be
    /// indistinguishable from the one above, which covers the nothing-stored case, and would prove
    /// nothing about the half-configured branch at all.</para>
    /// </summary>
    [Fact]
    public async Task A_sonarr_address_with_no_key_is_NotConfigured_rather_than_AuthenticationFailed()
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new ArrInstanceRepository(db).SetAsync(
                SonarrBaseUrl, apiKey: null, CancellationToken.None));

        // POSITIVE CONTROL: the address really is stored, so the verdict below is about the missing
        // key and not about an instance that was never configured.
        Assert.Equal(SonarrBaseUrl, await ReadStoredValueAsync(factory, ArrInstanceRepository.SonarrBaseUrlSettingName));
        Assert.Null(await ReadStoredValueAsync(factory, ArrInstanceRepository.SonarrApiKeySettingName));

        using var client = factory.CreateClient();
        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.NotConfigured)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>The Radarr half of the half-configured branch, with the same stored-address control.</summary>
    [Fact]
    public async Task A_radarr_address_with_no_key_is_NotConfigured_rather_than_AuthenticationFailed()
    {
        using var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new RadarrInstanceRepository(db).SetAsync(
                RadarrBaseUrl, apiKey: null, CancellationToken.None));

        // POSITIVE CONTROL: the address really is stored.
        Assert.Equal(RadarrBaseUrl, await ReadStoredValueAsync(factory, RadarrInstanceRepository.RadarrBaseUrlSettingName));
        Assert.Null(await ReadStoredValueAsync(factory, RadarrInstanceRepository.RadarrApiKeySettingName));

        using var client = factory.CreateClient();
        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.NotConfigured)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_series_document_is_Ok_and_the_records_reach_the_response()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("Example Show", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_movie_document_is_Ok_and_the_records_reach_the_response()
    {
        using var factory = await ConfiguredRadarrAsync(Json(MoviesBody("Example Movie")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("Example Movie", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SLIM PROJECTION really is the served shape: the members the bead names are present and the
    /// nested upstream rollup has been flattened onto them. Without this the exclusion tests below
    /// could pass against a response that carried nothing at all.
    /// </summary>
    [Fact]
    public async Task The_series_projection_serves_exactly_the_slim_members()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"id\":11", body, StringComparison.Ordinal);
        Assert.Contains("\"year\":2019", body, StringComparison.Ordinal);
        Assert.Contains("\"tvdbId\":424242", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"continuing\"", body, StringComparison.Ordinal);
        Assert.Contains("\"monitored\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"network\":\"Example Network\"", body, StringComparison.Ordinal);
        Assert.Contains("\"sizeOnDisk\":987654321", body, StringComparison.Ordinal);

        // The nested statistics rollup is flattened onto the item rather than served as an object.
        Assert.Contains("\"seasonCount\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"episodeFileCount\":28", body, StringComparison.Ordinal);
        Assert.Contains("\"episodeCount\":30", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"statistics\"", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Radarr half: the movie item's own slim members.</summary>
    [Fact]
    public async Task The_movie_projection_serves_exactly_the_slim_members()
    {
        using var factory = await ConfiguredRadarrAsync(Json(MoviesBody("Example Movie")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"id\":21", body, StringComparison.Ordinal);
        Assert.Contains("\"year\":2021", body, StringComparison.Ordinal);
        Assert.Contains("\"tmdbId\":515151", body, StringComparison.Ordinal);
        Assert.Contains("\"monitored\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"hasFile\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"sizeOnDisk\":123456789", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"released\"", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task An_unreachable_instance_is_Unreachable(string path)
    {
        using var factory = await ConfiguredAsync(
            path,
            new ThrowingHandler(new HttpRequestException(
                "connection refused",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.Unreachable)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task A_401_from_the_instance_is_AuthenticationFailed(string path)
    {
        using var factory = await ConfiguredAsync(
            path,
            new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.AuthenticationFailed)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task A_non_JSON_body_is_UnexpectedResponse(string path)
    {
        using var factory = await ConfiguredAsync(
            path,
            Json("<html><body>Please sign in</body></html>"));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// JSON OF THE WRONG SHAPE is UnexpectedResponse too. These endpoints answer with a bare ARRAY at
    /// the top level, unlike the queue's <c>{records:[...]}</c> envelope, so an OBJECT is the shape
    /// mistake available here — a proxy's error document, or an *arr answering a different route.
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task A_JSON_object_where_an_array_belongs_is_UnexpectedResponse(string path)
    {
        using var factory = await ConfiguredAsync(path, Json("""{"error":"not a library"}"""));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task A_500_from_the_instance_is_UnexpectedResponse(string path)
    {
        using var factory = await ConfiguredAsync(
            path,
            new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// AN EMPTY BODY is UnexpectedResponse rather than an empty library. A 200 carrying nothing is not
    /// "you have no series" — <c>[]</c> is that — it is a proxy or a truncated response, and reporting
    /// it as an empty library would tell an operator their configuration is fine when it is not.
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task An_empty_body_is_UnexpectedResponse(string path)
    {
        using var factory = await ConfiguredAsync(path, Json(string.Empty));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An EMPTY ARRAY is the genuinely-empty library, and it is <see cref="ArrSectionStatus.Ok"/> with
    /// a zero total — the contrast that makes the empty-body case above mean something.
    /// </summary>
    [Theory]
    [InlineData(AdminArrLibraryEndpoints.SonarrSeriesRoute)]
    [InlineData(AdminArrLibraryEndpoints.RadarrMoviesRoute)]
    public async Task An_empty_array_is_Ok_with_a_zero_total(string path)
    {
        using var factory = await ConfiguredAsync(path, Json("[]"));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, path);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"totalRecords\":0", body, StringComparison.Ordinal);
        Assert.Contains("\"records\":[]", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A REDIRECTING UPSTREAM IS <see cref="ArrSectionStatus.UnexpectedResponse"/>, AND NO SECOND
    /// REQUEST LEAVES THIS PROCESS — driven through the REAL registered client, so the
    /// <c>AllowAutoRedirect = false</c> the registration sets is the thing under test.
    ///
    /// <para>UnexpectedResponse rather than Unreachable because something DID answer: the operator's
    /// fix is the base URL (a proxy, a login redirect, the wrong service), not a host that is down.
    /// The request-count assertion is the SECURITY half — the SSRF property that registration exists
    /// for is that a key-bearing request is never reissued at a host nobody configured, which is a
    /// property about the second request not happening and which the status alone cannot show.</para>
    /// </summary>
    [Fact]
    public async Task A_redirecting_sonarr_is_UnexpectedResponse_and_no_second_request_is_issued()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://elsewhere.example/api/v3/series");

        var handler = new CountingHandler(redirect);
        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // The redirect was not followed: exactly one request left this process, and it went to the
        // configured address rather than to the one the redirect named.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("sonarr.example", handler.LastRequestUri!.Host);
    }

    /// <summary>The Radarr half of the redirect property, with the same one-request assertion.</summary>
    [Fact]
    public async Task A_redirecting_radarr_is_UnexpectedResponse_and_no_second_request_is_issued()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://elsewhere.example/api/v3/movie");

        var handler = new CountingHandler(redirect);
        using var factory = await ConfiguredRadarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("radarr.example", handler.LastRequestUri!.Host);
    }

    // ---------------------------------------------------------------------------------------------
    // The upstream request: ONLY the key is sent.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE UPSTREAM REQUEST CARRIES <c>apikey</c> AND NOTHING ELSE, and it goes to the unpaged
    /// library path.
    ///
    /// <para><c>/api/v3/series</c> and <c>/api/v3/movie</c> take no paging or filtering parameters —
    /// that is WHY this bead pages server-side — so sending any would be at best inert and at worst
    /// wrong. <c>includeUnknownSeriesItems</c> / <c>includeUnknownMovieItems</c> are QUEUE parameters
    /// and mean nothing here; <c>tvdbId</c> / <c>tmdbId</c> would select a single title and silently
    /// turn a library listing into a lookup. Asserted rather than assumed because the caller's own
    /// <c>?page=</c>, <c>?pageSize=</c> and <c>?q=</c> are RIGHT THERE in the inbound request, and
    /// forwarding them is the obvious-looking edit.</para>
    /// </summary>
    [Fact]
    public async Task The_sonarr_request_sends_only_the_key_to_the_unpaged_series_path()
    {
        var handler = new CountingHandler(JsonResponse(SeriesBody("Example Show")));
        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?q=example&page=2&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var uri = handler.LastRequestUri!;
        Assert.Equal("/api/v3/series", uri.AbsolutePath);

        // POSITIVE CONTROL: the one parameter that SHOULD be there is, found by the same kind of
        // search the absences below use.
        Assert.Contains("apikey=", uri.Query, StringComparison.Ordinal);

        // ...therefore these absences are real. The caller's own paging and filter stayed here.
        Assert.DoesNotContain("page", uri.Query.Replace("apikey=", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("q=", uri.Query.Replace("apikey=", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("includeUnknown", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tvdbId", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Radarr half, against its own singular path.</summary>
    [Fact]
    public async Task The_radarr_request_sends_only_the_key_to_the_unpaged_movie_path()
    {
        var handler = new CountingHandler(JsonResponse(MoviesBody("Example Movie")));
        using var factory = await ConfiguredRadarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.RadarrMoviesRoute + "?q=example&page=2&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var uri = handler.LastRequestUri!;
        Assert.Equal("/api/v3/movie", uri.AbsolutePath);

        Assert.Contains("apikey=", uri.Query, StringComparison.Ordinal);

        Assert.DoesNotContain("page", uri.Query.Replace("apikey=", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("q=", uri.Query.Replace("apikey=", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("includeUnknown", uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tmdbId", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // The fixed message, and the key.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE MESSAGE IS CHOSEN FROM THE STATUS ALONE, AND NOTHING THE UPSTREAM SAID REACHES IT.
    ///
    /// <para>The upstream answers with a body carrying a distinctive marker. The assertion is that the
    /// response's message equals the fixed wording for that status AND that the marker is absent from
    /// the WHOLE response — so an implementation that appended "(upstream said: ...)" fails here
    /// rather than shipping. The marker in the fixture is the positive control: without it the absence
    /// assertion would pass against any response at all.</para>
    ///
    /// <para>Run against the UnexpectedResponse branch specifically because that is the branch a
    /// well-meaning edit is most likely to make chatty — it is the one where the upstream body is the
    /// only thing that could explain what actually answered.</para>
    /// </summary>
    [Fact]
    public async Task The_sonarr_message_is_fixed_wording_and_never_carries_the_upstream_body()
    {
        var upstreamBody = $$"""{"error":"{{UpstreamMarker}} - this service is not an arr"}""";

        // DETECTABILITY CONTROL: the marker really is in what the upstream returns, found by the very
        // search used against the response below.
        Assert.Contains(UpstreamMarker, upstreamBody, StringComparison.Ordinal);

        using var factory = await ConfiguredSonarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        // NON-VACUITY: the request really did reach the UnexpectedResponse branch, so the absence
        // below is about a message that had the opportunity to carry the marker.
        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            body,
            StringComparison.Ordinal);

        // The wording is the fixed one for this status, character for character.
        Assert.Contains(
            "Something answered but it was not Sonarr.",
            body,
            StringComparison.Ordinal);

        // ...and nothing the upstream said came with it.
        Assert.DoesNotContain(UpstreamMarker, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Radarr half of the fixed-message property, with the same planted marker.</summary>
    [Fact]
    public async Task The_radarr_message_is_fixed_wording_and_never_carries_the_upstream_body()
    {
        var upstreamBody = $$"""{"error":"{{UpstreamMarker}} - this service is not an arr"}""";

        Assert.Contains(UpstreamMarker, upstreamBody, StringComparison.Ordinal);

        using var factory = await ConfiguredRadarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "Something answered but it was not Radarr.",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(UpstreamMarker, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE OK MESSAGE IS THIS SURFACE'S OWN WORDING, not the queue's. The two sections are free to
    /// word themselves differently — what they may not do is derive the wording from anything but the
    /// status — and this pins that the library endpoints did take their own.
    /// </summary>
    [Fact]
    public async Task The_ok_message_names_the_library_section_rather_than_the_queue()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Read the Sonarr series successfully.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("queue", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// THE KEY NEVER REACHES THE RESPONSE BODY, WITH A POSITIVE CONTROL.
    ///
    /// <para><b>THE CONTROL IS A DISTINCTIVE STRING THAT IS GENUINELY IN THE REAL BODY.</b> Finding it
    /// proves this search, over THIS body, actually finds things that are there. Only then does the
    /// key's absence mean the key is not there, rather than meaning the body was empty, the request
    /// 404'd, or the search was looking at the wrong response.</para>
    ///
    /// <para>The control differs per branch because the two branches carry different content. On the
    /// Ok branch it is the fixture's series title, which travelled the whole projection path. On the
    /// failure branch there are no records at all, so it is the fixed wording for that status — still
    /// distinctive, still genuinely in the response, still produced by the request under test.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_sonarr_key_never_appears_in_the_response_body(bool upstreamSucceeds)
    {
        var handler = upstreamSucceeds
            ? Json(SeriesBody("Example Show"))
            : new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        // NON-VACUITY: the response is a real envelope that the key-bearing request produced.
        Assert.Contains("\"status\"", body, StringComparison.Ordinal);

        // POSITIVE CONTROL: a distinctive string that IS in this body, found by the same search the
        // absence assertion below uses. If this fails, the absence proves nothing.
        var control = upstreamSucceeds
            ? "Example Show"
            : "Something answered but it was not Sonarr.";
        Assert.Contains(control, body, StringComparison.OrdinalIgnoreCase);

        // ...therefore this absence is real.
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Radarr half, with the same control discipline.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_radarr_key_never_appears_in_the_response_body(bool upstreamSucceeds)
    {
        var handler = upstreamSucceeds
            ? Json(MoviesBody("Example Movie"))
            : new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var factory = await ConfiguredRadarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"status\"", body, StringComparison.Ordinal);

        var control = upstreamSucceeds
            ? "Example Movie"
            : "Something answered but it was not Radarr.";
        Assert.Contains(control, body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(RadarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // The projection's exclusions. PER RECORD.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// NO RECORD LEAKS THE OPERATOR'S FILESYSTEM LAYOUT OR AN IMAGE URL, ASSERTED PER RECORD.
    ///
    /// <para>EVERY record in this fixture carries <c>path</c>, <c>rootFolderPath</c>,
    /// <c>folderName</c> AND an <c>images</c> array whose <c>url</c> is an absolute address back at
    /// the operator's own host — which is what stops the absence assertions being vacuous. The
    /// response is then searched per PLANTED VALUE over the whole body and, separately, the record
    /// count is asserted, so an implementation that stripped the path from the first record and leaked
    /// it on the other two cannot pass by having "some clean record".</para>
    ///
    /// <para><b><c>images</c> IS THE ONE TO WATCH.</b> The path fields are obviously topology; an
    /// image URL looks like harmless presentation data and is the exclusion a well-meaning edit is
    /// most likely to undo in order to render a poster. It carries the instance's own scheme, host and
    /// port — the very address this whole surface is constructed never to return.</para>
    /// </summary>
    [Fact]
    public async Task No_series_record_leaks_a_path_or_an_image_url_into_the_response()
    {
        // MUTUALLY NON-OVERLAPPING ON PURPOSE. Realistic values (a rootFolderPath that is a PREFIX of
        // the record path) make the counts below ambiguous: a search for the root folder finds it
        // inside the record path too, so "3" and "6" stop distinguishing "every record carries it"
        // from "one field leaked". Distinct top-level segments keep each count attributable to exactly
        // one planted field, which is what makes the per-record assertion say what it claims to say.
        const string RootFolderPath = "/mnt/planted-rootfolderpath/tv";
        const string RecordPath = "/mnt/planted-recordpath/tv/Example Show";
        const string FolderName = "planted-foldername.Example.Show";
        const string ImageUrl = "http://sonarr.example:8989/planted-imageurl/MediaCover/11/poster.jpg";
        const string RemoteImageUrl = "https://artwork.example/planted-remoteimageurl/poster.jpg";

        var records = string.Join(",", Enumerable.Range(1, 3).Select(i => $$"""
            {
              "id": {{i}},
              "title": "Example Show {{i}}",
              "year": 2019,
              "tvdbId": 42424{{i}},
              "status": "continuing",
              "monitored": true,
              "network": "Example Network",
              "sizeOnDisk": 987654321.0,
              "path": "{{RecordPath}}",
              "rootFolderPath": "{{RootFolderPath}}",
              "folderName": "{{FolderName}}",
              "images": [
                { "coverType": "poster", "url": "{{ImageUrl}}", "remoteUrl": "{{RemoteImageUrl}}" }
              ],
              "statistics": { "seasonCount": 3, "episodeFileCount": 28, "episodeCount": 30 }
            }
            """));

        var upstreamBody = $"[{records}]";

        var planted = new[] { RecordPath, RootFolderPath, FolderName, ImageUrl, RemoteImageUrl };

        // DETECTABILITY CONTROL: every planted field really is in what the upstream returns, found by
        // the very searches used against the response below — and in EVERY record, so a per-record
        // leak has three chances to be caught rather than one.
        foreach (var value in planted)
        {
            Assert.Equal(3, CountOccurrences(upstreamBody, value));
        }

        using var factory = await ConfiguredSonarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        // NON-VACUITY: all three records really did arrive, so an absence below is a projection that
        // dropped the field rather than a response that carried no records at all.
        Assert.Contains("\"totalRecords\":3", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(body, "\"title\":\"Example Show "));

        // PER RECORD: zero occurrences, not "fewer than three".
        foreach (var value in planted)
        {
            Assert.Equal(0, CountOccurrences(body, value));
        }

        // And the containing key is gone too, not merely its values — so a future edit cannot serve
        // an "images": [] that a later one fills in.
        Assert.DoesNotContain("\"images\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"path\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootFolderPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folderName", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Radarr half: the same planted set over a three-movie fixture, asserted per record.</summary>
    [Fact]
    public async Task No_movie_record_leaks_a_path_or_an_image_url_into_the_response()
    {
        const string RootFolderPath = "/mnt/planted-rootfolderpath/movies";
        const string RecordPath = "/mnt/planted-recordpath/movies/Example Movie";
        const string FolderName = "planted-foldername.Example.Movie";
        const string ImageUrl = "http://radarr.example:7878/planted-imageurl/MediaCover/21/poster.jpg";
        const string RemoteImageUrl = "https://artwork.example/planted-remoteimageurl/movie-poster.jpg";

        var records = string.Join(",", Enumerable.Range(1, 3).Select(i => $$"""
            {
              "id": {{i}},
              "title": "Example Movie {{i}}",
              "year": 2021,
              "tmdbId": 51515{{i}},
              "monitored": true,
              "hasFile": true,
              "status": "released",
              "sizeOnDisk": 123456789.0,
              "path": "{{RecordPath}}",
              "rootFolderPath": "{{RootFolderPath}}",
              "folderName": "{{FolderName}}",
              "images": [
                { "coverType": "poster", "url": "{{ImageUrl}}", "remoteUrl": "{{RemoteImageUrl}}" }
              ]
            }
            """));

        var upstreamBody = $"[{records}]";

        var planted = new[] { RecordPath, RootFolderPath, FolderName, ImageUrl, RemoteImageUrl };

        foreach (var value in planted)
        {
            Assert.Equal(3, CountOccurrences(upstreamBody, value));
        }

        using var factory = await ConfiguredRadarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.RadarrMoviesRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"totalRecords\":3", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(body, "\"title\":\"Example Movie "));

        foreach (var value in planted)
        {
            Assert.Equal(0, CountOccurrences(body, value));
        }

        Assert.DoesNotContain("\"images\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"path\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootFolderPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("folderName", body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Filter, sort, page — all server-side.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// THE FILTER IS CASE-INSENSITIVE AND MATCHES MID-STRING, NOT JUST A PREFIX.
    ///
    /// <para>Both halves are asserted because both are ways the obvious implementation goes wrong: a
    /// <c>StartsWith</c> would pass a prefix-only test, and an ordinal <c>Contains</c> without the
    /// comparison constant would pass a same-case one. The fixture's titles differ in case from the
    /// query AND place the needle in the middle.</para>
    /// </summary>
    [Theory]
    [InlineData("zephyr")]
    [InlineData("ZEPHYR")]
    [InlineData("ZePhYr")]
    public async Task The_filter_matches_case_insensitively_and_mid_string(string query)
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesArray(
            "Alpha Zephyr Chronicles",
            "Beta Unrelated Show",
            "Gamma Another Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + $"?q={query}");
        var body = await response.Content.ReadAsStringAsync();

        // The needle sits MID-STRING in the only matching title, so a prefix match would find nothing.
        Assert.Contains("Alpha Zephyr Chronicles", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Beta Unrelated Show", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Gamma Another Show", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>TotalRecords</c> REFLECTS THE FILTERED COUNT, not the library size.
    ///
    /// <para>This is the assertion the mutation (b) in this bead's commit body targets: computing the
    /// total before the filter is invisible in the rendered rows — the right records still come
    /// back — and shows up only as a pager offering pages that do not exist. The fixture's two counts
    /// are deliberately different so the wrong one cannot coincide with the right one.</para>
    /// </summary>
    [Fact]
    public async Task The_total_is_the_filtered_count_and_not_the_library_size()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesArray(
            "Alpha Zephyr One",
            "Beta Zephyr Two",
            "Gamma Unrelated Three",
            "Delta Unrelated Four",
            "Epsilon Unrelated Five")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?q=zephyr");
        var body = await response.Content.ReadAsStringAsync();

        // POSITIVE CONTROL: the filter really ran and really kept the two matching rows, so the count
        // below is about the total rather than about an empty result.
        Assert.Contains("Alpha Zephyr One", body, StringComparison.Ordinal);
        Assert.Contains("Beta Zephyr Two", body, StringComparison.Ordinal);

        // TWO, not the five the library holds.
        Assert.Contains("\"totalRecords\":2", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"totalRecords\":5", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE SORT RAN, proven by a fixture whose UPSTREAM ORDER DIFFERS FROM TITLE ORDER.
    ///
    /// <para>A fixture already in alphabetical order would pass whether or not anything sorted it,
    /// which is the vacuity this deliberately avoids. The comparer is named explicitly in the
    /// implementation (<c>StringComparer.OrdinalIgnoreCase</c>) because a bare <c>OrderBy</c> on
    /// strings is culture-sensitive and would order differently across environments.</para>
    /// </summary>
    [Fact]
    public async Task The_records_come_back_sorted_by_title_regardless_of_upstream_order()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesArray(
            "Zulu Show",
            "Alpha Show",
            "Mike Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        var body = await response.Content.ReadAsStringAsync();

        var alpha = body.IndexOf("Alpha Show", StringComparison.Ordinal);
        var mike = body.IndexOf("Mike Show", StringComparison.Ordinal);
        var zulu = body.IndexOf("Zulu Show", StringComparison.Ordinal);

        // NON-VACUITY: all three really are in the response, so the ordering below is over a real set.
        Assert.True(alpha >= 0 && mike >= 0 && zulu >= 0);

        // Title order, which is the REVERSE of nothing-happened for the first and last entries: the
        // upstream sent Zulu first.
        Assert.True(alpha < mike, "Alpha Show should precede Mike Show in the sorted response.");
        Assert.True(mike < zulu, "Mike Show should precede Zulu Show in the sorted response.");
    }

    /// <summary>
    /// The page size is CLAMPED rather than rejected, and the clamp is not silent: the envelope
    /// reports the size actually served. The bounds are <c>AdminArrQueueEndpoints</c>' constants,
    /// referenced rather than re-typed, so the two surfaces cannot disagree about what
    /// <c>?pageSize=200</c> means.
    /// </summary>
    [Theory]
    [InlineData("?pageSize=1000", 100)]
    [InlineData("?pageSize=101", 100)]
    [InlineData("?pageSize=0", 1)]
    [InlineData("?pageSize=-5", 1)]
    [InlineData("", 25)]
    [InlineData("?pageSize=50", 50)]
    public async Task The_page_size_is_clamped_and_the_envelope_reports_what_was_served(
        string query,
        int expectedPageSize)
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + query);

        Assert.Contains(
            $"\"pageSize\":{expectedPageSize}",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The clamp's BOUNDS ARE THE QUEUE SURFACE'S CONSTANTS, asserted rather than assumed. Pinned by
    /// value so that a re-typed 25 or 100 on either surface — the drift the shared constants exist to
    /// prevent — fails here rather than shipping as two surfaces that disagree.
    /// </summary>
    [Fact]
    public async Task The_clamp_bounds_are_the_shared_queue_constants()
    {
        Assert.Equal(25, AdminArrQueueEndpoints.DefaultPageSize);
        Assert.Equal(100, AdminArrQueueEndpoints.MaxPageSize);

        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var defaulted = await GetAsync(client, AdminArrLibraryEndpoints.SonarrSeriesRoute);
        Assert.Contains(
            $"\"pageSize\":{AdminArrQueueEndpoints.DefaultPageSize}",
            await defaulted.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        using var overAsked = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?pageSize=100000");
        Assert.Contains(
            $"\"pageSize\":{AdminArrQueueEndpoints.MaxPageSize}",
            await overAsked.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?page=0", 1)]
    [InlineData("?page=-3", 1)]
    [InlineData("", 1)]
    [InlineData("?page=4", 4)]
    public async Task The_page_is_floored_at_one_and_otherwise_passed_through(string query, int expectedPage)
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesBody("Example Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + query);

        Assert.Contains(
            $"\"page\":{expectedPage}",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// PAGE 2 IS THE SECOND SLICE OF THE SORTED, FILTERED LIST — the assertion that ties all three
    /// stages together in the right order.
    ///
    /// <para>The fixture is shuffled upstream and contains non-matching rows, so a page 2 taken before
    /// the sort, or before the filter, lands on different records. Two per page over four matches
    /// makes each slice unambiguous.</para>
    /// </summary>
    [Fact]
    public async Task Page_two_is_the_second_slice_of_the_sorted_filtered_list()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesArray(
            "Delta Zephyr Four",
            "Alpha Zephyr One",
            "Unrelated Beta Show",
            "Charlie Zephyr Three",
            "Bravo Zephyr Two",
            "Unrelated Gamma Show")));
        using var client = factory.CreateClient();

        // Page 1: the first two of the four matches in title order.
        using var first = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?q=zephyr&page=1&pageSize=2");
        var firstBody = await first.Content.ReadAsStringAsync();

        Assert.Contains("\"totalRecords\":4", firstBody, StringComparison.Ordinal);
        Assert.Contains("Alpha Zephyr One", firstBody, StringComparison.Ordinal);
        Assert.Contains("Bravo Zephyr Two", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Charlie Zephyr Three", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Delta Zephyr Four", firstBody, StringComparison.Ordinal);

        // Page 2: the REMAINING two, and no row from page 1.
        using var second = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?q=zephyr&page=2&pageSize=2");
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Contains("\"totalRecords\":4", secondBody, StringComparison.Ordinal);
        Assert.Contains("Charlie Zephyr Three", secondBody, StringComparison.Ordinal);
        Assert.Contains("Delta Zephyr Four", secondBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Alpha Zephyr One", secondBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Bravo Zephyr Two", secondBody, StringComparison.Ordinal);

        // Neither page carried a row the filter should have removed.
        Assert.DoesNotContain("Unrelated", firstBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated", secondBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page BEYOND the range is EMPTY RECORDS WITH THE REAL TOTAL. The queue surface got this free
    /// from upstream; here it is produced deliberately by counting before the slice, and it is what
    /// lets a client detect the overshoot rather than silently being served page 1 instead.
    /// </summary>
    [Fact]
    public async Task A_page_beyond_the_range_is_empty_records_with_the_real_total()
    {
        using var factory = await ConfiguredSonarrAsync(Json(SeriesArray(
            "Alpha Show", "Bravo Show", "Charlie Show")));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrLibraryEndpoints.SonarrSeriesRoute + "?page=99&pageSize=25");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"totalRecords\":3", body, StringComparison.Ordinal);
        Assert.Contains("\"records\":[]", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>One fully-populated series, as a single-element upstream array.</summary>
    private static string SeriesBody(string title) => $$"""
        [
          {
            "id": 11,
            "title": "{{title}}",
            "year": 2019,
            "tvdbId": 424242,
            "status": "continuing",
            "monitored": true,
            "network": "Example Network",
            "sizeOnDisk": 987654321.0,
            "statistics": { "seasonCount": 3, "episodeFileCount": 28, "episodeCount": 30 }
          }
        ]
        """;

    /// <summary>One fully-populated movie, as a single-element upstream array.</summary>
    private static string MoviesBody(string title) => $$"""
        [
          {
            "id": 21,
            "title": "{{title}}",
            "year": 2021,
            "tmdbId": 515151,
            "monitored": true,
            "hasFile": true,
            "status": "released",
            "sizeOnDisk": 123456789.0
          }
        ]
        """;

    /// <summary>
    /// A series array carrying the given titles IN THE GIVEN ORDER, which is what lets the sort test
    /// hand it an order that differs from title order.
    /// </summary>
    private static string SeriesArray(params string[] titles) =>
        "[" + string.Join(",", titles.Select((title, i) => $$"""
            {
              "id": {{i + 1}},
              "title": "{{title}}",
              "year": 2019,
              "tvdbId": {{100000 + i}},
              "status": "continuing",
              "monitored": true,
              "network": "Example Network",
              "sizeOnDisk": 1000.0,
              "statistics": { "seasonCount": 1, "episodeFileCount": 1, "episodeCount": 1 }
            }
            """)) + "]";

    /// <summary>
    /// Routes a handler to whichever kind the path belongs to, so the both-kinds theories above read
    /// as one case rather than two near-identical ones.
    /// </summary>
    private static Task<WebApplicationFactoryHandle> ConfiguredAsync(string path, HttpMessageHandler handler) =>
        path == AdminArrLibraryEndpoints.SonarrSeriesRoute
            ? ConfiguredSonarrAsync(handler)
            : ConfiguredRadarrAsync(handler);

    /// <summary>
    /// A host with Sonarr configured and ONLY the registered <see cref="SonarrLibraryClient"/>'s
    /// primary handler replaced. Everything downstream of the response — the classification, the
    /// projection, the filter/sort/page, the envelope, the gate — stays the code <c>Program.cs</c>
    /// composed.
    /// </summary>
    private static async Task<WebApplicationFactoryHandle> ConfiguredSonarrAsync(HttpMessageHandler handler)
    {
        var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new ArrInstanceRepository(db).SetAsync(
                SonarrBaseUrl, SonarrKey, CancellationToken.None));

        var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHttpClient<SonarrLibraryClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler)));

        return new WebApplicationFactoryHandle(factory, configured);
    }

    private static async Task<WebApplicationFactoryHandle> ConfiguredRadarrAsync(HttpMessageHandler handler)
    {
        var factory = new ArbitarrWebApplicationFactory();
        await SeedAdminKeyAsync(factory);
        await factory.SeedAsync(async db =>
            await new RadarrInstanceRepository(db).SetAsync(
                RadarrBaseUrl, RadarrKey, CancellationToken.None));

        var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHttpClient<RadarrLibraryClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler)));

        return new WebApplicationFactoryHandle(factory, configured);
    }

    /// <summary>
    /// Keeps the base factory alive for as long as the derived one built from it. Disposing the base
    /// while a <c>WithWebHostBuilder</c> derivative is still serving tears down the config directory
    /// the derivative is using.
    /// </summary>
    private sealed class WebApplicationFactoryHandle(
        ArbitarrWebApplicationFactory owner,
        WebApplicationFactory<Program> configured) : IDisposable
    {
        public HttpClient CreateClient() => configured.CreateClient();

        public void Dispose()
        {
            configured.Dispose();
            owner.Dispose();
        }
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client.SendAsync(request);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static async Task<string?> ReadStoredValueAsync(
        ArbitarrWebApplicationFactory factory,
        string name)
    {
        string? value = null;
        await factory.SeedAsync(async db =>
            value = (await db.Settings.FindAsync(name))?.Value);
        return value;
    }

    private static async Task SeedAdminKeyAsync(ArbitarrWebApplicationFactory factory) =>
        await factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = AdminKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });

    private static StubHandler Json(string body) => new(JsonResponse(body));

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Clone(response));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    /// <summary>
    /// Records every request and answers them all with one fixed response. The COUNT is what the
    /// redirect tests' security half turns on; the URI is what the only-the-key tests assert against.
    /// </summary>
    private sealed class CountingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequestUri = request.RequestUri;
            return Task.FromResult(Clone(response));
        }
    }

    /// <summary>
    /// A fresh response per request. An <see cref="HttpResponseMessage"/> is disposed by the caller
    /// once its content is read, so handing the same instance to a second request would answer with a
    /// disposed body — and a test class whose handlers serve more than one request would fail for a
    /// reason that has nothing to do with what it asserts.
    /// </summary>
    private static HttpResponseMessage Clone(HttpResponseMessage source)
    {
        var clone = new HttpResponseMessage(source.StatusCode);

        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (source.Content is not null)
        {
            var body = source.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(
                body,
                Encoding.UTF8,
                source.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }
}
