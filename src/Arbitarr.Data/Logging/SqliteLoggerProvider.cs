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
    /// How long <see cref="Dispose"/> waits for the pump's final drain before giving up. BOUNDED ON
    /// PURPOSE, and unchanged by arb-s3ky: an unbounded await here would trade a test-teardown race
    /// for a production hang — a wedged writer (a locked file, a full disk) would stop the container
    /// from stopping. Losing the last few log lines beats that. A caller that genuinely can afford to
    /// wait longer waits on <see cref="DrainCompleted"/> instead, which outlives this bound.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownWait = TimeSpan.FromSeconds(5);

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
    private readonly TimeSpan _shutdownWait;
    private readonly Func<string?, string?>? _cleanse;
    private readonly TaskCompletionSource _drainCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// arb-fjid: serialises <see cref="DrainAsync"/> against itself, so the pump's periodic drain and
    /// a caller's <see cref="FlushAsync"/> can never be in flight at the same time.
    ///
    /// <para><b>Why the whole drain and not just the write.</b> Both drains loop on
    /// <c>_queue.TryDequeue</c>. Gating the write alone leaves the dequeue racy: the pump takes the
    /// rows, the flush's drain then finds an EMPTY queue, returns early on <c>batch.Count == 0</c> and
    /// reports success while the pump is still inside <c>WriteAsync</c>'s BEGIN..COMMIT. A reader on a
    /// separate connection — which is every <c>LogStore.ReadAsync</c> — sees nothing, and the flush's
    /// contract is broken precisely when a test relies on it. So the gate spans the dequeue loop, the
    /// dropped-count exchange and the write together.</para>
    ///
    /// <para>The second failure this closes: two overlapping drains are two write transactions on the
    /// log database, and the loser takes SQLITE_BUSY into the catch below, which discards its whole
    /// batch by design. The race could therefore LOSE lines, not merely delay them.</para>
    ///
    /// <para>The wait is unbounded apart from the caller's own cancellation token. A timeout
    /// fall-through would reopen the hole it closes, by letting a flush return without the other
    /// drain's transaction having committed.</para>
    /// </summary>
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    private readonly Task _pump;

    private int _queueLength;
    private int _droppedSinceLastWrite;
    private int _disposed;

    public SqliteLoggerProvider(
        LogStore store,
        LogLevel minimumLevel = LogLevel.Information,
        TimeProvider? timeProvider = null,
        Action<Exception>? onError = null)
        : this(store, minimumLevel, timeProvider, onError, DefaultShutdownWait)
    {
    }

    /// <summary>
    /// arb-s3ky: test-only overload. <paramref name="shutdownWait"/> replaces
    /// <see cref="DefaultShutdownWait"/> for this instance — the same test-seam shape as
    /// <c>LogStore.WriteAsync</c>'s <c>cleanse</c> parameter (arb-qafw). It exists so a test can
    /// demonstrate the GIVING-UP path — that <see cref="DrainCompleted"/> still publishes after
    /// <see cref="Dispose"/> has abandoned a drain that outran its bound — without the test having
    /// to hold a real drain open for five wall-clock seconds. Every production call site uses the
    /// overload above and therefore <see cref="DefaultShutdownWait"/>; nothing here shortens
    /// production shutdown.
    /// </summary>
    public SqliteLoggerProvider(
        LogStore store,
        LogLevel minimumLevel,
        TimeProvider? timeProvider,
        Action<Exception>? onError,
        TimeSpan shutdownWait)
        : this(store, minimumLevel, timeProvider, onError, shutdownWait, cleanse: null)
    {
    }

    /// <summary>
    /// arb-fjid: test-only overload. <paramref name="cleanse"/>, when supplied, is handed to
    /// <c>LogStore.WriteAsync</c>'s own arb-qafw test seam of the same name, which it exists to reach:
    /// nothing here is new behaviour in the store, only a way for a test to supply what that overload
    /// already accepts.
    ///
    /// <para><b>NEVER pass this in production.</b> It REPLACES
    /// <see cref="LogMessageCleanser.Cleanse(string?)"/> for every row the drain writes, so a
    /// production caller supplying one would send un-scrubbed log lines to disk — a secrets defect,
    /// not a behaviour tweak. Both public constructors above pass <c>null</c>, which is what keeps the
    /// scrubber on the only path the host ever builds.</para>
    ///
    /// <para><b>Why the provider needs the seam at all.</b> The drain's ordering property — that
    /// <see cref="FlushAsync"/> cannot return while another drain holds an uncommitted batch — needs a
    /// drain held open INSIDE <see cref="_drainGate"/>, asynchronously. A held SQLite write lock
    /// cannot do it: <c>LogStore.WriteAsync</c> opens its connection synchronously, so a blocked drain
    /// blocks its caller inline rather than yielding, and the write then FAILS rather than waiting. A
    /// blocking cleanse runs inside the gate, under the caller's own await, and discriminates — an
    /// ungated provider's concurrent flush sails past it and returns having committed nothing.</para>
    ///
    /// <para><c>public</c>, not <c>internal</c>: there is no <c>InternalsVisibleTo</c> from this
    /// project to the test assembly, the same reason <c>LogStore.WriteAsync</c>'s <c>cleanse</c> seam
    /// and <c>LogStore.ResolveLevelsAtOrAbove</c> give for being public.</para>
    /// </summary>
    public SqliteLoggerProvider(
        LogStore store,
        LogLevel minimumLevel,
        TimeProvider? timeProvider,
        Action<Exception>? onError,
        TimeSpan shutdownWait,
        Func<string?, string?>? cleanse)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _onError = onError;
        _shutdownWait = shutdownWait;
        _cleanse = cleanse;
        _pump = Task.Run(PumpAsync);
    }

    public ILogger CreateLogger(string categoryName) => new SqliteLogger(this, StripPrefix(categoryName));

    /// <summary>
    /// Flushes anything still queued. Exposed for tests, which cannot wait out
    /// <see cref="FlushInterval"/> on every assertion without making the suite slow and timing-flaky.
    ///
    /// <para><b>The contract (arb-fjid, arb-mczu).</b> When this returns, every entry enqueued BEFORE
    /// the call was made is committed and visible to another connection — which is what a caller
    /// reading the rows back through <c>LogStore.ReadAsync</c> needs, since that opens a connection of
    /// its own and so cannot see an uncommitted transaction. Entries enqueued concurrently with the
    /// call may or may not be included; only the happens-before ones are promised.</para>
    ///
    /// <para>That guarantee rests on <see cref="_drainGate"/>: without it this could return while the
    /// background pump still held the rows in an uncommitted batch. See the gate's remarks.</para>
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => DrainAsync(cancellationToken);

    /// <summary>
    /// Completes when the pump has finished its FINAL <see cref="DrainAsync"/> — i.e. when the last
    /// pooled connection to <c>arbitarr-logs.db</c> has been returned to the pool and nothing this
    /// provider owns will open the file again.
    ///
    /// <para><b>Why this exists (arb-s3ky).</b> <see cref="Dispose"/>'s wait is bounded at
    /// <see cref="DefaultShutdownWait"/> and deliberately GIVES UP rather than hanging a shutting-down
    /// container. Under contention the final drain's write transaction can outrun that bound, so
    /// Dispose can return while the pump still holds — or is about to open — a pooled handle on the
    /// log database. A test teardown that then clears the pools and deletes the config directory
    /// loses to that handle, which is the intermittent this publishes a completion to close: a caller
    /// that CAN afford to wait longer than shutdown can await this instead of guessing.
    /// <c>ConfigDirectoryTeardown.Delete(string, IEnumerable&lt;Task&gt;)</c> is that caller.</para>
    ///
    /// <para><b>It publishes even when Dispose gave up, and even if the pump faults</b> — that is the
    /// whole point, so it is completed from a <c>finally</c> at the end of <see cref="PumpAsync"/>
    /// rather than being <c>_pump</c> handed out directly. This task therefore never faults and never
    /// cancels; awaiting it cannot throw. It is idempotent under a double <see cref="Dispose"/>
    /// because the pump runs, and so completes it, exactly once.</para>
    ///
    /// <para><b>arb-fjid's drain gate does not extend this bound.</b> Serialising the drains can make
    /// the pump's final drain WAIT for an in-flight <see cref="FlushAsync"/> before it starts, but
    /// <see cref="Dispose"/>'s wait stays bounded at <see cref="DefaultShutdownWait"/> and still gives
    /// up; the gate only adds one more way for the drain to be the thing Dispose gives up ON, which is
    /// the case this member already exists to cover.</para>
    /// </summary>
    public Task DrainCompleted => _drainCompleted.Task;

    public void Dispose()
    {
        // Idempotent by an explicit latch rather than by relying on Cancel()/Dispose() tolerating a
        // repeat: CancellationTokenSource.Cancel() on an already-disposed source is not contractually
        // safe, and xunit disposes some hosts through both disposal paths. The pump — and therefore
        // DrainCompleted — is unaffected either way; it runs exactly once.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _shutdown.Cancel();

        try
        {
            // Wait for the pump to observe cancellation and drain what it has. Bounded so a wedged
            // writer cannot hang process shutdown — losing the last few log lines beats a container
            // that will not stop. A caller needing the STRONGER guarantee (that the log database's
            // pooled handle is definitely back) awaits DrainCompleted, which outlives this.
            _pump.Wait(_shutdownWait);
        }
        catch (AggregateException)
        {
            // The pump never surfaces exceptions (see PumpAsync); this is belt-and-braces so
            // Dispose itself cannot throw during host shutdown.
        }

        // Cancel() above is idempotent and Wait() on a completed task returns at once, so a second
        // Dispose reaches here and must not double-dispose the source.
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
        try
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

            // Final drain on shutdown, so the lines explaining why the process is stopping are not
            // the ones that get lost. CancellationToken.None deliberately: _shutdown is already
            // cancelled.
            await DrainAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // arb-s3ky: published in a FINALLY, not after the final drain, so an unexpected fault
            // still publishes completion. A waiter blocked on DrainCompleted must never be stranded
            // by the one case it exists to survive — the pump not finishing the way it planned to.
            // DrainAsync already swallows write failures, so reaching here by exception means
            // something outside the drain broke; either way the pump is done with the log database
            // and every handle it held has gone back to the pool.
            //
            // arb-fjid: the pump's own drains pass CancellationToken.None, so waiting on the gate
            // cannot cancel out of them and nothing new escapes here that did not before.
            _drainCompleted.TrySetResult();

            // arb-fjid: disposed HERE rather than in Dispose. The pump is the last drain, and it has
            // just released the gate, so nothing this provider owns will take it again — whereas
            // Dispose can return while the pump is still inside a held drain (its wait is bounded on
            // purpose), and disposing the gate there would pull it out from under that drain. A
            // FlushAsync racing teardown lands on the ObjectDisposedException that DrainAsync
            // swallows.
            _drainGate.Dispose();
        }
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        // arb-fjid: one drain at a time, taken around the WHOLE body — dequeue, dropped-count
        // exchange and write — for the reasons in _drainGate's remarks. Unbounded apart from the
        // caller's token.
        try
        {
            await _drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The gate is disposed only after the pump has published DrainCompleted, so reaching here
            // means the provider is finished and there is nothing left this drain could usefully
            // commit. Swallowed rather than thrown for the same reason the write failure below is:
            // a late FlushAsync during teardown must not throw at a caller that is shutting down.
            return;
        }

        try
        {
            await DrainCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _drainGate.Release();
        }
    }

    /// <summary>
    /// The drain proper. Only ever called under <see cref="_drainGate"/> — see
    /// <see cref="DrainAsync"/>, which is the single caller.
    /// </summary>
    private async Task DrainCoreAsync(CancellationToken cancellationToken)
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
            // cancellationToken BY NAME: LogStore.WriteAsync's two overloads both accept an untyped
            // null second argument and the cleanse one wins as the more specific match, so a
            // positional call here would silently pass no token (that overload's own remarks warn of
            // it). _cleanse is null on every production path.
            await _store.WriteAsync(batch, _cleanse, cancellationToken: cancellationToken).ConfigureAwait(false);
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
