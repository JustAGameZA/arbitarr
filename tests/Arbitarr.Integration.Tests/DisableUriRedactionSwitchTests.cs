using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-u1c — pins the OTHER half of the "no <c>.RemoveAllLoggers()</c> on the Sonarr client" decision
/// in Program.cs: not just that the query string is redacted today, but WHY that redaction can be
/// switched off, and that this repository would then be relying on a false premise.
///
/// <para><b>The mechanism.</b> <c>IHttpClientFactory</c>'s own logging handler collapses an entire
/// query string to the literal <c>?*</c> before formatting the "Sending HTTP request" message — the
/// behaviour <c>SonarrKeyIsScrubbedFromLogsTests</c> measures and relies on. That collapsing is gated
/// by the <c>System.Net.Http.DisableUriRedaction</c> <see cref="AppContext"/> switch, read once from
/// the environment variable <c>DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION</c> (or a matching
/// <c>runtimeconfig.json</c> entry) at process start. The name is inverted from what it sounds like:
/// setting it disables the redaction, i.e. restores the FULL query string, key included, to the log
/// line. Nothing in this repository sets it — the default (redaction ON) is exactly what the Sonarr
/// registration's "NO .RemoveAllLoggers()" comment in Program.cs depends on. If any future dependency,
/// host configuration, or `runtimeconfig.json` entry ever set this switch for the process, the Sonarr
/// client would start logging its key in full and neither this repository's own code nor
/// <see cref="Arbitarr.Data.Logging.LogMessageCleanser"/> would catch it — the leak would be in
/// framework behaviour the cleanser never sees, exactly like the "query string vs path" gap CLAUDE.md
/// §1 already documents.</para>
///
/// <para><b>Why this drives two SEPARATE PROCESSES rather than flipping the switch in-process.</b>
/// The switch is read once and cached by the runtime the first time anything asks for it — flipping
/// <see cref="AppContext.SetSwitch"/> after that first read has no effect for the rest of the process,
/// which was measured directly here: an in-process version of this test passed or failed depending on
/// which scenario's <c>[Fact]</c> xunit happened to run first, i.e. it was measuring test order, not
/// the switch. The two <c>[Fact]</c>s below each launch a fresh <c>dotnet exec</c> child process of
/// this SAME test assembly with the environment variable set (or unset) BEFORE that process starts,
/// filtered to run only <see cref="RunSonarrLikeClientOnceAsync"/> in that child — which is what
/// actually issues the request and prints the one log line under test to stdout for the parent to
/// assert on. This is slower than an in-process flip, but it is the only shape that observes the real
/// effect rather than an artefact of read ordering.</para>
///
/// <para><b>Positive control, per CLAUDE.md §4.</b> The first fact (env var unset, i.e. this
/// repository's actual production state) reproduces the redaction <c>SonarrKeyIsScrubbedFromLogsTests</c>
/// already relies on. The second (env var set) is the mutation: same handler, same key, same URI
/// shape, in a fresh process — and the key now appears in full, proving the first result is a real
/// effect of the switch's default state rather than an artefact of this test's own plumbing.</para>
/// </summary>
public sealed class DisableUriRedactionSwitchTests
{
    private const string EnvVarName = "DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION";
    private const string SonarrKey = "placeholder-sonarr-key-4d2f9b11";
    private const string WorkerMethodName =
        "Arbitarr.Integration.Tests." +
        nameof(DisableUriRedactionSwitchTests) + "." +
        nameof(RunSonarrLikeClientOnceAsync);

    [Fact]
    public async Task Default_process_state_redacts_the_query_string_the_Sonarr_client_relies_on()
    {
        var message = await RunWorkerInChildProcessAsync(disableUriRedaction: null);

        // The Program.cs comment's claim, executable: with the switch unset (this repository's actual
        // state), the key never reaches the logged message at all — it is collapsed to the literal
        // "?*".
        Assert.Contains("system/status?*", message, StringComparison.Ordinal);
        Assert.DoesNotContain(SonarrKey, message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setting_the_env_var_defeats_the_redaction_the_Sonarr_registration_depends_on()
    {
        var message = await RunWorkerInChildProcessAsync(disableUriRedaction: true);

        // MUTATION EVIDENCE: same handler, same key, same URI shape, only the process-start
        // environment changed — and the key now rides in full through the exact log line the
        // Program.cs comment says never carries it. This is precisely what neither Arbitarr's own
        // code nor LogMessageCleanser would catch: the collapse (or its absence) happens inside the
        // framework's logging handler, before any Arbitarr code or the cleanser sees the string.
        Assert.Contains(SonarrKey, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Launches a child <c>dotnet exec</c> process of this same test assembly, filtered to run only
    /// <see cref="RunSonarrLikeClientOnceAsync"/>, with <see cref="EnvVarName"/> set (or left unset)
    /// before that process starts — the only point at which the switch's value can take effect.
    /// </summary>
    private static async Task<string> RunWorkerInChildProcessAsync(bool? disableUriRedaction)
    {
        var testAssemblyPath = Assembly.GetExecutingAssembly().Location;

        // Reuse the SDK's own `dotnet test` entry point against this assembly rather than depending on
        // a separately-restored runner: the child process just needs to execute one filtered [Fact]
        // and observe its Console.Out — the assertions on its result happen back in THIS process.
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "test",
                testAssemblyPath,
                "--filter",
                // A --filter that matches NOTHING still exits 0 -- vstest does not treat "ran zero
                // tests" as a failure. So if WorkerMethodName is ever wrong (a rename of
                // RunSonarrLikeClientOnceAsync that this constant is not updated to match), this
                // process would exit 0 having silently run nothing at all. The marker-line check
                // below (an InvalidOperationException when no "SONARR_LIKE_LOG_LINE::" line is found
                // on a zero exit code) is what actually catches that: a real run always prints it,
                // so its absence on a clean exit is the signal that the filter matched nothing.
                $"FullyQualifiedName={WorkerMethodName}",
                "--logger",
                "console;verbosity=detailed",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (disableUriRedaction is bool value)
        {
            startInfo.Environment[EnvVarName] = value ? "1" : "0";
        }
        else
        {
            startInfo.Environment.Remove(EnvVarName);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the child dotnet test process.");

        // CI HARDENING: a hung child must fail this test loudly rather than hang the CI job. One
        // filtered [Fact] against an already-built assembly, issuing one fake-handler request, has no
        // legitimate reason to take anywhere near this long — 60s is generous headroom, not a tuned
        // budget. On timeout, kill the whole process tree (not just the outer `dotnet` launcher) so no
        // orphaned testhost.exe survives the test run, then fail with a message that names what
        // happened rather than leaving a bare TaskCanceledException.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        var timeout = TimeSpan.FromSeconds(60);
        var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
        if (completed != exitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort: the process may have exited between the WhenAny race and this Kill.
            }

            throw new TimeoutException(
                $"Child dotnet test process (disableUriRedaction={disableUriRedaction}) did not exit " +
                $"within {timeout}. Killed the process tree; this must not be allowed to hang CI.");
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        // FAIL LOUDLY ON A NON-ZERO CHILD EXIT rather than only on a missing marker: a crash before
        // Console.WriteLine and a crash after both leave the marker absent, but naming the exit code
        // explicitly here means a CI failure reads as "the child test process failed" instead of a
        // more confusing "could not find expected output".
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Child dotnet test process (disableUriRedaction={disableUriRedaction}) exited with " +
                $"code {process.ExitCode}.\n--- stdout ---\n{stdOut}\n--- stderr ---\n{stdErr}");
        }

        const string marker = "SONARR_LIKE_LOG_LINE::";
        var markerLine = stdOut
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .FirstOrDefault(line => line.StartsWith(marker, StringComparison.Ordinal));

        if (markerLine is null)
        {
            throw new InvalidOperationException(
                "Child process exited 0 but did not print the expected marker line.\n" +
                $"--- stdout ---\n{stdOut}\n--- stderr ---\n{stdErr}");
        }

        return markerLine[marker.Length..];
    }

    /// <summary>
    /// THE WORKER. Builds the same registration shape as the Sonarr client in Program.cs (a named
    /// <see cref="IHttpClientFactory"/> client, no <c>.RemoveAllLoggers()</c>) against a fake handler
    /// that never touches the network, issues one request carrying <see cref="SonarrKey"/> in the
    /// query string, captures the resulting log message, and prints it to stdout behind a marker for
    /// the parent process to read back when run as the CHILD process the two <c>[Fact]</c>s above
    /// spawn.
    ///
    /// <para><b>This method ALSO runs once, unfiltered, as part of a normal full-suite invocation of
    /// this assembly</b> — xunit has no "child-only" concept, and the CI test-count ratchet counts
    /// every executed test, so this must never be <c>Skip</c>ped or made to assert nothing in that
    /// context. In the parent suite, <see cref="EnvVarName"/> is never set by anything in this
    /// process (only the two <c>[Fact]</c>s above set it, and only for the CHILD they spawn — never
    /// for themselves), so this method's own run always sees the DEFAULT state and its assertion below
    /// is the same one <see cref="Default_process_state_redacts_the_query_string_the_Sonarr_client_relies_on"/>
    /// makes: the key is redacted to <c>?*</c>. That means this method's body is exercised twice by a
    /// full CI run — once directly (asserting the default case) and once per child process spawned
    /// above (where its own assertion is this same one, and the PARENT re-checks the *marker line* for
    /// the specific scenario under test) — which is intentional, not a duplicate to remove.</para>
    /// </summary>
    [Fact]
    public async Task RunSonarrLikeClientOnceAsync()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(capture));

        services.AddHttpClient("sonarr-like")
            .ConfigurePrimaryHttpMessageHandler(() => new FakeHandler());

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("sonarr-like");

        var uri = new Uri($"http://192.0.2.80:8989/api/v3/system/status?apikey={Uri.EscapeDataString(SonarrKey)}");
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var message = await capture.WaitForMessageContainingAsync("Sending HTTP request", TimeSpan.FromSeconds(5));

        // ASSERT SOMETHING REAL IN BOTH CONTEXTS, not merely "a message was captured". When this
        // process's own environment does not have the switch's variable set -- true whenever this
        // runs directly in the parent suite, since only a CHILD process (spawned above) ever has it
        // set -- the key must be redacted exactly as SonarrKeyIsScrubbedFromLogsTests and the sibling
        // [Fact]s above rely on.
        if (Environment.GetEnvironmentVariable(EnvVarName) is null)
        {
            Assert.Contains("system/status?*", message, StringComparison.Ordinal);
            Assert.DoesNotContain(SonarrKey, message, StringComparison.OrdinalIgnoreCase);
        }

        // Printed for the PARENT process to read back when this runs as a spawned child; harmless
        // extra stdout when this runs directly in the parent suite.
        Console.WriteLine($"SONARR_LIKE_LOG_LINE::{message}");
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="ILoggerProvider"/>: this repository has no existing capture helper
    /// for plain <see cref="ILogger"/> output (the Sonarr log test above reads back through the real
    /// <c>LogStore</c>, which this test deliberately avoids to keep the switch's blast radius to one
    /// isolated, throwaway <see cref="IHttpClientFactory"/> in a disposable child process).
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public async Task<string> WaitForMessageContainingAsync(string fragment, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var match = _messages.FirstOrDefault(m => m.Contains(fragment, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException(
                $"No captured log message contained '{fragment}' within {timeout}. " +
                $"Captured: [{string.Join(" | ", _messages)}]");
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
