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
/// arb-6l9b.3: <c>GET /api/admin/arr/sonarr/queue</c> and <c>GET /api/admin/arr/radarr/queue</c>
/// end to end against the real Host, with only the upstream TRANSPORT replaced.
///
/// <para><b>WHAT IS REAL HERE AND WHY THAT MATTERS.</b> Each test replaces the primary handler of one
/// registered typed client and nothing else — the credential lookup, the status classification, the
/// projection, the envelope and the route's admin gate all remain the code <c>Program.cs</c>
/// composed. That distinction is the reason #57's webhook leak shipped green: a test that drives a
/// path bypassing the real composition proves only that the code it hand-wired works.</para>
///
/// <para><b>EVERY ABSENCE ASSERTION HERE CARRIES A POSITIVE CONTROL (CLAUDE.md §4).</b>
/// <c>Assert.DoesNotContain(secret, body)</c> passes just as happily when the secret was never in
/// play — an empty set contains nothing, and three leaks have shipped in this repository behind
/// exactly that shape. So each test below first demonstrates that the planted value WOULD be found by
/// the same search over the same body (the upstream fixture really carries it), and only then asserts
/// the real response carries none of it. Asserting merely that the fixture was created proves the
/// value EXISTS; it does not prove it would be DETECTABLE if it leaked.</para>
///
/// <para>All addresses are documentation forms (<c>sonarr.example:8989</c>,
/// <c>radarr.example:7878</c>) and every credential-shaped string carries the <c>placeholder-</c>
/// prefix: no real address or secret enters committed content.</para>
/// </summary>
public sealed class AdminArrQueueEndpointsTests
{
    private const string AdminKey = "the-real-admin-key";

    /// <summary>
    /// Distinctive enough that a substring search over a whole response body cannot match it by
    /// accident, which is what makes the "it is not in here" assertions meaningful.
    /// </summary>
    private const string SonarrKey = "placeholder-sonarr-key-4e7a91d3";

    private const string RadarrKey = "placeholder-radarr-key-5f8b02e4";

    private const string SonarrBaseUrl = "http://sonarr.example:8989";
    private const string RadarrBaseUrl = "http://radarr.example:7878";

    /// <summary>
    /// A marker planted in the UPSTREAM body. The fixed-message tests assert it is absent from the
    /// whole response, which is what proves <c>DescribeStatus</c> chose its wording from the status
    /// alone rather than interpolating anything the upstream said.
    /// </summary>
    private const string UpstreamMarker = "UPSTREAM-BODY-MARKER-9f31c7";

    // ---------------------------------------------------------------------------------------------
    // Gating, by name. The sweep in AdminApiKeyRouteEnumerationTests covers these routes generically
    // (both are concrete), but that coverage is a property of the route SHAPE rather than of this
    // feature. Pinned here so neither route is gated only by assumption.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Both queue routes refuse a request carrying no admin key.
    ///
    /// <para>The request deliberately sends NO body, for the same reason
    /// <c>AdminApiKeyRouteEnumerationTests</c>' sweep does: a required body is model-bound BEFORE the
    /// endpoint filter and would short-circuit to 400 without the gate running, letting an
    /// unauthenticated caller tell a malformed body from a well-formed one and enumerate which admin
    /// routes exist. These routes take no body at all, which is what keeps the gate strictly
    /// first. Do not "fix" the bodilessness.</para>
    /// </summary>
    [Theory]
    [InlineData(AdminArrQueueEndpoints.SonarrQueueRoute)]
    [InlineData(AdminArrQueueEndpoints.RadarrQueueRoute)]
    public async Task Every_queue_route_rejects_a_request_without_the_admin_key(string path)
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
    [InlineData(AdminArrQueueEndpoints.SonarrQueueRoute)]
    [InlineData(AdminArrQueueEndpoints.RadarrQueueRoute)]
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
    // Status branches. One assertion per case.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// NOT CONFIGURED, HALF ONE: nothing is stored at all. The top level is still 200 — the admin
    /// call succeeded and is reporting faithfully what it found — and the verdict is in the envelope.
    /// </summary>
    [Theory]
    [InlineData(AdminArrQueueEndpoints.SonarrQueueRoute)]
    [InlineData(AdminArrQueueEndpoints.RadarrQueueRoute)]
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
    public async Task An_address_with_no_key_is_NotConfigured_rather_than_AuthenticationFailed()
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
        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.NotConfigured)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_queue_document_is_Ok_and_the_records_reach_the_response()
    {
        using var factory = await ConfiguredSonarrAsync(Json(QueueBody(totalRecords: 1)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("Example.Show.S01E01.1080p.WEB-DL", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_instance_is_Unreachable()
    {
        using var factory = await ConfiguredSonarrAsync(
            new ThrowingHandler(new HttpRequestException(
                "connection refused",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.Unreachable)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_401_from_the_instance_is_AuthenticationFailed()
    {
        using var factory = await ConfiguredSonarrAsync(
            new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.AuthenticationFailed)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_JSON_body_is_UnexpectedResponse()
    {
        using var factory = await ConfiguredSonarrAsync(
            Json("<html><body>Please sign in</body></html>"));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_500_from_the_instance_is_UnexpectedResponse()
    {
        using var factory = await ConfiguredSonarrAsync(
            new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
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
    public async Task A_redirecting_instance_is_UnexpectedResponse_and_no_second_request_is_issued()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://elsewhere.example/api/v3/queue");

        var handler = new CountingHandler(redirect);
        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);

        Assert.Contains(
            $"\"status\":\"{nameof(ArrSectionStatus.UnexpectedResponse)}\"",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // The redirect was not followed: exactly one request left this process, and it went to the
        // configured address rather than to the one the redirect named.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("sonarr.example", handler.LastRequestUri!.Host);
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
    /// rather than shipping. The marker in the fixture is the positive control: without it the
    /// absence assertion would pass against any response at all.</para>
    ///
    /// <para>Run against the UnexpectedResponse branch specifically because that is the branch a
    /// well-meaning edit is most likely to make chatty — it is the one where the upstream body is the
    /// only thing that could explain what actually answered.</para>
    /// </summary>
    [Fact]
    public async Task The_message_is_fixed_wording_and_never_carries_the_upstream_body()
    {
        var upstreamBody = $$"""{"error":"{{UpstreamMarker}} - this service is not an arr"}""";

        // DETECTABILITY CONTROL: the marker really is in what the upstream returns, found by the very
        // search used against the response below.
        Assert.Contains(UpstreamMarker, upstreamBody, StringComparison.Ordinal);

        using var factory = await ConfiguredSonarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);
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

    /// <summary>
    /// THE KEY NEVER REACHES THE RESPONSE BODY, WITH A POSITIVE CONTROL.
    ///
    /// <para><b>THE CONTROL IS A DISTINCTIVE STRING THAT IS GENUINELY IN THE REAL BODY</b>, following
    /// <c>AdminArrEndpointsTests.The_stored_key_never_comes_back_on_the_read</c>: finding it proves
    /// this search, over THIS body, actually finds things that are there. Only then does the key's
    /// absence mean the key is not there, rather than meaning the body was empty, the request 404'd,
    /// or the search was looking at the wrong response.</para>
    ///
    /// <para>The control differs per branch because the two branches carry different content, and it
    /// has to be something the body really holds. On the Ok branch it is the fixture's queue title,
    /// which travelled the whole projection path. On the failure branch there are no records at all,
    /// so it is the fixed wording for that status — still a distinctive string, still genuinely in
    /// the response, and still produced by the request under test.</para>
    ///
    /// <para>Driven against the Ok branch AND a failure branch, because the failure paths are where an
    /// implementation is most tempted to explain itself with the request it made.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_sonarr_key_never_appears_in_the_response_body(bool upstreamSucceeds)
    {
        var handler = upstreamSucceeds
            ? Json(QueueBody(totalRecords: 1))
            : new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);
        var body = await response.Content.ReadAsStringAsync();

        // NON-VACUITY: the response is a real envelope that the key-bearing request produced.
        Assert.Contains("\"status\"", body, StringComparison.Ordinal);

        // POSITIVE CONTROL: a distinctive string that IS in this body, found by the same search the
        // absence assertion below uses. If this fails, the absence proves nothing.
        var control = upstreamSucceeds
            ? "Example.Show.S01E01.1080p.WEB-DL"
            : "Something answered but it was not Sonarr.";
        Assert.Contains(control, body, StringComparison.OrdinalIgnoreCase);

        // ...therefore this absence is real.
        Assert.DoesNotContain(SonarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Radarr half of the assertion above, with the same control discipline: the fixture's queue
    /// title is a distinctive string genuinely present in the response, so the key's absence beside it
    /// is a real absence rather than a search that never matches.
    /// </summary>
    [Fact]
    public async Task The_radarr_key_never_appears_in_the_response_body()
    {
        using var factory = await ConfiguredRadarrAsync(Json(QueueBody(totalRecords: 1)));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.RadarrQueueRoute);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"status\"", body, StringComparison.Ordinal);

        // POSITIVE CONTROL: really in this body, found by the same search used below.
        Assert.Contains("Example.Show.S01E01.1080p.WEB-DL", body, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(RadarrKey, body, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // The projection.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// NO RECORD CARRIES THE OPERATOR'S FILESYSTEM LAYOUT, ASSERTED PER RECORD.
    ///
    /// <para>EVERY record in this fixture carries <c>outputPath</c>, <c>path</c>,
    /// <c>rootFolderPath</c> and <c>folderName</c>, which is what stops the absence assertions being
    /// vacuous. The response is then searched per PLANTED VALUE over the whole body and, separately,
    /// the record count is asserted — so an implementation that stripped the path from the first
    /// record and leaked it on the other two cannot pass by having "some clean record".</para>
    /// </summary>
    [Fact]
    public async Task No_record_leaks_a_filesystem_path_into_the_response()
    {
        // MUTUALLY NON-OVERLAPPING ON PURPOSE. The realistic values (a rootFolderPath that is a
        // PREFIX of the record path) make the counts below ambiguous: a search for the root folder
        // finds it inside the record path too, so "3" and "6" stop distinguishing "every record
        // carries it" from "one field leaked". Distinct top-level segments keep each count
        // attributable to exactly one planted field, which is what makes the per-record assertion
        // say what it claims to say.
        const string OutputPath = "/mnt/planted-outputpath/downloads/complete/example-show";
        const string RootFolderPath = "/mnt/planted-rootfolderpath/tv";
        const string RecordPath = "/mnt/planted-recordpath/tv/Example Show/Season 01";
        const string FolderName = "planted-foldername.Example.Show.S01E01.WEB-DL-GROUP";

        var records = string.Join(",", Enumerable.Range(1, 3).Select(i => $$"""
            {
              "title": "Example.Show.S01E0{{i}}.1080p.WEB-DL",
              "status": "downloading",
              "size": 100.0,
              "sizeleft": 50.0,
              "protocol": "usenet",
              "downloadClient": "SABnzbd",
              "indexer": "Example Indexer",
              "outputPath": "{{OutputPath}}",
              "path": "{{RecordPath}}",
              "rootFolderPath": "{{RootFolderPath}}",
              "folderName": "{{FolderName}}"
            }
            """));

        var upstreamBody = $$"""{"page":1,"pageSize":25,"totalRecords":3,"records":[{{records}}]}""";

        var planted = new[] { OutputPath, RecordPath, RootFolderPath, FolderName };

        // DETECTABILITY CONTROL: every planted field really is in what the upstream returns, found by
        // the very searches used against the response below — and in EVERY record, so a per-record
        // leak has three chances to be caught rather than one.
        foreach (var value in planted)
        {
            Assert.Equal(3, CountOccurrences(upstreamBody, value));
        }

        using var factory = await ConfiguredSonarrAsync(Json(upstreamBody));
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute);
        var body = await response.Content.ReadAsStringAsync();

        // NON-VACUITY: all three records really did arrive, so an absence below is a projection that
        // dropped the field rather than a response that carried no records at all.
        Assert.Contains($"\"totalRecords\":3", body, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(body, "\"title\":\"Example.Show.S01E0"));

        // PER RECORD: zero occurrences, not "fewer than three". A count assertion is what makes this
        // per-record rather than "some record is clean".
        foreach (var value in planted)
        {
            Assert.Equal(0, CountOccurrences(body, value));
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Paging.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The page size is CLAMPED rather than rejected, and the clamp is not silent: the envelope
    /// reports the size actually served. See <c>AdminArrQueueEndpoints.ClampPageSize</c> for why this
    /// departs from the settings surface's reject-never-clamp posture.
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
        var handler = new CountingHandler(JsonResponse(QueueBody(totalRecords: 1)));
        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute + query);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"pageSize\":{expectedPageSize}", body, StringComparison.Ordinal);

        // The CLAMPED value is what went upstream, not the caller's raw one — otherwise the bound
        // would be cosmetic and an unbounded ask would still make this process fetch an arbitrarily
        // large document.
        Assert.Contains($"pageSize={expectedPageSize}", handler.LastRequestUri!.Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?page=0", 1)]
    [InlineData("?page=-3", 1)]
    [InlineData("", 1)]
    [InlineData("?page=4", 4)]
    public async Task The_page_is_floored_at_one_and_otherwise_passed_through(string query, int expectedPage)
    {
        var handler = new CountingHandler(JsonResponse(QueueBody(totalRecords: 1)));
        using var factory = await ConfiguredSonarrAsync(handler);
        using var client = factory.CreateClient();

        using var response = await GetAsync(client, AdminArrQueueEndpoints.SonarrQueueRoute + query);

        Assert.Contains(
            $"\"page\":{expectedPage}",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Contains($"page={expectedPage}", handler.LastRequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page BEYOND the range is not clamped: the upstream answers it with an empty records array
    /// and the real total, which is the truthful answer and lets a client detect the overshoot rather
    /// than silently being served page 1 instead.
    /// </summary>
    [Fact]
    public async Task A_page_beyond_the_range_is_empty_records_with_the_real_total()
    {
        using var factory = await ConfiguredSonarrAsync(
            Json("""{"page":99,"pageSize":25,"totalRecords":2,"records":[]}"""));
        using var client = factory.CreateClient();

        using var response = await GetAsync(
            client, AdminArrQueueEndpoints.SonarrQueueRoute + "?page=99");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"\"status\":\"{nameof(ArrSectionStatus.Ok)}\"", body, StringComparison.Ordinal);
        Assert.Contains("\"totalRecords\":2", body, StringComparison.Ordinal);
        Assert.Contains("\"records\":[]", body, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static string QueueBody(int totalRecords) => $$"""
        {
          "page": 1,
          "pageSize": 25,
          "totalRecords": {{totalRecords}},
          "records": [
            {
              "title": "Example.Show.S01E01.1080p.WEB-DL",
              "status": "downloading",
              "trackedDownloadStatus": "ok",
              "trackedDownloadState": "downloading",
              "size": 1234567890.0,
              "sizeleft": 234567890.0,
              "timeleft": "00:12:34",
              "protocol": "usenet",
              "downloadClient": "SABnzbd",
              "indexer": "Example Indexer",
              "statusMessages": []
            }
          ]
        }
        """;

    /// <summary>
    /// A host with Sonarr configured and ONLY the registered <see cref="SonarrQueueClient"/>'s
    /// primary handler replaced. Everything downstream of the response — the classification, the
    /// projection, the envelope, the gate — stays the code <c>Program.cs</c> composed.
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
                services.AddHttpClient<SonarrQueueClient>()
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
                services.AddHttpClient<RadarrQueueClient>()
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
    /// redirect test's security half turns on; the URI is what the paging tests assert the clamped
    /// values against.
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
