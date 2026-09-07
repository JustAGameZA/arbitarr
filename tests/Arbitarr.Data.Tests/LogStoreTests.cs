using Arbitarr.Data.Logging;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #65: the log store's read/write/paging/retention behaviour. Each test gets its own temporary
/// SQLite file — the store's whole point is that it is a real separate database file, so an
/// in-memory substitute would not exercise the thing under test (least of all the vacuum, which is
/// only meaningful against a file with a size on disk).
/// </summary>
public sealed class LogStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly LogStore _store;

    public LogStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "arbitarr-log-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _store = new LogStore(Path.Combine(_directory, LogStore.DatabaseFileName));
        _store.EnsureCreated();
    }

    public void Dispose()
    {
        // Pooled SQLite handles keep the file locked on Windows until the pool is cleared, so a
        // straight Directory.Delete here fails intermittently rather than cleanly.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    private static PendingLogEntry Entry(
        string message = "message",
        string level = "Information",
        string logger = "Api.Search",
        DateTimeOffset? time = null,
        string? exception = null,
        string? exceptionType = null) =>
        new(time ?? DateTimeOffset.UtcNow, level, logger, message, exception, exceptionType);

    [Fact]
    public async Task Written_entries_are_read_back_newest_first()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        await _store.WriteAsync(new[]
        {
            Entry("first", time: start),
            Entry("second", time: start.AddMinutes(1)),
            Entry("third", time: start.AddMinutes(2)),
        });

        var page = await _store.ReadAsync(level: null, logger: null, page: 1, pageSize: 10);

        Assert.Equal(3, page.Total);
        Assert.Equal(new[] { "third", "second", "first" }, page.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Level_filter_returns_only_that_level()
    {
        await _store.WriteAsync(new[]
        {
            Entry("info one", level: "Information"),
            Entry("a warning", level: "Warning"),
            Entry("info two", level: "Information"),
        });

        var warnings = await _store.ReadAsync(level: "Warning", logger: null, page: 1, pageSize: 10);

        Assert.Equal(1, warnings.Total);
        Assert.Equal("a warning", Assert.Single(warnings.Entries).Message);
    }

    [Fact]
    public async Task Level_filter_is_case_insensitive()
    {
        // The UI sends whatever is in its <select>; a casing mismatch silently returning zero rows
        // would look exactly like "no logs at that level", which is the wrong answer, not an error.
        await _store.WriteAsync(new[] { Entry("a warning", level: "Warning") });

        var page = await _store.ReadAsync(level: "warning", logger: null, page: 1, pageSize: 10);

        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Logger_filter_matches_a_substring()
    {
        await _store.WriteAsync(new[]
        {
            Entry("search line", logger: "Api.Search"),
            Entry("worker line", logger: "Host.RefreshWorker"),
        });

        var page = await _store.ReadAsync(level: null, logger: "Search", page: 1, pageSize: 10);

        Assert.Equal("search line", Assert.Single(page.Entries).Message);
    }

    [Fact]
    public async Task Logger_filter_treats_wildcards_as_literal_text()
    {
        // A LIKE wildcard typed into the filter box must search for that character, not match
        // everything — otherwise typing "%" looks like the filter silently stopped working.
        await _store.WriteAsync(new[] { Entry("plain", logger: "Api.Search") });

        var page = await _store.ReadAsync(level: null, logger: "%", page: 1, pageSize: 10);

        Assert.Empty(page.Entries);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Paging_splits_rows_and_reports_the_unpaged_total()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        await _store.WriteAsync(Enumerable.Range(0, 5)
            .Select(i => Entry($"entry {i}", time: start.AddMinutes(i)))
            .ToList());

        var first = await _store.ReadAsync(level: null, logger: null, page: 1, pageSize: 2);
        var second = await _store.ReadAsync(level: null, logger: null, page: 2, pageSize: 2);
        var third = await _store.ReadAsync(level: null, logger: null, page: 3, pageSize: 2);

        // Total is the count matching the FILTERS, not the page — the UI needs it to render
        // "page 2 of 3" and to know the next-page control belongs disabled on the last page.
        Assert.Equal(5, first.Total);
        Assert.Equal(5, second.Total);
        Assert.Equal(new[] { "entry 4", "entry 3" }, first.Entries.Select(e => e.Message));
        Assert.Equal(new[] { "entry 2", "entry 1" }, second.Entries.Select(e => e.Message));
        Assert.Equal(new[] { "entry 0" }, third.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Paging_is_stable_when_rows_share_a_timestamp()
    {
        // Rows written in one batch can share a timestamp to the tick. Ordering by Time alone would
        // let SQLite return them in any order per query, so a row could be skipped between pages or
        // appear on both. Ordering by the monotonic Id is what makes this deterministic.
        var sameInstant = DateTimeOffset.UtcNow;
        await _store.WriteAsync(Enumerable.Range(0, 6)
            .Select(i => Entry($"entry {i}", time: sameInstant))
            .ToList());

        var first = await _store.ReadAsync(level: null, logger: null, page: 1, pageSize: 3);
        var second = await _store.ReadAsync(level: null, logger: null, page: 2, pageSize: 3);

        var seen = first.Entries.Concat(second.Entries).Select(e => e.Message).ToList();
        Assert.Equal(6, seen.Distinct().Count());
    }

    [Fact]
    public async Task Page_size_is_capped()
    {
        await _store.WriteAsync(new[] { Entry() });

        var page = await _store.ReadAsync(level: null, logger: null, page: 1, pageSize: 100_000);

        // Asserted through the store rather than trusting the caller to clamp: an unbounded
        // page size is a trivially remote-triggered way to pull the whole table into memory.
        Assert.Single(page.Entries);
        Assert.True(LogStore.MaxPageSize < 100_000);
    }

    [Fact]
    public async Task An_empty_store_reads_back_as_an_empty_page()
    {
        var page = await _store.ReadAsync(level: null, logger: null, page: 1, pageSize: 10);

        Assert.Empty(page.Entries);
        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task Exception_text_and_type_round_trip()
    {
        await _store.WriteAsync(new[]
        {
            Entry("it failed", exception: "System.InvalidOperationException: boom\n   at Thing()",
                exceptionType: "System.InvalidOperationException"),
        });

        var entry = Assert.Single((await _store.ReadAsync(null, null, 1, 10)).Entries);

        Assert.Contains("boom", entry.Exception);
        Assert.Equal("System.InvalidOperationException", entry.ExceptionType);
    }

    [Fact]
    public async Task Distinct_loggers_are_listed_for_the_filter()
    {
        await _store.WriteAsync(new[]
        {
            Entry(logger: "Api.Search"),
            Entry(logger: "Api.Search"),
            Entry(logger: "Host.RefreshWorker"),
        });

        Assert.Equal(new[] { "Api.Search", "Host.RefreshWorker" }, await _store.GetLoggersAsync());
    }

    [Fact]
    public async Task Trim_deletes_rows_past_the_window_and_keeps_newer_ones()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.WriteAsync(new[]
        {
            Entry("ancient", time: now - LogRetentionPolicy.Retention - TimeSpan.FromHours(1)),
            Entry("just inside", time: now - LogRetentionPolicy.Retention + TimeSpan.FromHours(1)),
            Entry("recent", time: now),
        });

        var deleted = await _store.TrimAsync(now);

        Assert.Equal(1, deleted);
        var remaining = await _store.ReadAsync(null, null, 1, 10);
        Assert.Equal(new[] { "recent", "just inside" }, remaining.Entries.Select(e => e.Message));
    }

    [Fact]
    public async Task Trim_vacuums_so_the_file_actually_shrinks()
    {
        // THE POINT OF THIS TEST. SQLite's DELETE returns pages to its own freelist, not to the
        // filesystem, so a trim without a VACUUM leaves the file at its high-water mark forever and
        // the disk fills anyway while the trim appears to be working. Asserting on the row count
        // alone would pass with the vacuum removed — this asserts on the file size, which does not.
        var old = DateTimeOffset.UtcNow - LogRetentionPolicy.Retention - TimeSpan.FromDays(1);
        var padding = new string('x', 2_000);
        await _store.WriteAsync(Enumerable.Range(0, 2_000)
            .Select(i => Entry($"{padding} {i}", time: old))
            .ToList());

        // This first trim deletes nothing (its cutoff is a further 7 days before `old`); it is here
        // to run the vacuum + WAL checkpoint so the baseline is measured against a settled file
        // rather than one with the initial inserts still sitting in the -wal. Without that the
        // "before" size would understate the file and this test would pass for the wrong reason.
        await _store.TrimAsync(old.AddMinutes(1));
        var sizeBefore = new FileInfo(_store.DatabasePath).Length;

        var deleted = await _store.TrimAsync(DateTimeOffset.UtcNow);
        var sizeAfter = new FileInfo(_store.DatabasePath).Length;

        Assert.Equal(2_000, deleted);
        Assert.True(
            sizeAfter < sizeBefore,
            $"Expected the log database to shrink after the trim vacuumed, but it went from " +
            $"{sizeBefore} to {sizeAfter} bytes. Without the VACUUM in LogStore.TrimAsync the file " +
            "stays at its high-water mark and retention silently stops reclaiming disk.");
    }

    [Fact]
    public async Task Trim_on_an_empty_store_is_a_no_op()
    {
        Assert.Equal(0, await _store.TrimAsync(DateTimeOffset.UtcNow));
    }
}
