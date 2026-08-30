using System.Net;
using Arbitarr.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// M7-11 (plan §M7: "container starts from a clean volume and self-provisions its schema").
/// Boots the real Host composition root (<c>Program.cs</c>, unmodified — AC6) against a brand-new,
/// never-before-used <c>ARBITARR_CONFIG_DIR</c> so the SQLite file does not exist until the Host
/// creates it, and asserts that <c>dbContext.Database.Migrate()</c> (called on a fresh scope before
/// <c>app.Run()</c>) leaves behind a schema with the expected tables, and that a lite read-only
/// dashboard route answers 200 against that freshly-provisioned database.
/// </summary>
public sealed class StartupSchemaProvisioningTests : IDisposable
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-m7-11-clean-volume-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Host_self_provisions_schema_from_a_clean_volume_and_serves_a_lite_route()
    {
        // Precondition: this path has never existed, so the SQLite file the Host will open does not
        // exist yet either — this is the "clean /config volume" scenario M7-11 targets.
        Assert.False(Directory.Exists(_configDirectory));

        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", _configDirectory);

        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        // A lite, unauthenticated, read-only dashboard route (D1) must answer 200 once startup has
        // completed — this only happens if Database.Migrate() succeeded, since StatusEndpoint reads
        // from ArbitarrDbContext.
        var statusResponse = await client.GetAsync("/api/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var databasePath = Path.Combine(_configDirectory, "arbitarr.db");
        Assert.True(File.Exists(databasePath));

        // Assert the schema itself exists (tables present), independent of any particular endpoint's
        // behaviour, by querying sqlite_master directly for the entities the initial migration creates.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        Assert.Contains("SourceHealthRecords", tableNames);
        Assert.Contains("Settings", tableNames);
        Assert.Contains("SuppressionAuditLogEntries", tableNames);

        // Also confirm the EF migrations-history table records the InitialCreate migration as
        // applied, which is what Database.Migrate() being idempotent on a re-run depends on.
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        var appliedMigrations = await dbContext.Database.GetAppliedMigrationsAsync();
        Assert.Contains(appliedMigrations, m => m.Contains("InitialCreate", StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", null);

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
