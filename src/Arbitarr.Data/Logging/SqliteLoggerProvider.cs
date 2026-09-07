using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Arbitarr.Data.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that persists log entries to <see cref="LogStore"/> so they can
/// be read back from the System page's Logs tab (#65).
///
/// THE CENTRAL REQUIREMENT IS THAT THIS CANNOT SLOW DOWN OR BREAK A REQUEST (#65 AC5). A log call
/// happens on the request thread, on the D1-critical search path; a synchronous SQLite insert there
/// would put an fsync in the middle of a search, and a failing insert would throw out of
/// <c>ILogger.Log</c> and take the request with it. So:
///
///   - <see cref="SqliteLogger.Log"/> only enqueues, in memory, and returns. It never touches the
///     database, never waits on a lock, and never throws.
///   - A single background pump drains the queue and writes it in batches. Sonarr's equivalent is
///     NLog's AsyncWrapper with a 500 ms batch interval; <see cref="FlushInterval"/> matches it.
///   - The queue is BOUNDED (<see cref="MaxQueueLength"/>). If the writer stalls — a locked file, a
///     full disk — entries are dropped rather than accumulating until the process runs out of
///     memory. Losing log lines during a disk failure is an acceptable outcome; turning a disk
///     failure into an OOM kill of the search service is not. Dropping is counted and reported once
///     the pump recovers, so the gap is visible rather than silent.
///   - Every exception inside the pump is swallowed after being surfaced to <see cref="_onError"/>.
///     A sink that throws while recording a failure escalates that failure instead of recording it.
/// </summary>
public sealed class SqliteLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// How long the pump waits between flushes. Matches the 500 ms batch interval Sonarr's NLog
    /// AsyncWrapper uses — long enough that a burst of log lines becomes one transaction rather
    /// than dozens, short enough that a line is readable in the UI about as fast as an operator can
    /// switch tabs after reproducing something.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The bound on the in-memory queue. Roughly a few seconds of a pathological log storm; past
    /// this the sink sheds load rather than growing without limit. See the class remarks.
    /// </summary>
    public const int MaxQueueLength = 10_000;

    /// <summary>
    /// Categories are stored with this prefix stripped, matching Sonarr's handling of its own
    /// <c>NzbDrone.</c> prefix. Every Arbitarr logger category starts with it, so keeping it would
    /// cost horizontal space in the UI's Logger column to say the same word on every single row.
    /// </summary>
    private const string CategoryPrefix = "Arbitarr.";

    private readonly LogStore _store;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Exception>? _onError;
    private readonly ConcurrentQueue<PendingLogEntry> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;

    private int _queueLength;
    private int _droppedSinceLastWrite;

    public SqliteLoggerProvider(
        LogStore store,
        LogLevel minimumLevel = LogLevel.Information,
        TimeProvider? timeProvider = null,
        Action<Exception>? onError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onError = onError;
        _pump = Task.Run(PumpAsync);
    }

    public ILogger CreateLogger(string categoryName) => new SqliteLogger(this, StripPrefix(categoryName));

    /// <summary>
    /// Flushes anything still queued. Exposed for tests, which cannot wait out
    /// <see cref="FlushInterval"/> on every assertion without making the suite slow and timing-flaky.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => DrainAsync(cancellationToken);

    public void Dispose()
    {
        _shutdown.Cancel();

        try
        {
            // Wait for the pump to observe cancellation and drain what it has. Bounded so a wedged
            // writer cannot hang process shutdown — losing the last few log lines beats a container
            // that will not stop.
            _pump.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The pump never surfaces exceptions (see PumpAsync); this is belt-and-braces so
            // Dispose itself cannot throw during host shutdown.
        }

        _shutdown.Dispose();
    }

    private static string StripPrefix(string categoryName) =>
        categoryName.StartsWith(CategoryPrefix, StringComparison.Ordinal)
            ? categoryName[CategoryPrefix.Length..]
            : categoryName;

    private bool IsEnabled(LogLevel logLevel) => logLevel >= _minimumLevel && logLevel != LogLevel.None;

    private void Enqueue(PendingLogEntry entry)
    {
        // Interlocked rather than reading _queue.Count: ConcurrentQueue's Count walks the segments
        // and would be O(n) on the request path, which is exactly the cost this sink exists to avoid.
        if (Interlocked.Increment(ref _queueLength) > MaxQueueLength)
        {
            Interlocked.Decrement(ref _queueLength);
            Interlocked.Increment(ref _droppedSinceLastWrite);
            return;
        }

        _queue.Enqueue(entry);
    }

    private async Task PumpAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(FlushInterval, _timeProvider, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // Final drain on shutdown, so the lines explaining why the process is stopping are not the
        // ones that get lost. CancellationToken.None deliberately: _shutdown is already cancelled.
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        var batch = new List<PendingLogEntry>();
        while (_queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _queueLength);
            batch.Add(entry);
        }

        var dropped = Interlocked.Exchange(ref _droppedSinceLastWrite, 0);
        if (dropped > 0)
        {
            // Recorded as a row rather than only a counter: an operator reading the Logs tab needs
            // to see that there is a hole in the history, at the point in time where it happened.
            batch.Add(new PendingLogEntry(
                _timeProvider.GetUtcNow(),
                LogLevel.Warning.ToString(),
                "Data.Logging.SqliteLoggerProvider",
                $"Dropped {dropped} log entries: the log write queue was full. " +
                "Older entries are unaffected; this gap is in the most recent entries only.",
                Exception: null,
                ExceptionType: null));
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await _store.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose. This runs on a background pump with nothing to propagate to,
            // and the one thing a log sink must never do is convert "could not record a problem"
            // into a new, louder problem. The batch is discarded rather than retried: retrying a
            // write that failed because the disk is full simply fails again while the queue behind
            // it grows.
            _onError?.Invoke(ex);
        }
    }

    /// <summary>
    /// The per-category <see cref="ILogger"/>. Deliberately does no work beyond formatting and
    /// enqueuing — see the provider's class remarks for why nothing here may touch the database.
    /// </summary>
    private sealed class SqliteLogger : ILogger
    {
        private readonly SqliteLoggerProvider _provider;
        private readonly string _category;

        public SqliteLogger(SqliteLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

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

            try
            {
                var message = formatter(state, exception);

                _provider.Enqueue(new PendingLogEntry(
                    Time: _provider._timeProvider.GetUtcNow(),
                    Level: logLevel.ToString(),
                    Logger: _category,
                    Message: message,
                    Exception: exception?.ToString(),
                    ExceptionType: exception?.GetType().FullName));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A caller's own formatter delegate can throw. Letting that escape would fail the
                // REQUEST that logged, turning a bad log call into a user-visible 500 — the exact
                // coupling this sink is built to avoid.
                _provider._onError?.Invoke(ex);
            }
        }
    }
}
