using Arbitarr.Data.Logging;
using Arbitarr.Host.Logging;
using Arbitarr.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-1of: the SQLite log store used to receive every framework Information line, so the rows an
/// operator opened the Logs tab for were buried under ASP.NET Core request pairs.
/// <see cref="LoggingSetup.AddArbitarrSqliteLogging"/> demotes <c>Microsoft.*</c> to Warning FOR
/// THAT PROVIDER ONLY, and deliberately leaves <c>System.Net.Http.HttpClient.*</c> alone — see the
/// remarks on <c>NoisyFrameworkCategories</c> for why those rows must keep reaching the store.
///
/// <para>
/// These drive the real registration method rather than grepping <c>Program.cs</c> (which is what
/// <c>ProgramClassifierPollingWorkerTests</c> and <c>ProgramOllamaHttpClientTests</c> must do for the
/// top-level statements). That distinction is the point: a source-level assertion can only show a
/// filter line is PRESENT, never that it DROPS anything, and "the filter is wired up" passing while
/// the filter does nothing is exactly the vacuous shape CLAUDE.md §4 warns about.
/// </para>
/// </summary>
public sealed class LoggingSetupTests : IDisposable
{
    private readonly string _directory;

    public LoggingSetupTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arbitarr-1of-logging-setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        // Scoped to this class's own directory rather than ClearAllPools(), which would also close
        // pooled connections belonging to test classes running in parallel (arb-rga.3).
        SqlitePools.ClearPoolsForDirectory(_directory);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LogStore NewStore()
    {
        var store = new LogStore(Path.Combine(_directory, LogStore.DatabaseFileName));
        store.EnsureCreated();
        return store;
    }

    /// <summary>
    /// Builds a factory through the same method <c>Program.cs</c> calls, logs one line per category
    /// under test, and returns the messages that actually reached the store.
    /// </summary>
    private async Task<IReadOnlyList<string>> CaptureStoredMessagesAsync(Action<ILoggerFactory> log)
    {
        var store = NewStore();

        // Resolved from a real container rather than read off the ServiceDescriptor (arb-2t9u).
        // AddArbitarrSqliteLogging now registers a FACTORY, so there is no ImplementationInstance to
        // pick up, and invoking the descriptor's factory by hand would construct a SECOND provider —
        // one with its own pump, writing to the same store, and not the one the ILoggerFactory
        // actually logs through. Asking the container for the singleton gets the same instance the
        // factory below uses, which is what makes the flush deterministic.
        await using var services = new ServiceCollection()
            .AddLogging(builder =>
            {
                // The host's own floor. Without it the default is Information anyway, but stating it
                // means this test keeps testing the FILTER rather than silently starting to test a
                // default if that default ever changes.
                builder.SetMinimumLevel(LogLevel.Trace);
                LoggingSetup.AddArbitarrSqliteLogging(builder, store);
            })
            .BuildServiceProvider();

        var provider = services.GetServices<ILoggerProvider>().OfType<SqliteLoggerProvider>().Single();

        log(services.GetRequiredService<ILoggerFactory>());

        await provider.FlushAsync();

        var page = await store.ReadAsync(null, null, 1, LogStore.MaxPageSize);
        return page.Entries.Select(entry => entry.Message).ToList();
    }

    [Fact]
    public async Task Aspnet_information_is_dropped_while_warning_application_and_httpclient_rows_are_kept()
    {
        const string FrameworkInformation = "framework-information-should-be-dropped";
        const string FrameworkWarning = "framework-warning-should-be-kept";
        const string HttpClientInformation = "httpclient-information-should-be-kept";
        const string ApplicationInformation = "application-information-should-be-kept";

        var stored = await CaptureStoredMessagesAsync(factory =>
        {
            var hosting = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            hosting.LogInformation(FrameworkInformation);
            hosting.LogWarning(FrameworkWarning);

            factory.CreateLogger("System.Net.Http.HttpClient.upstream.LogicalHandler")
                .LogInformation(HttpClientInformation);

            factory.CreateLogger("Arbitarr.Something").LogInformation(ApplicationInformation);
        });

        // POSITIVE CONTROLS FIRST (CLAUDE.md §4). "The noisy line is absent" is vacuous unless a
        // line that SHOULD survive demonstrably does — an empty store contains nothing and would
        // pass the DoesNotContain below. These prove the pipeline delivered to the store at all,
        // so the absence that follows is the filter's doing rather than a broken fixture.
        Assert.Contains(FrameworkWarning, stored);
        Assert.Contains(ApplicationInformation, stored);

        // HttpClient Information is KEPT, asserted rather than merely left untested. These rows are
        // the positive-control surface for UpstreamApiKeyLogRedactionTests and
        // SonarrKeyIsScrubbedFromLogsTests; filtering the category empties their collections and
        // SILENCES those controls instead of failing them loudly. This assertion makes a future
        // attempt to add the category fail HERE, next to the explanation in LoggingSetup, rather
        // than in two secrets tests whose connection to this file is not obvious.
        Assert.Contains(HttpClientInformation, stored);

        // Asserted per row rather than as one compound condition, so a single category regressing
        // fails on its own line.
        Assert.DoesNotContain(FrameworkInformation, stored);

        // Exactly the three kept lines and nothing else, so a future filter that accidentally lets
        // a further category through is caught rather than passing the assertions above.
        Assert.Equal(3, stored.Count);
    }

    /// <summary>
    /// The filter is registered for <see cref="SqliteLoggerProvider"/> specifically, so a provider
    /// of any other type must still see the framework Information lines. That is what keeps
    /// <c>docker logs</c> the raw, unfiltered view the #65 comment in <c>Program.cs</c> requires —
    /// and it is the half that would silently break if the filter were ever added without the
    /// generic type argument.
    /// </summary>
    [Fact]
    public async Task The_filter_is_scoped_to_the_sqlite_provider_and_leaves_other_providers_alone()
    {
        const string FrameworkInformation = "framework-information-seen-by-other-providers";

        var store = NewStore();
        var otherProviderSaw = new List<string>();

        using (var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            LoggingSetup.AddArbitarrSqliteLogging(builder, store);

            // Stands in for AddConsole(): a different provider type, added alongside, with no
            // filter of its own.
            builder.AddProvider(new CapturingLoggerProvider(otherProviderSaw));
        }))
        {
            factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation(FrameworkInformation);
        }

        Assert.Contains(FrameworkInformation, otherProviderSaw);
    }

    /// <summary>
    /// arb-2t9u, THE PROPERTY: a line logged while the host is running is IN THE STORE once the host
    /// has stopped and been disposed — with nothing in the test disposing the provider by hand.
    ///
    /// <para><b>Why this is the right assertion.</b> The sink never writes on the caller's thread
    /// (see <c>SqliteLoggerProvider</c>'s remarks, AC5); a line only reaches the database when the
    /// pump drains, and the pump's FINAL drain only runs once it observes cancellation, which only
    /// happens when something calls <c>Dispose</c>. So "the line is in the store after shutdown" is
    /// true exactly when the container owns and disposes the provider — which is the defect, stated
    /// as a behaviour rather than as a fact about a registration call. An assertion on the
    /// ServiceDescriptor's shape would pass just as well against a registration that was a factory
    /// AND still never disposed.</para>
    ///
    /// <para><b>The line is logged and the host stopped INSIDE the flush interval</b> — no
    /// <c>FlushAsync</c>, no waiting out <c>SqliteLoggerProvider.FlushInterval</c>, no sleep. A
    /// periodic drain landing the row anyway would make this pass in the broken world too, so the
    /// test must reach the store only via the shutdown path. That is also why it is fast rather than
    /// despite it.</para>
    ///
    /// <para><b>NON-VACUITY / POSITIVE CONTROL (CLAUDE.md §4).</b>
    /// <see cref="A_line_logged_at_the_last_moment_is_LOST_when_the_provider_is_registered_as_an_instance"/>
    /// runs the identical host, timing and assertion against the registration shape this bead
    /// replaced, and finds the store EMPTY. Without it, "the row is present" would pass just as
    /// happily if the row had arrived by some route other than the drain — and an empty store would
    /// have been indistinguishable from a broken fixture. The control drives
    /// <c>ILoggingBuilder.AddProvider(ILoggerProvider)</c>, a framework API, so the old shape is
    /// reproduced without a test-only seam in production code and without leaving the vulnerable
    /// registration anywhere in the tree.</para>
    ///
    /// <para><b>MUTATION-PROVED</b> (CLAUDE.md §4) outside the repository, in a throwaway console
    /// project holding both registration shapes side by side; nothing was mutated in this worktree
    /// and nothing is left behind. A probe provider registered via <c>AddProvider(instance)</c>
    /// reported <c>Disposed = false</c> after a full <c>StartAsync</c>/<c>StopAsync</c>/<c>Dispose</c>
    /// cycle, and the same provider registered via
    /// <c>Services.AddSingleton&lt;ILoggerProvider&gt;(factory)</c> reported <c>true</c> — the
    /// ownership difference the fix turns on, measured rather than assumed.</para>
    /// </summary>
    [Fact]
    public async Task A_line_logged_at_the_last_moment_reaches_the_store_when_the_host_stops()
    {
        const string ShutdownLine = "the-line-that-must-survive-shutdown";

        var store = NewStore();

        using (var host = BuildHost(builder => LoggingSetup.AddArbitarrSqliteLogging(builder, store)))
        {
            await host.StartAsync();
            host.Services.GetRequiredService<ILogger<LoggingSetupTests>>().LogInformation(ShutdownLine);
            await host.StopAsync();
        }

        var stored = (await store.ReadAsync(null, null, 1, LogStore.MaxPageSize))
            .Entries.Select(entry => entry.Message).ToList();

        Assert.Contains(ShutdownLine, stored);
    }

    /// <summary>
    /// The positive control for the test above (CLAUDE.md §4): the SAME host, the SAME timing and
    /// the SAME assertion, differing only in that the provider is handed over as an
    /// already-constructed INSTANCE — the registration arb-2t9u replaced.
    ///
    /// <para><c>ILoggingBuilder.AddProvider(ILoggerProvider)</c> registers a constant singleton, and
    /// the container does not dispose an instance it did not create, so nothing cancels the pump,
    /// the final drain never runs and the line is still sitting in the in-memory queue when the
    /// process would have exited. Asserting that here is what proves the test above is detecting the
    /// drain rather than reporting a row that would have arrived regardless.</para>
    ///
    /// <para>The provider is disposed explicitly at the end, since nothing else will — the very
    /// defect under test — and leaving a live pump holding a pooled handle on the log database would
    /// lose a race with this class's directory teardown.</para>
    /// </summary>
    [Fact]
    public async Task A_line_logged_at_the_last_moment_is_LOST_when_the_provider_is_registered_as_an_instance()
    {
        const string ShutdownLine = "the-line-that-must-survive-shutdown";

        var store = NewStore();
        var undisposed = new SqliteLoggerProvider(store, LogLevel.Information);

        try
        {
            using (var host = BuildHost(builder => builder.AddProvider(undisposed)))
            {
                await host.StartAsync();
                host.Services.GetRequiredService<ILogger<LoggingSetupTests>>().LogInformation(ShutdownLine);
                await host.StopAsync();
            }

            var stored = (await store.ReadAsync(null, null, 1, LogStore.MaxPageSize))
                .Entries.Select(entry => entry.Message).ToList();

            Assert.DoesNotContain(ShutdownLine, stored);
        }
        finally
        {
            undisposed.Dispose();
        }
    }

    /// <summary>
    /// A real <see cref="IHost"/> with only the logging under test configured. Console and the
    /// default providers are cleared so the only thing that can put a row in the store is the
    /// registration <paramref name="configureLogging"/> adds.
    /// </summary>
    private static IHost BuildHost(Action<ILoggingBuilder> configureLogging)
    {
        // Fully qualified: the enclosing namespace is Arbitarr.Host, so a bare Host binds to that
        // namespace rather than to Microsoft.Extensions.Hosting.Host.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        configureLogging(builder.Logging);
        return builder.Build();
    }

    private sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(sink);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> sink) : ILogger
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
                ArgumentNullException.ThrowIfNull(formatter);
                lock (sink)
                {
                    sink.Add(formatter(state, exception));
                }
            }
        }
    }
}
