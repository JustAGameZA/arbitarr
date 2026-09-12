using Arbitarr.Ai;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static WebApplicationFactory<Program> CreateHost(
        string configDirectory, string? baseUrl, string? keepAlive = null, List<string>? logSink = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);

            if (baseUrl is not null)
            {
                builder.UseSetting("Arbitarr:Ai:Ollama:BaseUrl", baseUrl);
            }

            if (keepAlive is not null)
            {
                builder.UseSetting("Arbitarr:Ai:Ollama:KeepAlive", keepAlive);
            }

            if (logSink is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logSink)));
            }
        });
    }

    private static string NewConfigDirectory() =>
        Path.Combine(Path.GetTempPath(), "arbitarr-6u6-ollama-options", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Scoped to the databases the host created under THIS test's own config directory rather than
    /// ClearAllPools(), which would also close pooled connections belonging to test classes running
    /// in parallel (arb-rga.3). The host owns these files (it was given the directory via
    /// <c>Arbitarr:ConfigDir</c> and has been disposed by now), so they are NOT wrapped in a
    /// <c>SqliteTestDatabase</c> — that fixture is only for a database the test itself opens.
    ///
    /// <para>This used to clear only <c>SqlitePools.ClearPoolsForDirectory</c> and then swallow the
    /// failure in an empty <c>catch (IOException)</c> — half the teardown and no report, so the
    /// delete lost to a pooled handle on the main database every time and nothing said so (16
    /// directories per run, arb-gphi). <see cref="ConfigDirectoryTeardown"/> does both halves and
    /// THROWS.</para>
    /// </summary>
    private static void Cleanup(string configDirectory) =>
        ConfigDirectoryTeardown.Delete(configDirectory);

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

    /// <summary>
    /// An <c>Arbitarr:Ai:Ollama:KeepAlive</c> value that is neither a bare integer nor a
    /// unit-bearing Go duration string must not be used verbatim — it falls back to the built-in
    /// default ("-1") rather than being carried through to <see cref="OllamaClient"/>, which would
    /// otherwise reproduce the "-1x"/"-1"-as-string 400 this bead fixes.
    /// </summary>
    [Fact]
    public async Task A_malformed_keep_alive_falls_back_to_the_default()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using var host = CreateHost(configDirectory, baseUrl: null, keepAlive: "-1x");
            using var client = host.CreateClient();

            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal("-1", options.KeepAlive.Text);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// Positive control for the fallback above: a well-formed unit-bearing duration is kept
    /// verbatim, so the malformed-value test is a rejection of bad input, not a blanket override.
    /// </summary>
    [Fact]
    public async Task A_valid_keep_alive_duration_is_kept_verbatim()
    {
        var configDirectory = NewConfigDirectory();

        try
        {
            await using var host = CreateHost(configDirectory, baseUrl: null, keepAlive: "-1m");
            using var client = host.CreateClient();

            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal("-1m", options.KeepAlive.Text);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// arb-43b, closing the gap the bead named: the fallback test above asserts only the resulting
    /// VALUE, which would still pass if the host silently swallowed a bad value and told nobody.
    /// This asserts the operator is actually warned.
    /// </summary>
    [Fact]
    public async Task A_malformed_keep_alive_logs_a_warning()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, baseUrl: null, keepAlive: "-1x", logSink: logs);
            using var client = host.CreateClient();

            // Resolving the singleton is what runs the factory that validates and logs.
            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal("-1", options.KeepAlive.Text);

            Assert.Contains(logs, line => line.Contains("KeepAlive", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// POSITIVE CONTROL for the assertion above (CLAUDE.md section 4). "No warning was logged" is
    /// vacuous unless a warning would have been CAPTURED had one been logged — the test above proves
    /// the sink catches it, and this one proves a valid value produces nothing, so the two together
    /// show the warning tracks the input rather than always or never firing.
    /// </summary>
    [Fact]
    public async Task A_valid_keep_alive_logs_no_warning()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, baseUrl: null, keepAlive: "-1m", logSink: logs);
            using var client = host.CreateClient();

            var options = host.Services.GetRequiredService<OllamaOptions>();
            Assert.Equal("-1m", options.KeepAlive.Text);

            Assert.DoesNotContain(logs, line => line.Contains("KeepAlive", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// Captures warning-and-above messages so a test can assert what the host told the operator.
    /// Deliberately minimal: it records rendered text only, because that is what an operator reads.
    /// </summary>
    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                ArgumentNullException.ThrowIfNull(formatter);
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }
}
