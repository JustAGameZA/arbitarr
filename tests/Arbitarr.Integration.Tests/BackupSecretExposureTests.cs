using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Backup;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #56, plan §5's last bullet: the contents of the release-GUID key file must never appear in a
/// response body, an error message, or a log row.
///
/// <para><b>EVERY ABSENCE ASSERTION HERE CARRIES A POSITIVE CONTROL, AND THAT IS THE POINT.</b>
/// <c>Assert.DoesNotContain(secret, body)</c> passes just as happily when the secret was never in
/// play at all — an empty set contains nothing, and a test that searches a 404 page for a key it
/// never planted proves precisely nothing. CLAUDE.md §4 records three occasions on which exactly
/// this shape shipped alongside a real leak. So each test below FIRST demonstrates that the key
/// material would be detectable if it leaked — by finding it in the one place it is legitimately
/// present, the backup archive itself — and only then asserts its absence everywhere else.</para>
///
/// <para>The key material is whatever the host generated on first run; nothing here plants a
/// fixture secret, so there is no committed secret and no risk of asserting against a value the
/// process never actually held.</para>
/// </summary>
public sealed class BackupSecretExposureTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private const string AdminKey = "the-real-admin-key";

    private readonly ArbitarrWebApplicationFactory _factory;

    public BackupSecretExposureTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task The_key_file_contents_never_appear_in_the_status_response()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var keyMaterial = await ReadKeyMaterialFromArchiveAsync(client);

        // POSITIVE CONTROL. Prove the detector works before trusting its negative: the same needle,
        // searched for in a haystack it IS in, must be found. Without this the assertion below
        // would pass for an empty needle, a mis-encoded one, or a key the host never held.
        //
        // The haystack is the archive's DECOMPRESSED key entry, not the archive's raw bytes. Entries
        // are written with CompressionLevel.Optimal, so the key is deflated inside the .zip and a
        // raw byte scan of the file would not find it -- a control that searched the container
        // rather than the content would fail for a reason that has nothing to do with detectability
        // and would have to be weakened to pass, which is how a control quietly becomes decorative.
        var archivedKey = await ReadKeyEntryAsync(await DownloadArchiveAsync(client));
        Assert.True(
            IndexOf(archivedKey, keyMaterial) >= 0,
            "Positive control failed: the key material was not found in the archive entry that " +
            "definitely contains it, so the absence assertions below would be vacuous.");

        var status = await client.GetByteArrayAsync(AdminBackupEndpoints.StatusRoute);
        Assert.True(status.Length > 0, "The status response was empty, so searching it proves nothing.");

        Assert.True(IndexOf(status, keyMaterial) < 0, "The key material appeared in the status response.");
        Assert.DoesNotContain(Convert.ToBase64String(keyMaterial), Encoding.UTF8.GetString(status), StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(keyMaterial), Encoding.UTF8.GetString(status), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_key_file_contents_never_appear_in_a_restore_error_message()
    {
        // The likeliest leak path in this feature: a validator that echoes the bytes it rejected.
        // The upload here CONTAINS the real key material, so if the error message quoted any part
        // of what it was given, it would carry the secret straight into a response body and into
        // the log store behind it.
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var keyMaterial = await ReadKeyMaterialFromArchiveAsync(client);

        // POSITIVE CONTROL, on the exact bytes being uploaded: the payload really does carry the
        // key material, so an error message that echoed its input would be caught. Checked through
        // the entry rather than over the container for the same reason as above -- the entry is
        // deflated, so the raw upload does not contain the key bytes literally.
        var corrupt = BuildArchiveWithCorruptDatabase(keyMaterial);
        Assert.True(
            IndexOf(await ReadKeyEntryAsync(corrupt), keyMaterial) >= 0,
            "Positive control failed: the uploaded payload does not contain the key material, so " +
            "an echoing error message would not be detectable by this test.");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(corrupt);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(file, AdminBackupEndpoints.ArchiveFormField, "backup.zip");
        content.Add(
            new StringContent(AdminBackupEndpoints.RestoreConfirmationWord),
            AdminBackupEndpoints.ConfirmFormField);

        using var response = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.True(body.Length > 0, "The error response was empty, so searching it proves nothing.");
        Assert.True(IndexOf(body, keyMaterial) < 0, "The key material appeared in the restore error message.");

        var text = Encoding.UTF8.GetString(body);
        Assert.DoesNotContain(Convert.ToBase64String(keyMaterial), text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(keyMaterial), text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_key_file_contents_never_appear_in_the_log_store()
    {
        await SeedAdminKeyAsync();

        using var client = AuthorizedClient();
        var keyMaterial = await ReadKeyMaterialFromArchiveAsync(client);

        // Drive both routes so anything they log has been written.
        using (var download = await client.GetAsync(AdminBackupEndpoints.DownloadRoute))
        {
            download.EnsureSuccessStatusCode();
        }

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not a zip")), AdminBackupEndpoints.ArchiveFormField, "x.zip");
        content.Add(
            new StringContent(AdminBackupEndpoints.RestoreConfirmationWord),
            AdminBackupEndpoints.ConfirmFormField);
        using (var _ = await client.PostAsync(AdminBackupEndpoints.RestoreRoute, content))
        {
        }

        // The SQLite log sink batches on a background loop, so rows written by the two requests
        // above are not visible the instant those requests return. Waiting out the flush interval
        // is what LogSecretInjectionTests does for the same reason; without it the positive control
        // below fails intermittently on an empty table, which reads as "no leak" from a test that
        // in fact searched nothing.
        await FlushLogSinkAsync();

        var store = _factory.Services.GetRequiredService<LogStore>();
        var page = await store.ReadAsync(null, null, 1, LogStore.MaxPageSize, CancellationToken.None);

        // POSITIVE CONTROL for the log search itself: the store must have rows, or "the key is not
        // in the logs" is a statement about an empty table. Asserting the rows exist is what makes
        // the search below bite.
        Assert.True(page.Total > 0, "The log store held no rows, so searching it for the key proves nothing.");

        var rows = string.Join(
            "\n",
            page.Entries.Select(e => string.Join(" ", e.Time, e.Level, e.Logger, e.Message, e.Exception)));

        Assert.DoesNotContain(Convert.ToBase64String(keyMaterial), rows, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(keyMaterial), rows, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Encoding.Latin1.GetString(keyMaterial), rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the real key material out of a freshly downloaded archive — the one place it is
    /// legitimately present. Read from the wire rather than from disk so it is by construction the
    /// same bytes the process is actually serving.
    /// </summary>
    private async Task<byte[]> ReadKeyMaterialFromArchiveAsync(HttpClient client)
    {
        var bytes = await DownloadArchiveAsync(client);

        using var archive = new ZipArchive(new MemoryStream(bytes));
        using var entry = archive.GetEntry(BackupArchiveLayout.SecretKeyEntryName)!.Open();
        using var buffer = new MemoryStream();
        await entry.CopyToAsync(buffer);

        var material = buffer.ToArray();
        Assert.True(material.Length >= 16, "The archive's key entry was too short to be the real 32-byte secret.");

        // Cross-check the archived bytes against the key file this host is actually running on. If
        // they ever disagree, every assertion below would be searching for a needle the process
        // never held — the exact vacuity these tests exist to rule out — so it fails loudly here
        // rather than passing quietly there.
        var paths = _factory.Services.GetRequiredService<BackupPaths>();
        var onDisk = await File.ReadAllBytesAsync(paths.SecretKeyPath);
        Assert.True(
            onDisk.AsSpan().SequenceEqual(material),
            "The archived key does not match the key file this host is running on, so the positive " +
            "controls below would be meaningless.");

        return material;
    }

    /// <summary>
    /// Decompresses an archive's release-GUID key entry. Zip entries here are deflated, so the key
    /// bytes never appear literally in the container -- any check that the archive "contains" the
    /// key has to go through the entry.
    /// </summary>
    private static async Task<byte[]> ReadKeyEntryAsync(byte[] archiveBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(archiveBytes));
        using var entry = archive.GetEntry(BackupArchiveLayout.SecretKeyEntryName)!.Open();
        using var buffer = new MemoryStream();
        await entry.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>
    /// Waits out <see cref="SqliteLoggerProvider.FlushInterval"/> so queued log entries have been
    /// written before the store is read. Same approach, and the same margin, as
    /// <c>LogSecretInjectionTests</c>.
    /// </summary>
    private static async Task FlushLogSinkAsync() =>
        await Task.Delay(SqliteLoggerProvider.FlushInterval + TimeSpan.FromMilliseconds(750));

    private static async Task<byte[]> DownloadArchiveAsync(HttpClient client)
    {
        using var response = await client.GetAsync(AdminBackupEndpoints.DownloadRoute);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>
    /// An archive whose database entry is junk but whose key entry is the REAL key material, so a
    /// validator that echoed its input would be caught by the assertions above.
    /// </summary>
    private static byte[] BuildArchiveWithCorruptDatabase(byte[] keyMaterial)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var database = archive.CreateEntry(BackupArchiveLayout.DatabaseEntryName).Open())
            {
                database.Write(Encoding.UTF8.GetBytes("not a SQLite file at all"));
            }

            using var key = archive.CreateEntry(BackupArchiveLayout.SecretKeyEntryName).Open();
            key.Write(keyMaterial);
        }

        return buffer.ToArray();
    }

    /// <summary>Raw byte-sequence search — the encoding-independent form of "does this contain that".</summary>
    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return -1;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return start;
            }
        }

        return -1;
    }

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
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
