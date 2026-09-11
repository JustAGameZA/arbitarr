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
    }

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
