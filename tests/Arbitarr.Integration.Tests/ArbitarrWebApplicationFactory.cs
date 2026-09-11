using Arbitarr.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// Hosts the real <c>Arbitarr.Host</c> composition root in-process (<c>Program.cs</c>, unmodified)
/// against a fresh, per-instance SQLite file under a temp <c>/config</c> directory, so M2's
/// dashboard endpoints, migrations-on-startup behaviour, and static file serving are all exercised
/// exactly as they run in production. Callers seed rows via <see cref="SeedAsync"/> before issuing
/// requests through <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>.
/// </summary>
public sealed class ArbitarrWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m2-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The per-instance <c>/config</c> directory this host was given.</summary>
    public string ConfigDirectory => _configDirectory;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_configDirectory);

        // UseSetting, NEVER Environment.SetEnvironmentVariable. The env var is process-wide, so
        // with several hosts alive in one process the last writer wins and a host can end up
        // opening a neighbour's SQLite file. This setting belongs to this builder alone, which is
        // what lets the assembly run its classes in parallel. Program.cs reads Arbitarr:ConfigDir
        // ahead of the env var precisely so this wins.
        builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);

        // arb-0hd0, and a UseSetting for the same reason as the line above: ReleaseGuid's HMAC
        // secret is a mutable PROCESS-GLOBAL (ReleaseGuid._hmacKey), and Program.cs rewrote it via
        // ReleaseGuid.Configure on every host build. With classes running in parallel, a second
        // host starting mid-request changed the secret under the first host, whose search had
        // already computed a lookup key with the old one -- the issued link then matched neither
        // the memory tier nor the store, and the download returned 404 with no exception and no
        // log (arb-agh, four occurrences; the two hosts were 0.5 ms apart in the trace).
        //
        // Supplying the secret here makes Program.cs use this value instead of generating one per
        // config directory, so a host build stops being a rewrite of the global with a NEW value.
        // Derived from the config directory rather than random so that a factory rebuilding a host
        // for the same directory reproduces the same secret -- which is what the persisted-secret
        // file gives production across restarts, and what tests that restart a host depend on.
        //
        // This does NOT make the static safe on its own, and must not be mistaken for the fix: two
        // factories still write different values to the same global. The fix is that
        // RenderedRelease.ProxyGuid is now computed once per instance, so a request's five
        // evaluations agree even mid-swap. This just stops tests provoking the swap needlessly.
        //
        // Note Program.cs still CREATES the persisted secret file even when this setting is given,
        // and that is deliberate: BackupService copies release-guid-secret.key into every archive
        // unconditionally, so a host that skipped creating it answered 500 on the backup download.
        // Supplying this key overrides the in-memory value only; it must never be turned into a
        // reason to skip ReleaseGuidSecretFile.LoadOrCreate.
        builder.UseSetting("Arbitarr:ReleaseGuidSecret", ReleaseGuidSecretForConfigDirectory(_configDirectory));
    }

    /// <summary>
    /// A stable 32-byte secret for a config directory, base64-encoded as the configuration key
    /// expects. SHA-256 of the path: deterministic for the same directory, different for different
    /// ones (so two factories cannot collide on guids), and no weaker than the random secret it
    /// replaces for a test-only value that never leaves this process.
    /// </summary>
    private static string ReleaseGuidSecretForConfigDirectory(string configDirectory) =>
        Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(configDirectory)));

    /// <summary>Runs <paramref name="seed"/> against a fresh scoped <see cref="ArbitarrDbContext"/> and saves changes.</summary>
    public async Task SeedAsync(Func<ArbitarrDbContext, Task> seed)
    {
        // Force host startup (and its Database.Migrate() call) before seeding against the same schema.
        using var client = CreateClient();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        await seed(dbContext);
        await dbContext.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_configDirectory))
                {
                    Directory.Delete(_configDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked SQLite file on Windows shouldn't fail the test run.
            }
        }
    }
}
