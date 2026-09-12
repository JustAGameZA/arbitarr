using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-q75n, recommend-only from the #220 security review: honouring
/// <c>Arbitarr:ReleaseGuidSecret</c> from configuration was silent, so a value copy-pasted across
/// installs (sharing one instance's proxy-guid HMAC key with another) never surfaced anywhere an
/// operator would see it. Program.cs now logs a constant-string Warning naming only the key when
/// the override is honoured outside Development. No environment gate on the override itself (owner
/// decision pending) and no entropy/all-zero check (explicitly rejected) — this only pins the log.
///
/// <para><b>Positive control (CLAUDE.md §4).</b> "The captured logs do not contain the configured
/// value" passes just as happily if nothing was ever captured. <see
/// cref="NonDevelopment_WithOverride_WarnsWithKeyNameButNotValue"/> therefore asserts FIRST that the
/// capture is non-empty and carries the key name — proving the sink saw the intended message — and
/// only THEN that the planted secret value is absent from every captured line.</para>
/// </summary>
public sealed class ReleaseGuidSecretOverrideWarningTests
{
    // Valid base64 decoding to exactly 32 bytes, the supported shape (see
    // ReleaseGuidSecretValidationTests), so the override is honoured rather than rejected. The
    // plaintext is a readable, distinctive marker rather than key-shaped, so the pre-commit secret
    // guard has nothing to flag and its presence in a captured log line could only mean it leaked.
    private const string ThirtyTwoByteSecret = "YXJiLXE3NW4tb3ZlcnJpZGUtc2VjcmV0LTMyYnl0ZXMhISE=";

    private static WebApplicationFactory<Program> CreateHost(
        string configDirectory, List<string> logSink, string? environment = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);
            builder.UseSetting("Arbitarr:ReleaseGuidSecret", ThirtyTwoByteSecret);

            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logSink)));
        });
    }

    private static List<string> Snapshot(List<string> sink)
    {
        lock (sink)
        {
            return [.. sink];
        }
    }

    private static string NewConfigDirectory() =>
        Path.Combine(Path.GetTempPath(), "arbitarr-q75n-releaseguid-warning", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string configDirectory)
    {
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

    [Fact]
    public async Task NonDevelopment_WithOverride_WarnsWithKeyNameButNotValue()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            await using var host = CreateHost(configDirectory, logs, environment: "Staging");
            // CreateClient forces the host to actually start (and thus log); the client itself is
            // otherwise unused, so do not remove this as apparently-dead code.
            using var client = host.CreateClient();

            var snapshot = Snapshot(logs);

            // Positive control FIRST: prove the warning was actually produced and captured, so the
            // absence assertion below is evidence of scrubbing rather than of nothing happening.
            Assert.Contains(snapshot, line => line.Contains("Arbitarr:ReleaseGuidSecret", StringComparison.Ordinal));

            Assert.All(snapshot, line => Assert.DoesNotContain(ThirtyTwoByteSecret, line, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// Positive control for the capture mechanism itself (CLAUDE.md §4): proves the sink would have
    /// seen the secret had it leaked, by deliberately logging a message containing it and asserting
    /// the capture caught that. Without this, the absence assertion above could pass merely because
    /// the sink never captures the secret's exact byte-for-byte form.
    /// </summary>
    [Fact]
    public void CapturingLoggerProvider_CapturesAPlantedSecret()
    {
        var logs = new List<string>();
        var provider = new CapturingLoggerProvider(logs);
        var logger = provider.CreateLogger("test");

        logger.LogWarning("deliberately leaking control value: {Secret}", ThirtyTwoByteSecret);

        Assert.Contains(logs, line => line.Contains(ThirtyTwoByteSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Development_WithOverride_DoesNotWarn()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            // Explicit rather than relying on WebApplicationFactory's default, so a process-wide
            // ASPNETCORE_ENVIRONMENT cannot silently change which branch this test exercises.
            await using var host = CreateHost(configDirectory, logs, environment: "Development");
            // CreateClient forces the host to actually start (and thus log); the client itself is
            // otherwise unused, so do not remove this as apparently-dead code.
            using var client = host.CreateClient();

            var snapshot = Snapshot(logs);

            // Positive control FIRST (CLAUDE.md §4): prove the sink actually saw the host's
            // startup logging, so the absence assertion below is evidence the warning was
            // suppressed rather than evidence the sink never received anything at all.
            Assert.NotEmpty(snapshot);

            Assert.DoesNotContain(snapshot, line => line.Contains("Arbitarr:ReleaseGuidSecret", StringComparison.Ordinal));
            Assert.All(snapshot, line => Assert.DoesNotContain(ThirtyTwoByteSecret, line, StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(configDirectory);
        }
    }

    /// <summary>
    /// Captures information-and-above messages so a test can assert what the host told the
    /// operator, and so a clean run's non-empty capture is itself proof the sink is wired up
    /// (CLAUDE.md §4) rather than merely proof nothing was logged at Warning or above.
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

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

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
