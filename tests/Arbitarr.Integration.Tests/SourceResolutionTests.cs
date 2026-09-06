using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #53 stage 53b: the source-resolution read path, driving the real <c>Arbitarr.Host</c> composition
/// root (<c>Program.cs</c>, unmodified) so the startup ordering that makes this work — migrate, then
/// seed/resolve, then serve — is exercised exactly as it runs in production.
///
/// These tests pin the plan's §3.2 OWNER RULING (seed once from the environment, then the database
/// is authoritative). The most important of them is
/// <see cref="Source_rows_survive_a_container_recreated_with_no_environment_variables"/>: it is the
/// regression test for the 2026-09-07 incident and it fails against pre-53b code.
/// </summary>
public sealed class SourceResolutionTests
{
    // RFC 5737 TEST-NET-1 throughout: guaranteed non-routable, so nothing here can reach a real host
    // even by accident, and no real address is committed.
    private const string EnvironmentBaseUrl = "http://192.0.2.10:5076";
    private const string DatabaseBaseUrl = "http://192.0.2.20:5076";

    /// <summary>
    /// Drives Program.cs against a config directory the caller controls, so a single directory can be
    /// reused across two factory lifetimes to simulate a container restart against a persistent
    /// /config volume.
    /// </summary>
    private static WebApplicationFactory<Program> CreateHost(
        string configDirectory,
        bool withEnvironmentVariables,
        string baseUrl = EnvironmentBaseUrl,
        string apiKey = "placeholder-seed-key",
        string sourceName = "NZBHydra2")
    {
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", configDirectory);

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (withEnvironmentVariables)
            {
                builder.UseSetting("Arbitarr:Sources:NzbHydra:BaseUrl", baseUrl);
                builder.UseSetting("Arbitarr:Sources:NzbHydra:ApiKey", apiKey);
                builder.UseSetting("Arbitarr:Sources:NzbHydra:SourceName", sourceName);
            }
        });
    }

    private static string NewConfigDirectory() =>
        Path.Combine(Path.GetTempPath(), "arbitarr-53b-source-resolution", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string configDirectory)
    {
        Environment.SetEnvironmentVariable("ARBITARR_CONFIG_DIR", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(configDirectory))
            {
                Directory.Delete(configDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort; a locked SQLite file on Windows shouldn't fail the run.
        }
    }

    private static async Task<bool> ReadNzbHydraConfiguredAsync(HttpClient client)
    {
        var payload = await client.GetFromJsonAsync<JsonElement>("/api/config/effective");
        return payload.GetProperty("nzbHydraConfigured").GetBoolean();
    }

    /// <summary>
    /// THE regression test for the 2026-09-07 incident, and the single most valuable test in this
    /// stage (plan §5).
    ///
    /// What broke live: the container was recreated on a new image without its environment variables
    /// carried across. Under the pre-53b wiring the source was read straight from
    /// <c>Arbitarr:Sources:NzbHydra:ApiKey</c> at startup, so an absent environment meant an absent
    /// source — <c>nzbHydraConfigured</c> silently flipped true to false with nothing logged.
    ///
    /// This test reproduces exactly that: it starts a host with source rows already in the database
    /// and <b>no source environment variables at all</b>, and asserts the source still resolves.
    ///
    /// It genuinely fails against pre-53b code, because pre-53b Program.cs computed
    /// <c>NzbHydraConfigurationStatus(IsConfigured: !string.IsNullOrWhiteSpace(nzbHydraApiKey))</c>
    /// where <c>nzbHydraApiKey</c> came only from configuration; with no environment variables that
    /// is empty, so <c>nzbHydraConfigured</c> would be false here no matter what the database held.
    /// </summary>
    [Fact]
    public async Task Source_rows_survive_a_container_recreated_with_no_environment_variables()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            // Arrange: a deployment that already has a configured source row (as a seeded or
            // UI-configured deployment would), with its key in the write-only Settings row.
            await using (var seedHost = CreateHost(configDirectory, withEnvironmentVariables: false))
            {
                using var seedClient = seedHost.CreateClient();
                using var scope = seedHost.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();

                var source = new Source
                {
                    Kind = SourceSeeder.NzbHydraKind,
                    DisplayName = "NZBHydra2",
                    BaseUrl = DatabaseBaseUrl,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                dbContext.Sources.Add(source);
                await dbContext.SaveChangesAsync();

                dbContext.Settings.Add(new SettingEntry
                {
                    Name = SourceRepository.ApiKeySettingName(source.Id),
                    Value = "placeholder-stored-key",
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            // Act: the container is recreated — same /config volume, no environment variables.
            await using var recreatedHost = CreateHost(configDirectory, withEnvironmentVariables: false);
            using var client = recreatedHost.CreateClient();

            // Assert: the source still resolves from the database and the dashboard still reports it
            // as configured. Pre-53b this asserted value would be false.
            Assert.True(await ReadNzbHydraConfiguredAsync(client));

            var resolved = recreatedHost.Services.GetRequiredService<ResolvedSourceConfiguration>();
            Assert.Equal(DatabaseBaseUrl, resolved.BaseUrl);
            Assert.Equal("NZBHydra2", resolved.SourceName);
            Assert.True(resolved.IsConfigured);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// First run with an empty sources table and environment variables set: the environment is read
    /// once and written as the initial row, and resolution then matches the pre-53b behaviour exactly
    /// (same base URL, same name, <c>nzbHydraConfigured</c> true).
    /// </summary>
    [Fact]
    public async Task First_run_with_an_empty_table_seeds_the_source_from_the_environment()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using var host = CreateHost(configDirectory, withEnvironmentVariables: true);
            using var client = host.CreateClient();

            Assert.True(await ReadNzbHydraConfiguredAsync(client));

            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();

            var rows = await dbContext.Sources.AsNoTracking().ToListAsync();
            var seeded = Assert.Single(rows);
            Assert.Equal(SourceSeeder.NzbHydraKind, seeded.Kind);
            Assert.Equal("NZBHydra2", seeded.DisplayName);
            Assert.Equal(EnvironmentBaseUrl, seeded.BaseUrl);
            Assert.True(seeded.Enabled);

            // The key was seeded into the write-only Settings row, under the colon-namespaced name no
            // SettingKey enum value can produce — so it can never surface on GET /api/admin/settings.
            Assert.True(await dbContext.Settings.AsNoTracking()
                .AnyAsync(e => e.Name == SourceRepository.ApiKeySettingName(seeded.Id)));

            var resolved = host.Services.GetRequiredService<ResolvedSourceConfiguration>();
            Assert.Equal(EnvironmentBaseUrl, resolved.BaseUrl);
            Assert.True(resolved.IsConfigured);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// With rows already present, the environment is not consulted at all: the database value wins
    /// even though a *different* environment value is set. This is the "DB is authoritative, env is
    /// inert" half of the ruling.
    /// </summary>
    [Fact]
    public async Task Existing_rows_win_and_the_environment_is_not_consulted()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using (var seedHost = CreateHost(configDirectory, withEnvironmentVariables: false))
            {
                using var seedClient = seedHost.CreateClient();
                using var scope = seedHost.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
                dbContext.Sources.Add(new Source
                {
                    Kind = SourceSeeder.NzbHydraKind,
                    DisplayName = "Configured In Database",
                    BaseUrl = DatabaseBaseUrl,
                    Enabled = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            // Start again with a *divergent* environment: a different base URL and name.
            await using var host = CreateHost(
                configDirectory,
                withEnvironmentVariables: true,
                baseUrl: EnvironmentBaseUrl,
                sourceName: "Configured In Environment");
            using var client = host.CreateClient();

            var resolved = host.Services.GetRequiredService<ResolvedSourceConfiguration>();
            Assert.Equal(DatabaseBaseUrl, resolved.BaseUrl);
            Assert.Equal("Configured In Database", resolved.SourceName);

            // And the environment was not merged in as an extra row.
            using var assertScope = host.Services.CreateScope();
            var assertContext = assertScope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            var row = Assert.Single(await assertContext.Sources.AsNoTracking().ToListAsync());
            Assert.Equal(DatabaseBaseUrl, row.BaseUrl);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// A second start never re-seeds and never overwrites: the row written by the first run's seed
    /// survives a restart byte-for-byte, even though the environment is still present and would seed
    /// the same values again if the empty-table guard were wrong.
    /// </summary>
    [Fact]
    public async Task A_second_start_does_not_re_seed_or_overwrite()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            long seededId;
            DateTimeOffset seededUpdatedAt;

            await using (var firstRun = CreateHost(configDirectory, withEnvironmentVariables: true))
            {
                using var client = firstRun.CreateClient();
                using var scope = firstRun.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
                var seeded = Assert.Single(await dbContext.Sources.AsNoTracking().ToListAsync());
                seededId = seeded.Id;
                seededUpdatedAt = seeded.UpdatedAt;
            }

            // Second start, same /config volume, same environment still set.
            await using var secondRun = CreateHost(configDirectory, withEnvironmentVariables: true);
            using var secondClient = secondRun.CreateClient();

            using var assertScope = secondRun.Services.CreateScope();
            var assertContext = assertScope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            var rows = await assertContext.Sources.AsNoTracking().ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal(seededId, row.Id);
            Assert.Equal(seededUpdatedAt, row.UpdatedAt);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// A deployment with an empty table and no environment configuration at all seeds nothing — it
    /// does not manufacture a source from compiled-in defaults, which would then be authoritative
    /// forever. It still boots and still serves; it simply reports nothing configured.
    /// </summary>
    [Fact]
    public async Task An_empty_table_with_no_environment_configuration_seeds_nothing()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using var host = CreateHost(configDirectory, withEnvironmentVariables: false);
            using var client = host.CreateClient();

            Assert.False(await ReadNzbHydraConfiguredAsync(client));

            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            Assert.Empty(await dbContext.Sources.AsNoTracking().ToListAsync());
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// A disabled source is not resolved — disabling a source in the database takes it out of the
    /// read path without deleting the row (and therefore without ever re-opening the seed path).
    /// </summary>
    [Fact]
    public async Task A_disabled_source_is_not_resolved()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using (var seedHost = CreateHost(configDirectory, withEnvironmentVariables: false))
            {
                using var seedClient = seedHost.CreateClient();
                using var scope = seedHost.Services.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
                dbContext.Sources.Add(new Source
                {
                    Kind = SourceSeeder.NzbHydraKind,
                    DisplayName = "Disabled Source",
                    BaseUrl = DatabaseBaseUrl,
                    Enabled = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
                await dbContext.SaveChangesAsync();
            }

            await using var host = CreateHost(configDirectory, withEnvironmentVariables: false);
            using var client = host.CreateClient();

            Assert.False(await ReadNzbHydraConfiguredAsync(client));

            // The row is still there — disabled, not deleted, so the table is never empty again and
            // the seed path stays closed.
            using var scope2 = host.Services.CreateScope();
            var assertContext = scope2.ServiceProvider.GetRequiredService<Arbitarr.Data.ArbitarrDbContext>();
            Assert.Single(await assertContext.Sources.AsNoTracking().ToListAsync());
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }
}
