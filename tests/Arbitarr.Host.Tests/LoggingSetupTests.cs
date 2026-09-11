using Arbitarr.Data.Logging;
using Arbitarr.Host.Logging;
using Arbitarr.TestSupport;
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
        SqliteLoggerProvider? provider = null;

        using (var factory = LoggerFactory.Create(builder =>
        {
            // The host's own floor. Without it LoggerFactory.Create defaults to Information anyway,
            // but stating it means this test keeps testing the FILTER rather than silently starting
            // to test a default if that default ever changes.
            builder.SetMinimumLevel(LogLevel.Trace);
            LoggingSetup.AddArbitarrSqliteLogging(builder, store);

            // The provider instance the method created, so the test can flush it deterministically
            // rather than waiting out SqliteLoggerProvider.FlushInterval on every assertion.
            provider = builder.Services
                .Select(descriptor => descriptor.ImplementationInstance)
                .OfType<SqliteLoggerProvider>()
                .Single();
        }))
        {
            log(factory);
        }

        Assert.NotNull(provider);
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
