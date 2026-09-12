using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Arbitarr.Core.Media;

namespace Arbitarr.Core.Tests;

/// <summary>
/// arb-6l9b.3: the *arr queue reader must classify every upstream outcome into the closed
/// <see cref="ArrSectionStatus"/>, and must project the upstream record rather than pass it through.
///
/// <para><b>THE TWO LOAD-BEARING TESTS HERE ARE THE PROJECTION ONE AND THE REDIRECT ONE.</b> The
/// projection test plants <c>outputPath</c> / <c>rootFolderPath</c> / <c>path</c> / <c>folderName</c>
/// in EVERY record of a multi-record fixture and asserts per record that none of them reaches the
/// projected item — a fixture in which the fields were absent would make that assertion vacuous, and
/// "some record is clean" would pass an implementation that leaked on every other one (CLAUDE.md §4).
/// The redirect test asserts BOTH that a 302 is classified and that exactly ONE request was issued,
/// because the SSRF property the <c>AllowAutoRedirect = false</c> registration exists for is about
/// the second request never happening, which a status assertion alone would not notice.</para>
///
/// <para>Addresses are documentation forms (<c>sonarr.example</c>, <c>radarr.example</c>) and every
/// credential-shaped string carries the <c>placeholder-</c> prefix.</para>
/// </summary>
public class ArrQueueReaderTests
{
    private static readonly Uri SonarrBaseUrl = new("http://sonarr.example:8989");
    private static readonly Uri RadarrBaseUrl = new("http://radarr.example:7878");

    private const string ApiKey = "placeholder-sonarr-key-3d91f7c2";

    private const string QueueBody = """
        {
          "page": 1,
          "pageSize": 25,
          "totalRecords": 2,
          "records": [
            {
              "title": "Example.Show.S01E01.1080p.WEB-DL",
              "status": "downloading",
              "trackedDownloadStatus": "ok",
              "trackedDownloadState": "downloading",
              "size": 1234567890.0,
              "sizeleft": 234567890.0,
              "timeleft": "00:12:34",
              "estimatedCompletionTime": "2026-09-12T10:30:00Z",
              "protocol": "usenet",
              "downloadClient": "SABnzbd",
              "indexer": "Example Indexer",
              "errorMessage": null,
              "statusMessages": []
            },
            {
              "title": "Example.Show.S01E02.1080p.WEB-DL",
              "status": "completed",
              "trackedDownloadStatus": "warning",
              "trackedDownloadState": "importPending",
              "size": 999.0,
              "sizeleft": 0.0,
              "timeleft": null,
              "estimatedCompletionTime": null,
              "protocol": "torrent",
              "downloadClient": "qBittorrent",
              "indexer": "Other Indexer",
              "errorMessage": "One file was not imported",
              "statusMessages": [
                { "title": "Example.Show.S01E02", "messages": ["Not an upgrade for existing episode file"] }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task A_queue_document_is_Ok_and_carries_the_upstream_total()
    {
        var reader = ReaderReturning(Json(QueueBody));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 1, pageSize: 25);

        Assert.Equal(ArrSectionStatus.Ok, result.Status);
        Assert.Equal(2, result.TotalRecords);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public async Task The_projection_carries_the_fields_an_operator_watches_a_download_by()
    {
        var reader = ReaderReturning(Json(QueueBody));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 1, pageSize: 25);

        var first = result.Items[0];
        Assert.Equal("Example.Show.S01E01.1080p.WEB-DL", first.Title);
        Assert.Equal("downloading", first.Status);
        Assert.Equal("ok", first.TrackedDownloadStatus);
        Assert.Equal("downloading", first.TrackedDownloadState);
        Assert.Equal(1234567890L, first.Size);
        Assert.Equal(234567890L, first.SizeLeft);
        Assert.Equal("00:12:34", first.TimeLeft);
        Assert.Equal(DateTimeOffset.Parse("2026-09-12T10:30:00Z"), first.EstimatedCompletionTime);
        Assert.Equal("usenet", first.Protocol);
        Assert.Equal("SABnzbd", first.DownloadClient);
        Assert.Equal("Example Indexer", first.Indexer);
    }

    /// <summary>
    /// The upstream's nested <c>{title, messages[]}</c> pairs are flattened to the lines an operator
    /// reads, and the error message is carried — it is *arr-authored operator-facing text, not an
    /// exception or a body Arbitarr invented.
    /// </summary>
    [Fact]
    public async Task Status_messages_are_flattened_and_the_error_message_is_carried()
    {
        var reader = ReaderReturning(Json(QueueBody));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 1, pageSize: 25);

        var second = result.Items[1];
        Assert.Equal("One file was not imported", second.ErrorMessage);
        Assert.Equal(["Not an upgrade for existing episode file"], second.StatusMessages);
    }

    /// <summary>
    /// THE PROJECTION EXCLUDES THE OPERATOR'S FILESYSTEM LAYOUT, ASSERTED PER RECORD.
    ///
    /// <para>Every record in this fixture carries <c>outputPath</c>, <c>path</c>,
    /// <c>rootFolderPath</c> AND <c>folderName</c>, which is what stops the absence assertions being
    /// vacuous: an empty set contains nothing, and "the path is not in the output" passes just as
    /// happily when no path was ever in the input (CLAUDE.md §4). The loop asserts per RECORD rather
    /// than "some record is clean", because an implementation that projected the path on every record
    /// but the first would pass the weaker check.</para>
    ///
    /// <para>Asserted against the rendered item rather than a field list, so a future member named
    /// something other than <c>Path</c> that nonetheless carried one would still be caught.</para>
    /// </summary>
    [Fact]
    public async Task No_record_carries_a_filesystem_path_through_the_projection()
    {
        // Mutually non-overlapping, so a failure names exactly which planted field leaked rather
        // than matching on a shared prefix.
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

        var body = $$"""{"page":1,"pageSize":25,"totalRecords":3,"records":[{{records}}]}""";

        // DETECTABILITY CONTROL: every planted field really is in what the upstream "returns", found
        // by the very searches used against the projection below. Without this the per-record
        // assertions would pass against a fixture that never contained them.
        foreach (var planted in new[] { OutputPath, RecordPath, RootFolderPath, FolderName })
        {
            Assert.Contains(planted, body, StringComparison.Ordinal);
        }

        var reader = ReaderReturning(Json(body));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 1, pageSize: 25);

        Assert.Equal(ArrSectionStatus.Ok, result.Status);

        // Non-vacuity: the records really did arrive, so the loop below iterates.
        Assert.Equal(3, result.Items.Count);

        foreach (var item in result.Items)
        {
            var rendered = System.Text.Json.JsonSerializer.Serialize(item);

            foreach (var planted in new[] { OutputPath, RecordPath, RootFolderPath, FolderName })
            {
                Assert.DoesNotContain(planted, rendered, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_is_AuthenticationFailed_rather_than_UnexpectedResponse(HttpStatusCode status)
    {
        // Checked BEFORE the success check, so a wrong key is never reported as "something else
        // answered": the instance is there and healthy and only the credential is wrong, which is a
        // different fix.
        var reader = ReaderReturning(new HttpResponseMessage(status));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, "placeholder-sonarr-key-wrong", 1, 25);

        Assert.Equal(ArrSectionStatus.AuthenticationFailed, result.Status);
    }

    [Fact]
    public async Task A_server_error_is_UnexpectedResponse_rather_than_Unreachable()
    {
        // A 500 means something answered: the address is right, so "unreachable" would mislead.
        var reader = ReaderReturning(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.UnexpectedResponse, result.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><body>Please sign in</body></html>")]
    [InlineData("""["an","array"]""")]
    [InlineData("""{"unrelated":"document"}""")]
    public async Task A_body_that_is_not_a_queue_document_is_UnexpectedResponse(string body)
    {
        // 200 OK carrying something else: the classic "base URL points at a reverse proxy or the
        // wrong service" case. Recognised on the SHAPE (a records array) rather than on a value,
        // matching the connectivity probers — and note a genuinely empty queue sends "records": [],
        // which is a present-but-empty list and stays Ok.
        var reader = ReaderReturning(Json(body));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.UnexpectedResponse, result.Status);
    }

    [Fact]
    public async Task An_empty_queue_is_Ok_rather_than_UnexpectedResponse()
    {
        // The distinction the shape check above turns on: nothing downloading is a healthy answer,
        // not an unrecognised document.
        var reader = ReaderReturning(Json("""{"page":1,"pageSize":25,"totalRecords":0,"records":[]}"""));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.Ok, result.Status);
        Assert.Equal(0, result.TotalRecords);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task A_socket_failure_is_Unreachable()
    {
        var reader = ReaderThrowing(new HttpRequestException(
            "connection refused",
            new SocketException((int)SocketError.ConnectionRefused)));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.Unreachable, result.Status);
    }

    /// <summary>
    /// A TLS failure is <see cref="ArrSectionStatus.Unreachable"/> here rather than a status of its
    /// own, which is the deliberate difference from the connectivity probers. Pinned so the omission
    /// reads as a decision: the queue surface's answer to a failed handshake is "the queue could not
    /// be read", and the section that tells TLS apart from a refused connection — because that is the
    /// question it exists to answer — is the probe, which already has a <c>TlsFailure</c> outcome and
    /// a button to produce it.
    /// </summary>
    [Fact]
    public async Task A_TLS_failure_is_Unreachable_because_the_probe_is_where_that_distinction_lives()
    {
        var reader = ReaderThrowing(new HttpRequestException(
            "The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid.")));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task The_read_times_out_rather_than_hanging_the_UI()
    {
        var reader = new SonarrQueueClient(
            new HttpClient(new NeverRespondingHandler()),
            timeout: TimeSpan.FromMilliseconds(150));

        var result = await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Caller_cancellation_is_rethrown_rather_than_reported_as_an_arr_failure()
    {
        // An aborted request (operator navigated away, host shutting down) says nothing about the
        // *arr, so it must not be recorded as a verdict against it.
        var reader = new SonarrQueueClient(new HttpClient(new NeverRespondingHandler()));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, 1, 25, cancelled.Token));
    }

    /// <summary>
    /// A REDIRECTING UPSTREAM IS CLASSIFIED, AND NO SECOND REQUEST IS ISSUED.
    ///
    /// <para>UnexpectedResponse rather than Unreachable, because something DID answer and the
    /// operator's fix is the base URL (a proxy, a login redirect, the wrong service) rather than the
    /// host being down. The 302 arrives as a non-success status only because the registered client
    /// sets <c>AllowAutoRedirect = false</c>; this test constructs its handler the same way so it
    /// exercises the same shape.</para>
    ///
    /// <para><b>The request-count assertion is the security half.</b> The SSRF property that
    /// registration exists for is that a key-bearing request is never REISSUED at a host nobody
    /// configured — a property about the second request not happening, which an assertion on the
    /// returned status alone would not notice at all.</para>
    /// </summary>
    [Fact]
    public async Task A_redirecting_upstream_is_UnexpectedResponse_and_only_one_request_is_issued()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("http://elsewhere.example/api/v3/queue");

        // A custom primary handler, so redirect-following (which lives in HttpClientHandler /
        // SocketsHttpHandler, not in HttpClient) never happens here either — the same shape the
        // registration produces by setting AllowAutoRedirect = false on its own primary handler.
        var handler = new CountingHandler(redirect);
        var reader = new RadarrQueueClient(new HttpClient(handler, disposeHandler: false));

        var result = await reader.ReadQueueAsync(RadarrBaseUrl, ApiKey, 1, 25);

        Assert.Equal(ArrSectionStatus.UnexpectedResponse, result.Status);

        // The redirect was NOT followed: exactly one request left this process, and it went to the
        // configured address rather than to the one the redirect named.
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("radarr.example", handler.LastRequest!.RequestUri!.Host);
    }

    /// <summary>
    /// THE KEY IS SENT UPSTREAM, AND IN THE QUERY STRING.
    ///
    /// <para>The first half is what makes the read authenticated at all. The second is what the "no
    /// <c>.RemoveAllLoggers()</c>" decision on these clients' registrations rests on — the query
    /// string is where <c>IHttpClientFactory</c>'s <c>?*</c> collapse fires and where
    /// <c>LogMessageCleanser</c> scrubs, and neither covers a PATH segment (CLAUDE.md §1). Asserted
    /// against the URI the reader actually issued rather than left to the registration's comment.</para>
    /// </summary>
    [Fact]
    public async Task The_key_is_sent_in_the_query_string_and_never_in_the_path_or_a_header()
    {
        var handler = new CountingHandler(Json(QueueBody));
        var reader = new SonarrQueueClient(new HttpClient(handler, disposeHandler: false));

        await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 1, pageSize: 25);

        var uri = handler.LastRequest!.RequestUri!;

        // It authenticates at all...
        Assert.Contains($"apikey={ApiKey}", uri.Query, StringComparison.Ordinal);
        // ...against the queue endpoint...
        Assert.Equal("/api/v3/queue", uri.AbsolutePath);
        // ...and the key is NOT in the path, which is the placement neither log layer covers.
        Assert.DoesNotContain(ApiKey, uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);

        // Nor in a header: a second placement would split the codebase's one convention and
        // invalidate the registration comment SonarrKeyIsScrubbedFromLogsTests pins.
        Assert.False(handler.LastRequest.Headers.Contains("X-Api-Key"));
    }

    /// <summary>
    /// Paging is passed STRAIGHT THROUGH to the upstream, which pages this endpoint itself — rather
    /// than fetching everything and slicing locally, which is what the unpaged library endpoints will
    /// have to do.
    /// </summary>
    [Fact]
    public async Task The_paging_parameters_are_passed_through_to_the_upstream()
    {
        var handler = new CountingHandler(Json(QueueBody));
        var reader = new SonarrQueueClient(new HttpClient(handler, disposeHandler: false));

        await reader.ReadQueueAsync(SonarrBaseUrl, ApiKey, page: 3, pageSize: 50);

        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.Contains("page=3", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A base path is preserved, so an *arr behind a reverse proxy at a sub-path is read at the right
    /// place rather than at the proxy's root — matching the connectivity probers' handling.
    /// </summary>
    [Fact]
    public async Task A_base_path_is_preserved_so_a_proxied_instance_is_read_at_the_right_place()
    {
        var handler = new CountingHandler(Json(QueueBody));
        var reader = new RadarrQueueClient(new HttpClient(handler, disposeHandler: false));

        await reader.ReadQueueAsync(new Uri("http://radarr.example:7878/radarr/"), ApiKey, 1, 25);

        Assert.Equal("/radarr/api/v3/queue", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static SonarrQueueClient ReaderReturning(HttpResponseMessage response) =>
        new(new HttpClient(new StubHandler(response)));

    private static SonarrQueueClient ReaderThrowing(Exception exception) =>
        new(new HttpClient(new ThrowingHandler(exception)));

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    /// <summary>
    /// Records every request and answers them all with one fixed response. The COUNT is what the
    /// redirect test's security half turns on — a handler that only remembered the last request
    /// could not distinguish one request from two.
    /// </summary>
    private sealed class CountingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
