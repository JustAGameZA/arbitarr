using Arbitarr.Integration.Tests.TestSupport;
using Arbitarr.Media.Providers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-5uw review fixup: <c>Arbitarr:AnimeLists:SourceUrl</c> is OPERATOR CONFIGURATION, so the
/// registration in <c>Program.cs</c> must not build its <see cref="Uri"/> with <c>new Uri(...)</c>.
/// </summary>
/// <remarks>
/// <para><b>WHY THIS FILE EXISTS.</b> The first revision of that registration did exactly that, and
/// a malformed value therefore threw <see cref="UriFormatException"/> during service registration —
/// before <c>Build()</c>, so the host did not boot at all. That is strictly worse than the setting
/// being ignored: this tier is OPTIONAL and its inactive state is fully supported, so a typo in it
/// taking the whole process down trades a disabled fallback for a dead install. The repository
/// already has this rule and this shape, under arb-6u6 for the Ollama startup-fallback client
/// (<see cref="OllamaOptionsStartupTests"/>, <c>Program.cs</c> ~:412) — this is the same fix applied
/// at the second site that needed it.</para>
///
/// <para><b>THE VALUE IS NEVER LOGGED, AND THAT IS A SECURITY PROPERTY RATHER THAN TIDINESS.</b> One
/// of the three rejection causes is a userinfo-bearing URL (<c>user:pw@host</c>). The registration's
/// own comment justifies omitting <c>.RemoveAllLoggers()</c> on the ground that this URI carries no
/// credential; echoing a rejected value into the warning would falsify that premise by writing the
/// credential into the persistent log store served at <c>GET /api/admin/logs</c>, which neither
/// .NET's query-string redaction nor <c>LogMessageCleanser</c> would scrub (CLAUDE.md §1). Each
/// rejection fact below therefore asserts the key name IS present and the value is NOT.</para>
///
/// <para><b>NON-VACUITY.</b> "The value is absent from the log" passes trivially if nothing was
/// logged, and "IsConfigured is false" passes trivially if the key is never read at all. Both are
/// closed by <see cref="A_well_formed_source_url_configures_the_tier_and_logs_no_warning"/>, which
/// proves the key IS read and honoured and that the sink catches nothing when it should not — so a
/// false from the rejection facts is evidence of rejection rather than evidence of a key nobody
/// looks at.</para>
/// </remarks>
public sealed class AnimeListsSourceUrlValidationTests
{
    /// <summary>The configuration key under test. Asserted present in every rejection warning.</summary>
    private const string SourceUrlKey = "Arbitarr:AnimeLists:SourceUrl";

    /// <summary>
    /// A documentation-only <c>.example</c> host, never a real one: arb-5uw leaves the choice of
    /// upstream to the operator, so no anime-lists host is named anywhere in this repository.
    /// </summary>
    private const string WellFormedSourceUrl = "https://anime-lists.example/anime-list-full.xml";

    private static WebApplicationFactory<Program> CreateHost(
        string configDirectory,
        string? sourceUrl,
        List<string>? logSink = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);

            if (sourceUrl is not null)
            {
                builder.UseSetting(SourceUrlKey, sourceUrl);
            }

            if (logSink is not null)
            {
                builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logSink)));
            }
        });
    }

    private static string NewConfigDirectory() =>
        Path.Combine(Path.GetTempPath(), "arbitarr-5uw-animelists-url", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Scoped to the databases the host created under THIS test's own config directory rather than
    /// ClearAllPools(), which would also close pooled connections belonging to test classes running
    /// in parallel (arb-rga.3). Same teardown the arb-6u6 tests use, and it THROWS rather than
    /// swallowing an IOException (arb-gphi).
    /// </summary>
    private static void Cleanup(string configDirectory) =>
        ConfigDirectoryTeardown.Delete(configDirectory);

    /// <summary>
    /// THE POSITIVE CONTROL, and the regression's other half: a well-formed value is READ and
    /// HONOURED, so the tier really does activate. Without this, every "IsConfigured is false"
    /// assertion below would pass just as happily against a registration that ignored the key
    /// entirely, and every "no value in the log" assertion would pass against a silent host.
    /// </summary>
    [Fact]
    public async Task A_well_formed_source_url_configures_the_tier_and_logs_no_warning()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, WellFormedSourceUrl, logs);
            using var client = host.CreateClient();

            var provider = host.Services.GetRequiredService<AnimeListsProvider>();

            Assert.True(provider.IsConfigured);
            Assert.Equal(
                new Uri(WellFormedSourceUrl),
                host.Services.GetRequiredService<AnimeListsProviderOptions>().SourceUrl);

            // The sink demonstrably catches this warning in the rejection facts below, so its
            // silence here means no warning was raised rather than that nothing is being captured.
            Assert.DoesNotContain(logs, line => line.Contains(SourceUrlKey, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// THE REGRESSION TEST: a value that is not a parseable absolute URL must not throw during
    /// service registration. Pre-fix, <c>CreateClient()</c> below — which forces host startup — threw
    /// a <see cref="UriFormatException"/> out of the <c>AnimeListsProviderOptions</c> construction
    /// and the host never booted.
    /// </summary>
    [Theory]
    [InlineData("not-a-url")]                                   // unparseable, no host to fetch from
    [InlineData("/anime-list-full.xml")]                        // relative, same problem
    [InlineData("ftp://anime-lists.example/anime-list-full.xml")] // SEC-M1: not an http(s) fetch target
    [InlineData("file:///etc/passwd")]                          // ditto, and the reason scheme is checked
    public async Task A_malformed_or_non_http_source_url_leaves_the_tier_inactive_without_crashing_startup(
        string sourceUrl)
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, sourceUrl, logs);
            using var client = host.CreateClient();

            var provider = host.Services.GetRequiredService<AnimeListsProvider>();

            // Inactive is a SUPPORTED state, so this is a degradation rather than a failure.
            Assert.False(provider.IsConfigured);
            Assert.Null(host.Services.GetRequiredService<AnimeListsProviderOptions>().SourceUrl);

            AssertWarnedWithoutEchoingTheValue(logs, sourceUrl);
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// A userinfo-bearing URL is refused, and refusing it is what keeps the registration's
    /// "no credential in this URI, so no .RemoveAllLoggers() needed" comment TRUE. Accepted, the
    /// credential would ride in every logged request URI into <c>GET /api/admin/logs</c>.
    /// </summary>
    /// <remarks>
    /// The planted credential here doubles as the leak probe for the assertion helper: it is a
    /// distinctive string, so if the warning ever echoed the rejected value this test would see it.
    /// </remarks>
    [Fact]
    public async Task A_source_url_carrying_credentials_is_refused_and_the_credential_is_never_logged()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();
        const string credentialBearingUrl =
            "https://planteduser:plantedsecret0123456789@anime-lists.example/anime-list-full.xml";

        try
        {
            await using var host = CreateHost(configDirectory, credentialBearingUrl, logs);
            using var client = host.CreateClient();

            Assert.False(host.Services.GetRequiredService<AnimeListsProvider>().IsConfigured);

            AssertWarnedWithoutEchoingTheValue(logs, credentialBearingUrl);

            // The credential's parts, not just the whole URL: a warning that echoed only the host
            // and userinfo would still leak the secret while passing a whole-string comparison.
            Assert.DoesNotContain(logs, line => line.Contains("plantedsecret0123456789", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("planteduser", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// The UNSET case keeps its Information line rather than a warning: an operator who never
    /// configured the tier has not made a mistake, and warning them would train them to ignore the
    /// warning that means something. The sink captures warning-and-above only, so its silence here
    /// is the assertion — and the rejection facts above prove it is not silent for a real warning.
    /// </summary>
    [Fact]
    public async Task An_unset_source_url_is_not_warned_about()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, sourceUrl: null, logSink: logs);
            using var client = host.CreateClient();

            Assert.False(host.Services.GetRequiredService<AnimeListsProvider>().IsConfigured);
            Assert.DoesNotContain(logs, line => line.Contains(SourceUrlKey, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// The operator is told WHICH key was refused — and is not shown the value, which may be the
    /// credential-bearing string the check just rejected.
    /// </summary>
    private static void AssertWarnedWithoutEchoingTheValue(List<string> logs, string rejectedValue)
    {
        Assert.Contains(logs, line => line.Contains(SourceUrlKey, StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains(rejectedValue, StringComparison.Ordinal));
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
