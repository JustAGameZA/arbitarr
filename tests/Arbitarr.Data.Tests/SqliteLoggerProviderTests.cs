using System.Diagnostics;
using Arbitarr.Data.Logging;
using Arbitarr.TestSupport;
using Microsoft.Data.Sqlite;
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
        // Scoped to the log databases under THIS class's own temp directory rather than
        // ClearAllPools(), which would also close pooled connections belonging to test classes
        // running in parallel (arb-rga.3). LogStore's own connection string (Mode/Cache/Pooling)
        // is what keys its pool, and ClearPoolsForDirectory covers that shape.
        SqlitePools.ClearPoolsForDirectory(_directory);
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

    // Category=Timing (arb-rga.5) on this fact ALONE, not on the class. It is the only one of this
    // class's nine facts that asserts elapsed wall time (the 1,000-line loop under a 2s stopwatch
    // below). The other eight are functional — including
    // Messages_are_cleansed_before_they_are_stored, a secrets-adjacent assertion that must keep
    // running on every PR — so a class-level trait would pull real coverage out of the merge path
    // to buy back time this one fact does not cost.
    [Trait("Category", "Timing")]
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

    /// <summary>
    /// arb-s3ky, THE PROPERTY: <see cref="SqliteLoggerProvider.DrainCompleted"/> reports when the
    /// pump has actually FINISHED, which is a strictly later moment than <c>Dispose</c> returning.
    /// Dispose's wait is bounded and gives up on purpose; a caller that can afford to wait longer — a
    /// test teardown about to delete the directory the log database lives in — needs the stronger
    /// signal, because a pool clear closes only the handles a pool HOLDS and cannot reach one a
    /// still-running drain has checked out.
    ///
    /// <para><b>MUTANT KILLED:</b> <c>DrainCompleted =&gt; Task.CompletedTask</c>. The first
    /// assertion below fails against it immediately — a completed task is complete the moment Dispose
    /// returns, which is precisely the guarantee this member exists NOT to give.</para>
    ///
    /// <para><b>The wait is DRIVEN, not slept.</b> The hold is a real SQLite write lock taken by this
    /// test on the log database, so the pump's final <c>WriteAsync</c> genuinely blocks inside
    /// <c>BEGIN</c> rather than the test approximating a busy pump with a timer. The provider's
    /// shutdown bound is shortened through its test-only constructor (see that overload's remarks)
    /// purely so the give-up happens in milliseconds; <c>DefaultShutdownWait</c>, and therefore
    /// production shutdown, is untouched.</para>
    /// </summary>
    [Fact]
    public async Task DrainCompleted_outlives_a_Dispose_that_gave_up_and_completes_when_the_drain_is_released()
    {
        var store = NewStore();
        var holder = OpenExclusiveWriter(store);

        SqliteLoggerProvider provider;
        try
        {
            // A short shutdown bound, so Dispose is certain to give up while the drain below is
            // still blocked on the lock held above — which it will be for as long as the hold lasts,
            // the store's busy_timeout notwithstanding (see OpenExclusiveWriter).
            provider = new SqliteLoggerProvider(
                store,
                LogLevel.Information,
                timeProvider: null,
                onError: null,
                shutdownWait: TimeSpan.FromMilliseconds(200));

            // A queued entry is what makes the final drain WRITE rather than return early on an empty
            // batch — without it the drain never touches the lock and this would prove nothing about
            // a held drain.
            provider.CreateLogger("Arbitarr.Api.Search").LogInformation("held behind a write lock");

            provider.Dispose();

            // THE ASSERTION THE MUTANT FAILS. Dispose has returned, but the pump is still inside its
            // final write, so the completion must NOT be signalled yet.
            Assert.False(
                provider.DrainCompleted.IsCompleted,
                "DrainCompleted was already complete when Dispose returned from a drain it had " +
                "abandoned. It must report the pump FINISHING, which is later than Dispose giving up.");
        }
        finally
        {
            // Releasing the hold lets the blocked drain finish.
            ReleaseExclusiveWriter(holder);
        }

        // ... and the completion then publishes. Bounded so a regression fails the test rather than
        // hanging the run.
        await provider.DrainCompleted.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// arb-s3ky: the NON-VACUITY control for the test above. That test asserts DrainCompleted is NOT
    /// complete just after Dispose — an assertion that would pass just as happily if the pump had
    /// never run, or if the entry never reached a write at all. This proves the drain was genuinely
    /// in flight and blocked BY THE HOLD: the row it was carrying reaches the store once released,
    /// which an un-started pump could not produce.
    /// </summary>
    [Fact]
    public async Task A_drain_released_after_Dispose_gave_up_still_writes_its_batch()
    {
        var store = NewStore();
        var holder = OpenExclusiveWriter(store);

        SqliteLoggerProvider provider;
        try
        {
            provider = new SqliteLoggerProvider(
                store,
                LogLevel.Information,
                timeProvider: null,
                onError: null,
                shutdownWait: TimeSpan.FromMilliseconds(200));

            provider.CreateLogger("Arbitarr.Api.Search").LogInformation("written after the hold released");
            provider.Dispose();
        }
        finally
        {
            ReleaseExclusiveWriter(holder);
        }

        await provider.DrainCompleted.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(
            (await store.ReadAsync(null, null, 1, 10)).Entries,
            e => e.Message == "written after the hold released");
    }

    /// <summary>
    /// arb-s3ky: on the ORDINARY path — nothing holding the database — the completion is already
    /// published by the time <c>Dispose</c> returns, so a caller awaiting it pays nothing.
    ///
    /// <para><b>MUTANT KILLED:</b> publishing the completion somewhere that does not run on the
    /// normal path (for instance only in the catch of Dispose's <c>AggregateException</c>, which the
    /// pump never raises). This fails against that; the held-drain test above does not, because there
    /// the completion arrives late either way.</para>
    /// </summary>
    [Fact]
    public void A_normal_Dispose_publishes_the_drain_completion_before_it_returns()
    {
        var store = NewStore();
        var provider = new SqliteLoggerProvider(store);
        provider.CreateLogger("Arbitarr.Api.Search").LogInformation("a line to drain");

        provider.Dispose();

        Assert.True(
            provider.DrainCompleted.IsCompletedSuccessfully,
            "Dispose returned without the pump's drain completion being published, even though " +
            "nothing was holding the log database. A caller awaiting it would then be waiting for a " +
            "pump that had already finished.");
    }

    /// <summary>
    /// arb-s3ky: <c>Dispose</c> is safe to call twice. xunit disposes some hosts through both
    /// disposal paths, and a second call must not throw out of a teardown nor unpublish the
    /// completion.
    ///
    /// <para><b>MUTANT KILLED:</b> removing the disposed latch, which leaves the second call invoking
    /// <c>Cancel()</c> on an already-disposed <c>CancellationTokenSource</c>.</para>
    /// </summary>
    [Fact]
    public void Disposing_twice_does_not_throw_and_leaves_the_completion_published()
    {
        var store = NewStore();
        var provider = new SqliteLoggerProvider(store);
        provider.CreateLogger("Arbitarr.Api.Search").LogInformation("a line to drain");

        provider.Dispose();

        Assert.Null(Record.Exception(provider.Dispose));
        Assert.True(provider.DrainCompleted.IsCompletedSuccessfully);
    }

    /// <summary>
    /// An open <c>BEGIN IMMEDIATE</c> on the store's own database. Any other writer — here the
    /// provider's final drain — blocks on it, and goes on blocking for as long as this is held: the
    /// store's <c>busy_timeout</c> does NOT time the waiter out, which was measured rather than
    /// assumed. That is what makes the hold a controllable gate rather than a race.
    ///
    /// <para>The connection string is built the way <c>LogStore</c> builds its own, because a
    /// different string names a different pool.</para>
    ///
    /// <para><b>Release is <see cref="ReleaseExclusiveWriter"/>, never a bare <c>Dispose</c>.</b>
    /// Disposing a POOLED connection with a transaction still open returns it to the pool with the
    /// transaction — and therefore the write lock — intact, so the blocked writer stays blocked
    /// forever. Measured: a bare Dispose left the drain wedged past 30 seconds. The explicit
    /// <c>ROLLBACK</c> is what actually releases it.</para>
    /// </summary>
    private static SqliteConnection OpenExclusiveWriter(LogStore store)
    {
        var holder = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
        }.ToString());
        holder.Open();

        using var begin = holder.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();

        return holder;
    }

    /// <summary>Releases <see cref="OpenExclusiveWriter"/>'s lock — see its remarks for why the
    /// explicit rollback is required and a Dispose alone is not.</summary>
    private static void ReleaseExclusiveWriter(SqliteConnection holder)
    {
        using (var rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        holder.Dispose();
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
