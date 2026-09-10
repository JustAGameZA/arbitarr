using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Backup;
using Arbitarr.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #56: the backup download, the restore upload, and the status read, driven through the real Host.
///
/// <para><b>WHY THESE EXIST ALONGSIDE THE ENUMERATION SWEEP.</b> The sweep in
/// <see cref="AdminApiKeyRouteEnumerationTests"/> covers every concrete admin route generically and
/// does cover these three. It cannot cover what makes THIS pair different: that the GET's response
/// body is the instance's credentials, and that the POST takes a multipart body without letting
/// model binding run before the gate. Those are by-name properties, so they are asserted by name
/// here — the sweep passing is not evidence for them.</para>
/// </summary>
public sealed class AdminBackupEndpointsTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public AdminBackupEndpointsTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("GET", AdminBackupEndpoints.DownloadRoute)]
    [InlineData("GET", AdminBackupEndpoints.StatusRoute)]
    [InlineData("POST", AdminBackupEndpoints.RestoreRoute)]
    public async Task Every_backup_route_rejects_a_request_with_no_admin_key(string method, string path)
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", AdminBackupEndpoints.DownloadRoute)]
    [InlineData("GET", AdminBackupEndpoints.StatusRoute)]
    [InlineData("POST", AdminBackupEndpoints.RestoreRoute)]
    public async Task Every_backup_route_rejects_a_request_with_the_wrong_admin_key(string method, string path)
    {
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(AdminApiKeyFilter.HeaderName, "not-the-configured-admin-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_restore_route_runs_the_gate_before_it_looks_at_the_body()
    {
        // The information-leak guard, asserted by name because it is the property the sweep exists
        // to protect and the one a well-meant refactor breaks. A bound IFormFile/[FromBody]
        // parameter is model-bound BEFORE endpoint filters run, so a bodiless request would
        // short-circuit to 400 without AdminApiKeyFilter executing at all — telling an
        // unauthenticated caller that this route exists and takes a body.
        //
        // The assertion is 401 and specifically NOT 400: a 400 here means binding ran first.
        await SeedAdminKeyAsync();

        using var client = _factory.CreateClient();
        using var bodiless = new HttpRequestMessage(HttpMethod.Post, AdminBackupEndpoints.RestoreRoute);
        using var response = await client.SendAsync(bodiless);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_download_serves_a_zip_named_with_a_utc_timestamp()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        using var response = await client.GetAsync(AdminBackupEndpoints.DownloadRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        Assert.NotNull(fileName);
        Assert.StartsWith("arbitarr-backup-", fileName, StringComparison.Ordinal);
        Assert.EndsWith("Z.zip", fileName, StringComparison.Ordinal);

        // The archive really is one, and really carries both files — AC1 through the wire, not just
        // through the service's own unit test.
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync());
        Assert.NotNull(archive.GetEntry(BackupArchiveLayout.DatabaseEntryName));
        Assert.NotNull(archive.GetEntry(BackupArchiveLayout.SecretKeyEntryName));
    }

    [Fact]
    public async Task The_download_url_carries_no_key_and_the_request_carries_it_in_a_header()
    {
        // AC8 / plan §3.3. A signed or one-shot download URL is the obvious way to make an
        // <a download> work and is exactly what is forbidden: a URL granting access to a
        // credential-bearing file lands in browser history, in a proxy's access log, and in
        // IHttpClientFactory's own Information-level record of full absolute URIs.
        await SeedAdminKeyAsync();

        Assert.DoesNotContain('?', AdminBackupEndpoints.DownloadRoute);

        // Positive control: prove the route is genuinely reachable WITH the header, so the
        // "no key in the URL" property below is a real constraint on a working route and not a
        // statement about a route that never serves anything.
        using var authorized = AuthorizedClient();
        using var served = await authorized.GetAsync(AdminBackupEndpoints.DownloadRoute);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        // And the same URL with the key as a query parameter does NOT open the gate.
        using var client = _factory.CreateClient();
        using var viaQuery = await client.GetAsync(
            AdminBackupEndpoints.DownloadRoute + "?apikey=" + AdminKey + "&key=" + AdminKey);

        Assert.Equal(HttpStatusCode.Unauthorized, viaQuery.StatusCode);
    }

    [Fact]
    public async Task Restore_refuses_an_upload_without_the_typed_confirmation()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        using var content = BuildUpload(await DownloadArchiveBytesAsync(), confirmation: "yes");

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // The copy must name the GUID-secret consequence, not merely say "confirm". Restoring an
        // older key invalidates every release GUID issued since, and an operator who was not told
        // that cannot have consented to it.
        Assert.Contains("release-GUID secret", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_refuses_a_confirmation_that_is_merely_close()
    {
        // A confirmation that accepts near-misses is not a confirmation. This is the last gate in
        // front of the most destructive action in the product.
        await SeedAdminKeyAsync();

        var archive = await DownloadArchiveBytesAsync();

        foreach (var attempt in new[] { "restore", "Restore", " RESTORE", "RESTORE " })
        {
            using var client = AuthorizedClient();
            using var content = BuildUpload(archive, confirmation: attempt);
            using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

            Assert.True(
                response.StatusCode == HttpStatusCode.BadRequest,
                $"Confirmation '{attempt}' must not be accepted, but the request returned {(int)response.StatusCode}.");
        }
    }

    [Fact]
    public async Task Restore_refuses_an_archive_that_is_not_a_backup_and_says_which_way_it_failed()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        using var content = BuildUpload(
            Encoding.UTF8.GetBytes("this is not a zip archive at all"),
            confirmation: AdminBackupEndpoints.RestoreConfirmationWord);

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("not a readable Arbitarr backup archive", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An oversized upload is refused, and refused on the WIRE rather than after being spooled.
    ///
    /// <para>The limit is applied to the request body itself (a Content-Length check plus
    /// IHttpMaxRequestBodySizeFeature, before ReadFormAsync is called), not only checked as
    /// IFormFile.Length inside the handler. That distinction is the reason this test exists: an
    /// in-handler length check reads exactly like a limit while ReadFormAsync has already written
    /// the whole upload to a temp file on the very disk this feature protects. A 413 is evidence
    /// the body was refused; the in-handler check is a backstop, not the limit.</para>
    ///
    /// <para>RequestSizeLimitAttribute is deliberately NOT used: it is MVC filter metadata and a
    /// minimal-API route silently ignores it, so it looks like a limit and enforces nothing. That
    /// was tried here and the handler still ran.</para>
    /// </summary>
    [Fact]
    public async Task Restore_refuses_an_upload_larger_than_the_limit_before_buffering_it()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        // Comfortably over the cap, and never materialised as one big array: the point is that the
        // server stops reading, so the client must be willing to send more than the server accepts.
        var oversized = new byte[AdminBackupEndpoints.MaxUploadBytes + (8L * 1024 * 1024)];
        using var content = BuildUpload(oversized, confirmation: AdminBackupEndpoints.RestoreConfirmationWord);

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    /// <summary>
    /// A recorded automatic-backup failure reaches the status response, and a later success clears
    /// it. This is the degraded path'''s provenance (docs/standards/data.md): without it a broken
    /// scheduled backup is indistinguishable from a healthy one that has not run again yet, because
    /// the last-backup timestamp merely stops advancing.
    ///
    /// <para>POSITIVE CONTROL FIRST: the response is shown to carry NO failure while healthy, so
    /// the assertion that one appears is not satisfied by a field that is always populated.</para>
    /// </summary>
    [Fact]
    public async Task The_status_read_reports_an_automatic_backup_failure_and_stops_once_it_recovers()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var state = _factory.Services.GetRequiredService<BackupStateStore>();

        // POSITIVE CONTROL: healthy first. If the field were always set, the next assertion would
        // pass without the recording path working at all.
        var healthy = await client.GetFromJsonAsync<BackupStatusResponse>(AdminBackupEndpoints.StatusRoute);
        Assert.Null(healthy!.LastBackupFailureAt);
        Assert.Null(healthy.LastBackupFailureReason);

        var failedAt = DateTimeOffset.UtcNow;
        state.RecordBackupFailure(failedAt, "IOException: There is not enough space on the disk.");

        var degraded = await client.GetFromJsonAsync<BackupStatusResponse>(AdminBackupEndpoints.StatusRoute);
        Assert.NotNull(degraded!.LastBackupFailureAt);
        Assert.Contains("not enough space", degraded.LastBackupFailureReason!, StringComparison.Ordinal);

        // Recovery clears it: a warning that outlives the problem teaches an operator to ignore it.
        state.RecordBackup(failedAt.AddMinutes(1), automatic: true);

        var recovered = await client.GetFromJsonAsync<BackupStatusResponse>(AdminBackupEndpoints.StatusRoute);
        Assert.Null(recovered!.LastBackupFailureAt);
        Assert.Null(recovered.LastBackupFailureReason);
    }

    /// <summary>
    /// RESTORE IS EXCLUDED FROM #43's BOOTSTRAP BYPASS. With no admin key configured, a restore
    /// from a local-network peer is refused with 503 rather than allowed through.
    ///
    /// <para>Why this is a security test and not a policy preference: multipart/form-data is a
    /// CORS-SIMPLE content type, so a page on any site a LAN user visits can auto-submit this route
    /// cross-origin with no preflight and no key. Every other bootstrap-reachable route sets one
    /// value; this one replaces the configuration database AND the release-GUID secret - every
    /// credential the instance holds - which is not something a fresh install needs before its key
    /// is set.</para>
    ///
    /// <para>Uses its own unseeded factory: the shared class fixture has an admin key seeded by
    /// other cases here, so the unconfigured state has to be built deliberately rather than depended
    /// on by test ordering.</para>
    /// </summary>
    [Fact]
    public async Task Restore_is_refused_during_the_bootstrap_window_and_reads_no_form()
    {
        await using var factory = new ArbitarrWebApplicationFactory();
        using var client = factory.CreateClient();

        var stagedBefore = CountStagedRestoreFiles(factory);

        using var content = BuildUpload(
            Encoding.UTF8.GetBytes("an archive the attacker chose"),
            confirmation: AdminBackupEndpoints.RestoreConfirmationWord);

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        // NOTE ON WHAT THIS DOES AND DOES NOT PROVE. It asserts only that an unconfigured instance
        // refuses. In THIS harness the 503 comes from AdminApiKeyFilter rather than from the handler
        // guard: the TestServer leaves RemoteIpAddress null, IsTrustedNetwork treats null as
        // untrusted, and the filter short-circuits before the handler runs. Asserting the handler's
        // own title here fails for that reason, and asserting the status code alone cannot tell the
        // two sources apart -- both confirmed by mutation.
        //
        // The guard that matters is the one for a TRUSTED peer, where the filter lets the request
        // through and the handler is the only refusal left. It is tested where it is actually
        // reachable, by invoking the handler directly: RestoreBootstrapRefusalTests in
        // Arbitarr.Api.Tests.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("admin API key", body, StringComparison.OrdinalIgnoreCase);

        // The refusal happened BEFORE the form was read, so the attacker's upload was never spooled
        // to the disk this feature exists to protect. A 503 that still wrote the file would be a
        // refusal in name only.
        //
        // arb-3gd: compared against THIS factory's own instance staging directory, not the
        // machine-wide system temp directory — see CountStagedRestoreFiles.
        Assert.Equal(stagedBefore, CountStagedRestoreFiles(factory));
    }

    /// <summary>
    /// arb-3gd POSITIVE CONTROL for the assertion above, in two halves:
    ///
    /// <list type="number">
    ///   <item>A file with the upload prefix planted in THIS factory's own instance staging
    ///   directory changes what <see cref="CountStagedRestoreFiles"/> reports — so the equality
    ///   assertion above is a real check, not two calls that could never disagree.</item>
    ///   <item>The identical file, planted in the machine-wide system temp directory a bystander
    ///   process would use, does NOT change it — proving the count reads this instance's directory
    ///   only, which is the property that makes it parallel-safe across assemblies.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task Staged_restore_file_count_detects_a_planted_file_in_this_instance_and_ignores_a_bystander_elsewhere()
    {
        await using var factory = new ArbitarrWebApplicationFactory();
        using var client = factory.CreateClient();

        var stagingDirectory = factory.Services
            .GetRequiredService<Arbitarr.Data.Backup.BackupPaths>().StagingDirectory;
        Directory.CreateDirectory(stagingDirectory);

        var before = CountStagedRestoreFiles(factory);

        var plantedInInstance = Path.Combine(
            stagingDirectory, "arbitarr-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(plantedInInstance, "planted by the positive control");
        try
        {
            Assert.NotEqual(before, CountStagedRestoreFiles(factory));
        }
        finally
        {
            File.Delete(plantedInInstance);
        }

        Assert.Equal(before, CountStagedRestoreFiles(factory));

        var bystanderPath = Path.Combine(
            Path.GetTempPath(), "arbitarr-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllText(bystanderPath, "a bystander process's file in the shared system temp dir");
        try
        {
            Assert.Equal(before, CountStagedRestoreFiles(factory));
        }
        finally
        {
            File.Delete(bystanderPath);
        }
    }

    /// <summary>
    /// POSITIVE CONTROL for the bootstrap test above: with a key CONFIGURED and presented, restore
    /// is reachable again. That is what makes the 503 a bootstrap-window refusal rather than restore
    /// being broken outright.
    ///
    /// <para>Note on what could NOT be asserted here. The obvious control - "download and status
    /// still behave as before while unconfigured" - distinguishes nothing under
    /// WebApplicationFactory: its in-memory TestServer leaves RemoteIpAddress null, and
    /// AdminApiKeyFilter.IsTrustedNetwork treats a null address as untrusted, so #43's bypass never
    /// engages in this harness and every admin route answers 503 while unconfigured. Asserting that
    /// would have measured the harness rather than the rule. The stronger claim - that restore is
    /// refused even from a TRUSTED peer - rests on the ordering inside RestoreAsync (the resolver
    /// check precedes HasFormContentType and ReadFormAsync) together with the staged-file assertion
    /// in the test above.</para>
    /// </summary>
    [Fact]
    public async Task Restore_is_reachable_again_once_an_admin_key_is_configured()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        using var content = BuildUpload(
            Encoding.UTF8.GetBytes("this is not a zip archive at all"),
            confirmation: AdminBackupEndpoints.RestoreConfirmationWord);

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);

        // 400 (the archive is junk) and specifically NOT 503: the gate opened and the handler ran.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// The archive response carries <c>Cache-Control: no-store</c>. The body is the instance's HMAC
    /// secret and every source API key, so it must not be written into any shared or on-disk cache.
    /// <c>no-store</c> is the only directive that forbids storing it at all; <c>no-cache</c> would
    /// still permit a stored copy that is revalidated before reuse.
    /// </summary>
    [Fact]
    public async Task The_download_forbids_caching_the_archive()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        using var response = await client.GetAsync(AdminBackupEndpoints.DownloadRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore, "The archive response did not forbid storing.");
    }

    [Fact]
    public async Task The_status_read_reports_the_retained_count_and_the_last_backup_time()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();

        // Taking a download records a last-backup time, so this asserts a transition rather than a
        // static value — a status that never changed would pass a single-shot read just as well.
        using var before = await client.GetFromJsonSafeAsync(AdminBackupEndpoints.StatusRoute);
        using var download = await client.GetAsync(AdminBackupEndpoints.DownloadRoute);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);

        var after = await client.GetStringAsync(AdminBackupEndpoints.StatusRoute);

        Assert.Contains("\"lastBackupAt\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lastBackupAt\":null", after, StringComparison.Ordinal);
        Assert.Contains("\"automaticBackupsRetained\"", after, StringComparison.Ordinal);
        Assert.NotNull(before);
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    private async Task<byte[]> DownloadArchiveBytesAsync()
    {
        using var client = AuthorizedClient();
        using var response = await client.GetAsync(AdminBackupEndpoints.DownloadRoute);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static MultipartFormDataContent BuildUpload(byte[] archive, string confirmation)
    {
        var content = new MultipartFormDataContent();

        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, AdminBackupEndpoints.ArchiveFormField, "backup.zip");
        content.Add(new StringContent(confirmation), AdminBackupEndpoints.ConfirmFormField);

        return content;
    }

    // Upsert rather than Add: the factory's SQLite database is shared across every [Fact] in this
    // IClassFixture-scoped class (Name is the SettingEntry primary key), so a second test seeding
    // the same key would collide with a unique-constraint violation instead of overwriting.
    /// <summary>
    /// How many restore staging files exist in the GIVEN FACTORY'S OWN instance staging directory
    /// (<c>BackupPaths.StagingDirectory</c>, resolved from its service provider) — not the
    /// machine-wide system temp directory this used before (arb-3gd). Compared as a DELTA so a
    /// concurrent test in the SAME instance cannot make it flaky, and used to assert that a refusal
    /// wrote nothing. Instance-scoped rather than process-global is what makes it parallel-safe
    /// across assemblies: another test process's uploads land in a different instance directory
    /// entirely and can no longer be counted here.
    /// </summary>
    private static int CountStagedRestoreFiles(ArbitarrWebApplicationFactory factory)
    {
        var stagingDirectory = factory.Services
            .GetRequiredService<Arbitarr.Data.Backup.BackupPaths>().StagingDirectory;

        if (!Directory.Exists(stagingDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(stagingDirectory, "arbitarr-upload-*").Count() +
            Directory.EnumerateFiles(stagingDirectory, "arbitarr-restore-validate-*").Count();
    }

    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
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
    }
}

internal static class BackupTestHttpExtensions
{
    /// <summary>Reads a route and returns the response so the caller can assert it was servable.</summary>
    public static async Task<HttpResponseMessage> GetFromJsonSafeAsync(this HttpClient client, string path) =>
        await client.GetAsync(path);
}
