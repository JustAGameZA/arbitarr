using Arbitarr.Integration.Tests.TestSupport;
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

    /// <summary>
    /// Builds a host this class OWNS over a caller-supplied config directory (arb-yt7j), returning
    /// the root factory so the caller can await its <c>DisposeAsync</c> before deleting.
    ///
    /// <para>It previously derived from a bare <c>new WebApplicationFactory&lt;Program&gt;()</c> via
    /// <c>WithWebHostBuilder</c>. Such a derived host is owned by the ROOT factory, and the root was
    /// never disposed at all here — so the host outlived the test, and <see cref="Cleanup"/> deleted
    /// the config directory out from under it while it still held the database open. That is why
    /// the delete failed, and the empty <c>catch (IOException)</c> is why it failed in silence:
    /// measured, this class leaked 2 config directories per run before this change and leaks none
    /// after.</para>
    ///
    /// <para><see cref="ArbitarrWebApplicationFactory.OverConfigDirectory"/> is the
    /// caller-supplied-directory host and is deliberately NON-OWNING: it stops the host and clears
    /// the pools on disposal but does NOT delete the directory, so there is no double delete with
    /// this class's own <see cref="Cleanup"/>. The per-test directory and per-test host stay as they
    /// were — what changes is that the host is now drained before the directory is removed.</para>
    ///
    /// <para>The two <c>UseSetting</c> calls below still win over the ones
    /// <c>ArbitarrWebApplicationFactory</c> applies in its own <c>ConfigureWebHost</c>, because
    /// <c>WithWebHostBuilder</c>'s configuration runs after it. That matters for
    /// <c>Arbitarr:ReleaseGuidSecret</c> specifically: this class pins a KNOWN value in order to
    /// assert it never reaches the logs, so the factory's directory-derived default must not be the
    /// one in force.</para>
    /// </summary>
    /// <para><b>Both factories are returned, and disposing the ROOT is what drains the host</b> —
    /// the same asymmetry <c>CategoryParamCapTests</c> documents. <c>WithWebHostBuilder</c> hands
    /// back a DERIVED factory that the caller must use to create its client (that is the one
    /// carrying the log capture), while the running host belongs to the root. Awaiting the root's
    /// <c>DisposeAsync</c> stops the host and clears the pools; the derived factory needs no
    /// separate disposal.</para>
    private static (ArbitarrWebApplicationFactory Root, WebApplicationFactory<Program> Host) CreateHost(
        string configDirectory, List<string> logSink, string? environment = null)
    {
        var root = ArbitarrWebApplicationFactory.OverConfigDirectory(configDirectory);

        var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", configDirectory);
            builder.UseSetting("Arbitarr:ReleaseGuidSecret", ThirtyTwoByteSecret);

            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(logSink)));
        });

        return (root, host);
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

    /// <summary>
    /// Removes a per-test config directory, FAILING if it cannot (arb-yt7j).
    ///
    /// <para>This replaces a <c>Directory.Delete</c> inside an empty <c>catch (IOException)</c> with
    /// no pool clear — the shape CLAUDE.md §4 names, where the cleanup stops working and the run
    /// stays green anyway. <see cref="TestSupport.ConfigDirectoryTeardown.Delete"/> supplies the
    /// half that was missing (the pool clear, without which the delete cannot win against a pooled
    /// handle's share lock on Windows) and throws on failure instead of swallowing. Throwing is
    /// correct here because this class owns the directory, so the failure lands on the test
    /// responsible for it.</para>
    ///
    /// <para>The caller must have awaited the root factory's <c>DisposeAsync</c> FIRST: the clear
    /// closes handles the pool HOLDS, and a host that is still running has not returned them.</para>
    /// </summary>
    private static void Cleanup(string configDirectory) =>
        ConfigDirectoryTeardown.Delete(configDirectory);

    [Fact]
    public async Task NonDevelopment_WithOverride_WarnsWithKeyNameButNotValue()
    {
        var configDirectory = NewConfigDirectory();
        var logs = new List<string>();

        try
        {
            // The ROOT is what gets disposed (it owns the running host); the derived factory is what
            // creates the client. See CreateHost for why they are separate.
            var (root, host) = CreateHost(configDirectory, logs, environment: "Staging");
            await using var owned = root;
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
            var (root, host) = CreateHost(configDirectory, logs, environment: "Development");
            await using var owned = root;
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
