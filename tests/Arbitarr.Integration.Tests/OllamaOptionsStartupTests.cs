using Arbitarr.Ai;
using Arbitarr.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-6u6: the <see cref="OllamaOptions"/> startup-fallback singleton in <c>Program.cs</c> used to
/// build its <see cref="Uri"/> straight from the raw <c>Arbitarr:Ai:Ollama:BaseUrl</c> environment
/// value with no validation, so a malformed value threw at host startup — before
/// <c>OllamaBaseUrlSeeder</c> (which already validates the same value for the persisted row) ever
/// got a chance to run. This pins that the host still boots, and that it falls back to the same
/// compiled-in default the seeder uses, exactly like <c>OllamaBaseUrlSeederTests</c> pins for the
/// seeded row.
/// </summary>
public sealed class OllamaOptionsStartupTests
{
    // Not an absolute http(s) URL: fails SettingsValidator.ValidateOllamaBaseUrl the same way it
    // would fail on PUT /api/admin/ai/ollama, and pre-fix this is exactly the shape that made
    // `new Uri(baseUrlRaw)` throw during service registration.
    private const string MalformedBaseUrl = "not-a-url";

    private static WebApplicationFactory<Program> CreateHost(string configDirectory, string? baseUrl)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);

            if (baseUrl is not null)
            {
                builder.UseSetting("Arbitarr:Ai:Ollama:BaseUrl", baseUrl);
            }
        });
    }

    private static string NewConfigDirectory() =>
        Path.Combine(Path.GetTempPath(), "arbitarr-6u6-ollama-options", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string configDirectory)
    {
        // Scoped to the databases the host created under THIS test's own config directory rather
        // than ClearAllPools(), which would also close pooled connections belonging to test classes
        // running in parallel (arb-rga.3). The host owns these files (it was given the directory
        // via Arbitarr:ConfigDir and has been disposed by now), so they are NOT wrapped in a
        // SqliteTestDatabase — that fixture is only for a database the test itself opens.
        SqlitePools.ClearPoolsForDirectory(configDirectory);
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

    /// <summary>
    /// THE regression test: a malformed <c>Arbitarr:Ai:Ollama:BaseUrl</c> must not throw during
    /// service registration. Pre-fix, <c>CreateClient()</c> below (which forces host startup) threw
    /// a <see cref="UriFormatException"/> out of the <c>OllamaOptions</c> singleton factory.
    /// </summary>
    [Fact]
    public async Task A_malformed_environment_base_url_does_not_throw_at_startup()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using var host = CreateHost(configDirectory, MalformedBaseUrl);
            using var client = host.CreateClient();

            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal(
                Arbitarr.Data.Settings.OllamaBaseUrlResolver.DefaultBaseUrl,
                options.BaseUrl.ToString().TrimEnd('/'));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// Positive control: a well-formed environment value is still honoured by the startup-fallback
    /// singleton, so the fix above is a rejection of bad input, not a blanket override to the default.
    /// </summary>
    [Fact]
    public async Task A_valid_environment_base_url_is_still_used()
    {
        var configDirectory = NewConfigDirectory();
        const string validBaseUrl = "http://192.0.2.50:11434";

        try
        {
            await using var host = CreateHost(configDirectory, validBaseUrl);
            using var client = host.CreateClient();

            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal(validBaseUrl, options.BaseUrl.ToString().TrimEnd('/'));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }
}
