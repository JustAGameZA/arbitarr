using Arbitarr.Core.Security;
using Arbitarr.Data;
using Arbitarr.Data.Backup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Api.Admin;

/// <summary>Response body of <c>GET /api/admin/backup/status</c> (#56).</summary>
/// <param name="LastBackupAt">When the most recent backup was taken, or null if none is known.</param>
/// <param name="LastBackupAutomatic">True when the most recent known backup was a scheduled one.</param>
/// <param name="AutomaticBackupsRetained">
/// How many automatic archives are kept; 0 means automatic backups are off.
/// </param>
/// <param name="LastBackupFailureAt">
/// When the last automatic backup FAILED, or null when the most recent pass succeeded. Without
/// this, a broken scheduled backup is indistinguishable from a healthy one that simply has not run
/// again yet: <paramref name="LastBackupAt"/> just stops advancing.
/// </param>
/// <param name="LastBackupFailureReason">Why it failed. A reason string, never a stack trace.</param>
/// <param name="LastRestoreAt">When the last restore was ATTEMPTED this process lifetime, or null.</param>
/// <param name="LastRestoreSucceeded">Whether that attempt applied.</param>
/// <param name="LastRestoreMessage">The outcome message from that attempt.</param>
public sealed record BackupStatusResponse(
    DateTimeOffset? LastBackupAt,
    bool LastBackupAutomatic,
    int AutomaticBackupsRetained,
    DateTimeOffset? LastBackupFailureAt,
    string? LastBackupFailureReason,
    DateTimeOffset? LastRestoreAt,
    bool LastRestoreSucceeded,
    string? LastRestoreMessage);

/// <summary>Response body of <c>POST /api/admin/restore</c> (#56).</summary>
/// <param name="Succeeded">False when the archive was refused; nothing was changed.</param>
/// <param name="Message">Operator-facing outcome. Never carries file contents.</param>
/// <param name="PreRestoreBackupTaken">Whether the pre-restore safety copy was written.</param>
/// <param name="Restarting">Whether the host was asked to stop so the restored state is loaded.</param>
public sealed record RestoreResponse(
    bool Succeeded,
    string Message,
    bool PreRestoreBackupTaken,
    bool Restarting);

/// <summary>
/// Maps the three #56 routes: the backup download, the restore upload, and the small status read
/// behind the System page's Backup tab.
///
/// <para><b>THE DOWNLOAD IS A GET AND IS STILL GATED — that is the point, not an inconsistency.</b>
/// It carries <see cref="Arbitarr.Api.Routing.RouteClassification.AdminMutating"/> for the same
/// reason <see cref="LogsEndpoint"/> and <c>ObservabilityEndpoint</c> do: the classification reads,
/// for a GET, as "gated by the admin key" and never as a claim that the handler writes. Here the
/// sensitivity argument is at its strongest in the whole codebase — the response body IS the
/// instance's HMAC secret and every configured source's API key. Classifying by HTTP verb would
/// serve that file to anyone who can reach the port.</para>
///
/// <para><b>NO DOWNLOAD TOKEN IN THE URL, EVER.</b> The archive is fetched with the
/// <c>X-Admin-Api-Key</c> header like every other admin call — the SPA reads it as a blob and hands
/// it to the browser through an object URL. A signed or one-shot download URL would be the obvious
/// way to make an <c>&lt;a download&gt;</c> work, and it is exactly what plan §3.3 forbids: a URL
/// granting access to a credential-bearing file lands in browser history, in any reverse proxy's
/// access log, and in the <c>IHttpClientFactory</c> logging handler's own Information-level record
/// of full absolute URIs. A header does not.</para>
///
/// <para><b>WHY THE RESTORE UPLOAD IS READ FROM <c>HttpContext</c> AND NOT BOUND AS A PARAMETER.</b>
/// Read this before "tidying" the handler to take an <c>IFormFile</c> parameter. A bound form/body
/// parameter is model-bound BEFORE endpoint filters run, so a request with no body short-circuits
/// to 400 without <see cref="AdminApiKeyFilter"/> ever executing — letting an unauthenticated remote
/// caller tell a malformed request (400) from a well-formed one (503) and thereby learn that this
/// route exists. <see cref="AdminSecurityEndpoints"/> solves the same problem with
/// <c>[FromBody(EmptyBodyBehavior.Allow)]</c>, which has no multipart equivalent; reading the form
/// inside the handler, after the gate has already run, is the multipart form of the same rule.
/// <c>AdminApiKeyRouteEnumerationTests</c> sweeps every concrete admin route with NO body at all and
/// is what catches a regression here.</para>
/// </summary>
public static class AdminBackupEndpoints
{
    public const string DownloadRoute = "/api/admin/backup";
    public const string StatusRoute = "/api/admin/backup/status";
    public const string RestoreRoute = "/api/admin/restore";

    /// <summary>
    /// The word an operator must type to confirm a restore, sent as the <c>confirm</c> form field.
    /// Fixed and matched EXACTLY (ordinal, case-sensitive) rather than parsed loosely: this is the
    /// last gate in front of the most destructive action in the product, and a confirmation that
    /// accepts near-misses is not a confirmation.
    /// </summary>
    public const string RestoreConfirmationWord = "RESTORE";

    /// <summary>The multipart field name carrying the uploaded archive.</summary>
    public const string ArchiveFormField = "archive";

    /// <summary>The multipart field name carrying the typed confirmation.</summary>
    public const string ConfirmFormField = "confirm";

    /// <summary>
    /// Cap on an accepted upload. A backup of a homelab config database is single-digit megabytes,
    /// so 64 MB is far above any real one and still refuses to spool an arbitrary upload onto the
    /// very disk this feature exists to protect.
    ///
    /// <para><b>IT IS ENFORCED BEFORE THE BODY IS BUFFERED, AND THAT IS THE WHOLE POINT.</b> The
    /// obvious placement — checking <c>IFormFile.Length</c> inside the handler — reads like a limit
    /// and is not one: <c>ReadFormAsync</c> has by then already spooled the entire upload to a temp
    /// file, so the check reports on a cost that has been paid in full. The limit is therefore
    /// applied to the request body itself (see <see cref="Map"/>), which lets Kestrel refuse an
    /// oversized upload while it is still on the wire. The in-handler check below is kept as the
    /// backstop that turns a refusal into a message naming the limit, not as the limit.</para>
    ///
    /// <para>It must also stay BELOW the framework defaults it sits behind (Kestrel's 30 MB body
    /// cap and the 128 MB multipart cap), or the constant is unreachable and an operator gets a
    /// bare 413 instead of a sentence telling them what the ceiling is.</para>
    /// </summary>
    public const long MaxUploadBytes = 64L * 1024 * 1024;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(DownloadRoute, DownloadAsync).RequireAdminApiKey();
        endpoints.MapGet(StatusRoute, GetStatusAsync).RequireAdminApiKey();
        endpoints.MapPost(RestoreRoute, RestoreAsync).RequireAdminApiKey();
    }

    /// <summary>
    /// Streams a fresh backup archive. The file name carries an ISO 8601 UTC timestamp so an
    /// operator with several downloads can tell which is which without opening them.
    /// </summary>
    public static async Task<IResult> DownloadAsync(
        HttpContext context,
        BackupService backupService,
        BackupStateStore state,
        BackupPaths paths,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var takenAt = timeProvider.GetUtcNow();

        // Built at a staging path and streamed from there. Never inside the repository tree and
        // never under a statically-served directory — plan §3.3. Arb-3gd: this instance's own
        // BackupPaths.StagingDirectory, not the machine-wide Path.GetTempPath() — see that
        // property's doc comment for why the shared system temp directory was the wrong place.
        var archivePath = Path.Combine(
            paths.EnsureStagingDirectory(),
            StagingFileNames.DownloadPrefix + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await backupService.WriteArchiveAsync(archivePath, cancellationToken);

            // arb-gk6: persists the instant to the backup directory's state file so it survives a
            // restart, not just RecordBackup's in-memory record.
            state.RecordManualDownload(takenAt, paths);

            // The response body is the instance's HMAC secret and every source API key, so it must
            // not be written to a shared cache, a disk cache, or a proxy's store. no-store is the
            // only directive that forbids writing it anywhere at all; no-cache would still permit a
            // stored copy that is merely revalidated before reuse.
            context.Response.Headers.CacheControl = "no-store";

            var bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken);
            return Results.File(bytes, "application/zip", BackupService.FileNameFor(takenAt));
        }
        finally
        {
            try
            {
                if (File.Exists(archivePath))
                {
                    File.Delete(archivePath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The one refusal message for an oversized upload, so the three places that can detect it do
    /// not each word the limit differently.
    /// </summary>
    private static IResult TooLarge() =>
        Results.Json(
            new
            {
                error = "That file is larger than the " + (MaxUploadBytes / (1024 * 1024)) +
                    " MB limit for a backup archive.",
            },
            statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// The small read behind the Backup tab: when the last backup was taken, whether automatic
    /// backups are on, and the outcome of the last restore attempt this process saw.
    /// </summary>
    public static async Task<IResult> GetStatusAsync(
        BackupStateStore state,
        Arbitarr.Data.Settings.SettingsReader settingsReader,
        CancellationToken cancellationToken)
    {
        var retained = await settingsReader.GetAutomaticBackupRetainedCountAsync(cancellationToken);

        var lastBackup = state.LastBackup;
        var lastFailure = state.LastBackupFailure;
        var lastRestore = state.LastRestore;

        return Results.Ok(new BackupStatusResponse(
            LastBackupAt: lastBackup?.TakenAt,
            LastBackupAutomatic: lastBackup?.Automatic ?? false,
            AutomaticBackupsRetained: retained,
            LastBackupFailureAt: lastFailure?.AttemptedAt,
            LastBackupFailureReason: lastFailure?.Reason,
            LastRestoreAt: lastRestore?.AttemptedAt,
            LastRestoreSucceeded: lastRestore?.Succeeded ?? false,
            LastRestoreMessage: lastRestore?.Message));
    }

    /// <summary>
    /// Validates and applies an uploaded archive, then asks the host to stop so the restored state
    /// is actually loaded.
    ///
    /// The form is read from <paramref name="context"/> rather than bound — see the class remarks
    /// for why that is load-bearing and not a style choice.
    /// </summary>
    public static async Task<IResult> RestoreAsync(
        HttpContext context,
        ICredentialResolver keyResolver,
        RestoreService restoreService,
        RestoreCoordinator coordinator,
        BackupStateStore state,
        ArbitarrDbContext dbContext,
        BackupPaths paths,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // RESTORE IS DELIBERATELY EXCLUDED FROM #43's BOOTSTRAP BYPASS, AND THIS RUNS BEFORE THE
        // FORM IS READ.
        //
        // While no admin key exists, AdminApiKeyFilter lets an unkeyed request from an RFC1918 peer
        // through so a fresh install can be configured. multipart/form-data is a CORS-SIMPLE content
        // type, so a page on any website a LAN user visits can auto-submit this route cross-origin
        // -- no preflight, no key -- and replace the whole configuration plus the release-GUID
        // secret with an attacker's archive. Every other bootstrap-reachable route sets one thing;
        // this one replaces every credential the instance holds, which is not an operation a fresh
        // install needs before its key is set.
        //
        // Ordered before HasFormContentType and before ReadFormAsync on purpose: refusing after the
        // body is read would still have spooled the attacker's upload to disk.
        var resolution = await keyResolver.ResolveAsync(
            context.Request.Headers[AdminApiKeyFilter.HeaderName].ToString(),
            ApiKeyScope.Admin,
            cancellationToken);

        if (resolution.Outcome == CredentialResolutionOutcome.NotConfigured)
        {
            return Results.Problem(
                title: "Set an admin API key first",
                detail:
                    "Restore replaces the configuration database and the release-GUID secret - every " +
                    "credential this instance holds - so it is not available before an admin API key " +
                    "is configured. Create one via POST /api/admin/keys, or set the shared key via " +
                    "PUT /api/admin/security/admin-key, then restore.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest(new
            {
                error = "Upload the backup archive as multipart/form-data with an '" +
                    ArchiveFormField + "' file field and a '" + ConfirmFormField + "' field.",
            });
        }

        // BOUND THE BODY BEFORE READING IT. ReadFormAsync spools the entire upload to a temp file
        // before returning, so a length check after it reports on a cost already paid — on the very
        // disk this feature exists to protect. Two guards, because neither alone is enough:
        //
        //   - Content-Length, when the client sent one, refuses an oversized upload without reading
        //     a byte of it. It is advisory (a chunked request has none, and a hostile client can
        //     understate it), which is why it is not the only check.
        //   - MaxRequestBodySize lowers Kestrel's own per-request ceiling, so the server stops
        //     reading at the cap and answers 413 even when Content-Length lied or was absent. This
        //     is the one that actually holds; the header check just fails faster and more clearly.
        if (context.Request.ContentLength > MaxUploadBytes)
        {
            return TooLarge();
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = MaxUploadBytes;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            // Kestrel stopped reading at the cap. Translated into the same message the header check
            // produces so an operator gets one explanation of the limit, not two.
            return TooLarge();
        }

        var confirmation = form[ConfirmFormField].ToString();
        if (!string.Equals(confirmation, RestoreConfirmationWord, StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = "Type " + RestoreConfirmationWord + " to confirm. Restoring replaces the " +
                    "configuration database AND the release-GUID secret; an older secret invalidates " +
                    "every release GUID issued since that backup was taken.",
            });
        }

        var upload = form.Files.GetFile(ArchiveFormField);
        if (upload is null || upload.Length == 0)
        {
            return Results.BadRequest(new { error = "No backup archive was uploaded." });
        }

        // Backstop only. The body was already bounded above; this catches the case where the
        // multipart envelope fitted but the file part itself is still implausible.
        if (upload.Length > MaxUploadBytes)
        {
            return TooLarge();
        }

        // Arb-3gd: this instance's own BackupPaths.StagingDirectory, not the machine-wide
        // Path.GetTempPath() — see BackupPaths.StagingSubdirectoryName's doc comment. Resolved (and
        // the directory created) only here, AFTER the bootstrap-refusal check above and after the
        // confirmation/size gates: a refused request must stage nothing, so nothing above this line
        // may touch the staging directory.
        var stagedPath = Path.Combine(
            paths.EnsureStagingDirectory(),
            StagingFileNames.UploadPrefix + Guid.NewGuid().ToString("N") + ".zip");

        try
        {
            await using (var staged = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await upload.CopyToAsync(staged, cancellationToken);
            }

            // The migrations THIS build knows how to apply. An archive naming a newer one is
            // refused by the validator with both ids in the message.
            var knownMigrations = dbContext.Database.GetMigrations().ToList();

            // TryRestoreAsync, not RestoreAsync: a second concurrent restore is REFUSED rather than
            // queued. Queuing would apply it to a host the first restore has already asked to stop,
            // and the two would race over the single fixed pre-restore safety-copy path - see
            // RestoreService._restoreGate for why an interleaving corrupts that copy specifically.
            var result = await restoreService.TryRestoreAsync(stagedPath, knownMigrations, cancellationToken);
            if (result is null)
            {
                return Results.Problem(
                    title: "A restore is already running",
                    detail:
                        "Another restore is in progress on this instance. Wait for it to finish - it " +
                        "shuts Arbitarr down when it completes - and check the Backup tab afterwards " +
                        "before trying again.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            state.RecordRestore(timeProvider.GetUtcNow(), result.Succeeded, result.Message);

            if (!result.Succeeded)
            {
                return Results.BadRequest(new RestoreResponse(
                    Succeeded: false,
                    Message: result.Message,
                    PreRestoreBackupTaken: result.PreRestoreBackupTaken,
                    Restarting: false));
            }

            // Requested AFTER the response is composed, and honoured only once it has been written
            // — see RestoreCoordinator. Stopping here would abort this very request and leave the
            // operator staring at a connection reset after the most destructive action available.
            var restarting = coordinator.RequestRestart();

            return Results.Ok(new RestoreResponse(
                Succeeded: true,
                Message: result.Message,
                PreRestoreBackupTaken: result.PreRestoreBackupTaken,
                Restarting: restarting));
        }
        finally
        {
            try
            {
                if (File.Exists(stagedPath))
                {
                    File.Delete(stagedPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
