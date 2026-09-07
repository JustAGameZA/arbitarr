using System.Diagnostics;
using Arbitarr.Data.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #65 plan §5: the sink cannot block — a slow or failing log write must never fail or delay the
/// caller. That is the acceptance criterion these tests exist for (AC5), and it is why the provider
/// enqueues rather than writing inline.
/// </summary>
public sealed class SqliteLoggerProviderTests : IDisposable
{
    private readonly string _directory;

    public SqliteLoggerProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arbitarr-log-provider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LogStore NewStore(string name = LogStore.DatabaseFileName)
    {
        var store = new LogStore(Path.Combine(_directory, name));
        store.EnsureCreated();
        return store;
    }

    [Fact]
    public async Task Logged_entries_reach_the_store()
    {
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);

        provider.CreateLogger("Arbitarr.Api.Search").LogInformation("a search happened");
        await provider.FlushAsync();

        var entry = Assert.Single((await store.ReadAsync(null, null, 1, 10)).Entries);
        Assert.Equal("a search happened", entry.Message);
        Assert.Equal("Information", entry.Level);
    }

    [Fact]
    public async Task The_Arbitarr_category_prefix_is_stripped()
    {
        // Matches Sonarr stripping its own "NzbDrone." prefix: every category starts with it, so
        // keeping it spends horizontal space in the UI's Logger column saying the same word twice.
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);

        provider.CreateLogger("Arbitarr.Api.Search").LogInformation("x");
        provider.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("y");
        await provider.FlushAsync();

        var loggers = await store.GetLoggersAsync();
        Assert.Contains("Api.Search", loggers);
        Assert.Contains("Microsoft.Hosting.Lifetime", loggers);
    }

    [Fact]
    public async Task Entries_below_the_minimum_level_are_not_stored()
    {
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store, LogLevel.Information);
        var logger = provider.CreateLogger("Arbitarr.Api.Search");

        logger.LogDebug("chatter");
        logger.LogTrace("more chatter");
        logger.LogInformation("kept");
        logger.LogWarning("also kept");
        await provider.FlushAsync();

        var page = await store.ReadAsync(null, null, 1, 10);
        Assert.Equal(2, page.Total);
        Assert.DoesNotContain(page.Entries, e => e.Message == "chatter");
    }

    [Fact]
    public async Task Exceptions_are_recorded_with_their_type()
    {
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);

        provider.CreateLogger("Arbitarr.Api.Search")
            .LogError(new InvalidOperationException("boom"), "the thing failed");
        await provider.FlushAsync();

        var entry = Assert.Single((await store.ReadAsync(null, null, 1, 10)).Entries);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
        Assert.Contains("boom", entry.Exception);
    }

    [Fact]
    public void Logging_does_not_block_the_caller()
    {
        // AC5. The log call happens on the request thread; if it did the SQLite insert inline, this
        // loop would pay an fsync per line. Enqueuing makes it memory-speed, and this asserts the
        // call path stays in that regime rather than merely "works".
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);
        var logger = provider.CreateLogger("Arbitarr.Api.Search");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 1_000; i++)
        {
            logger.LogInformation("line {Index}", i);
        }

        stopwatch.Stop();

        // Deliberately loose: this is a regression guard against reintroducing a synchronous write
        // (which would be seconds for 1,000 fsyncs), not a benchmark. A tight bound here would be
        // flaky on a loaded CI box and would get "fixed" by loosening it, defeating the point.
        Assert.True(
            stopwatch.ElapsedMilliseconds < 2_000,
            $"Enqueuing 1,000 log entries took {stopwatch.ElapsedMilliseconds}ms. The sink must not " +
            "write synchronously on the caller's thread — see SqliteLoggerProvider's remarks (AC5).");
    }

    [Fact]
    public void A_failing_log_write_never_throws_to_the_caller()
    {
        // AC5's other half. The store here points at a path that cannot be opened, so every flush
        // fails. The caller must not see any of it: a sink that throws while recording a failure
        // escalates that failure instead of recording it.
        var unopenable = new LogStore(Path.Combine(_directory, "no-such-directory", "logs.db"));
        var errors = new List<Exception>();
        using var provider = new SqliteLoggerProvider(unopenable, LogLevel.Information, onError: errors.Add);
        var logger = provider.CreateLogger("Arbitarr.Api.Search");

        var exception = Record.Exception(() =>
        {
            logger.LogInformation("this write will fail");
            provider.FlushAsync().GetAwaiter().GetResult();
        });

        Assert.Null(exception);
        // The failure is surfaced through onError rather than swallowed invisibly.
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void A_throwing_formatter_never_reaches_the_caller()
    {
        // A caller's own message formatter can throw. Letting that escape ILogger.Log would fail
        // the REQUEST that logged, turning a bad log call into a user-visible 500.
        var store = NewStore();
        var errors = new List<Exception>();
        using var provider = new SqliteLoggerProvider(store, LogLevel.Information, onError: errors.Add);
        var logger = provider.CreateLogger("Arbitarr.Api.Search");

        var exception = Record.Exception(() => logger.Log<object?>(
            LogLevel.Information,
            new EventId(1),
            state: null,
            exception: null,
            formatter: (_, _) => throw new InvalidOperationException("bad formatter")));

        Assert.Null(exception);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task A_full_queue_drops_entries_rather_than_growing_without_bound()
    {
        // Shedding load beats an OOM kill of the search service: losing log lines during a disk
        // failure is acceptable, turning that disk failure into a dead container is not. The drop
        // must be VISIBLE, though — a silent hole in the history is worse than a noted one.
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);
        var logger = provider.CreateLogger("Arbitarr.Api.Search");

        for (var i = 0; i < SqliteLoggerProvider.MaxQueueLength + 500; i++)
        {
            logger.LogInformation("line {Index}", i);
        }

        await provider.FlushAsync();

        var page = await store.ReadAsync(null, null, 1, LogStore.MaxPageSize);
        Assert.True(page.Total <= SqliteLoggerProvider.MaxQueueLength + 1);
        Assert.Contains(
            (await store.ReadAsync("Warning", null, 1, 10)).Entries,
            e => e.Message.Contains("Dropped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Messages_are_cleansed_before_they_are_stored()
    {
        // The cleanser runs at the store's single choke point, so it applies however the entry got
        // there — a call site that forgets is exactly what this layer is for.
        var store = NewStore();
        using var provider = new SqliteLoggerProvider(store);

        provider.CreateLogger("Arbitarr.Api.Search")
            .LogWarning("upstream call to http://192.0.2.10/api?apikey=placeholder-redacted-value failed");
        await provider.FlushAsync();

        var entry = Assert.Single((await store.ReadAsync(null, null, 1, 10)).Entries);
        Assert.DoesNotContain("placeholder-redacted-value", entry.Message);
    }
}
