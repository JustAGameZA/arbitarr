using System.Collections.Concurrent;
using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Arbitarr.Host.Security;

/// <summary>
/// The real <see cref="IApiKeyLastUsedRecorder"/> (#58 step 7): records a key's last-used time at
/// most once per <see cref="IApiKeyLastUsedRecorder.ThrottleWindow"/>, off the request path.
///
/// <para><b>THE MECHANISM, STATED.</b> Two independent reductions, both required:</para>
/// <list type="number">
/// <item><b>Coalescing.</b> A per-key timestamp of the last write is held in memory. A call arriving
/// inside the window returns having done nothing at all — no scope, no query, no write. A *arr
/// instance polling every few seconds therefore costs one UPDATE a minute, not one per request.</item>
/// <item><b>Detachment.</b> The write that does happen is dispatched to the thread pool, not awaited.
/// The request has already been authorized by the time this is called; making it wait on a
/// bookkeeping UPDATE against the single-writer SQLite database would put a serialized write in
/// front of every gated call, which is exactly what the plan forbids.</item>
/// </list>
///
/// <para><b>WHY THE TIMESTAMP IS STAMPED AT CALL TIME, NOT WRITE TIME.</b> The value persisted is
/// when the key was USED, captured synchronously here, rather than whenever the detached write
/// happens to run. Otherwise a loaded thread pool would record a time the key was not used, and the
/// field would be answering a slightly different question than the operator is asking.</para>
///
/// <para><b>WHY THE MAP CANNOT GROW WITHOUT BOUND.</b> It is keyed by key ID, and keys are minted by
/// an operator by hand — the domain is dozens, not millions. There is deliberately no eviction: an
/// entry for a revoked key is one <c>long</c> and one <c>DateTimeOffset</c>, and reaping them would
/// be more machinery than the leak it prevents.</para>
///
/// <para>Singleton (the throttle state must outlive a request, which is the entire point), so it
/// resolves a scope per write like <see cref="Diagnostics.ScopedEventSink"/> does, and for the same
/// reason: <see cref="ApiKeyRepository"/> wraps the scoped <c>ArbitarrDbContext</c>, which is not
/// thread-safe.</para>
///
/// <para><b>WHY THIS IS ALSO AN <see cref="IHostedService"/> (arb-acy9).</b> Detachment has a cost
/// at exactly one moment: shutdown. A write dispatched a millisecond before the host stops finds
/// its scope's provider disposed out from under it, so the swallow branch below fires and logs the
/// Warning — on a shutdown where nothing is actually wrong. The Warning itself is not the problem
/// and must not be softened (it is the only evidence of a genuinely failing write); what is wrong
/// is that shutdown creates the failure. So the same singleton is registered as a hosted service and
/// <see cref="StopAsync"/> awaits whatever is in flight, bounded by <see cref="DrainTimeout"/>,
/// BEFORE the provider goes away. Registering the instance twice rather than adding a separate
/// drain service is deliberate: the in-flight set and the thing that must wait on it are the same
/// object, and a second type would have to be handed a reference to this one anyway.</para>
///
/// <para>The drain is a best-effort bound, not a guarantee: a write that outruns
/// <see cref="DrainTimeout"/> is still abandoned, and will still log the Warning. That is the right
/// trade — a shutdown must terminate — and it is why the timeout is seconds rather than
/// milliseconds.</para>
/// </summary>
public sealed class ThrottledApiKeyLastUsedRecorder : IApiKeyLastUsedRecorder, IHostedService
{
    /// <summary>
    /// How long <see cref="StopAsync"/> waits for in-flight writes before giving up on them.
    ///
    /// <para>Five seconds. Each pending item is a single indexed UPDATE against a local SQLite file,
    /// so the realistic worst case is the single-writer lock being held by another write, measured
    /// in milliseconds. The number is generous against that rather than tuned to it, because the
    /// cost of being generous is nothing on a healthy shutdown (the drain returns as soon as the set
    /// empties, not after the timeout) while the cost of being stingy is the orphaned write this
    /// exists to prevent. It is a constant rather than a setting for the reason
    /// <c>NotificationHostedService.CycleInterval</c> is: no operator has a reason to turn it.</para>
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastWrittenAt = new();

    /// <summary>
    /// The writes dispatched but not yet finished. A set rather than a counter so the drain can
    /// actually AWAIT them instead of spinning on a number, and keyed by the task itself because a
    /// <see cref="Task"/> is its own identity. <see cref="ConcurrentDictionary{TKey,TValue}"/> is
    /// used as a concurrent set (there is no <c>ConcurrentSet</c>); the value is ignored.
    /// </summary>
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ThrottledApiKeyLastUsedRecorder> _logger;

    public ThrottledApiKeyLastUsedRecorder(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<ThrottledApiKeyLastUsedRecorder> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    /// <remarks>Nothing to start: the drain is the only reason this is a hosted service.</remarks>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Awaits the in-flight writes so they finish while the DI provider is still alive.
    /// <paramref name="cancellationToken"/> is the host's shutdown token, which is ALREADY signalled
    /// by the time a forced shutdown reaches here — so it is deliberately not passed to the wait.
    /// Honouring it would make the drain a no-op in precisely the case it is for. The bound is
    /// <see cref="DrainTimeout"/> instead, which terminates shutdown on its own.
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync();

    /// <summary>
    /// Waits for every write dispatched so far, bounded by <see cref="DrainTimeout"/>. Faulted
    /// writes are not observed here: <see cref="WriteAsync"/> already swallows and logs, so a task
    /// in this set never faults, and <see cref="Task.WhenAll(IEnumerable{Task})"/> would have
    /// nothing to rethrow.
    /// </summary>
    private async Task DrainAsync()
    {
        var pending = _inFlight.Keys.ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(pending);
        var completed = await Task.WhenAny(all, Task.Delay(DrainTimeout)).ConfigureAwait(false);

        if (!ReferenceEquals(completed, all))
        {
            // The Delay won. Say so rather than letting the abandoned writes' Warnings be the only
            // trace, which would read as a database fault rather than as a shutdown that ran out of
            // patience.
            _logger.LogWarning(
                "Gave up waiting for {PendingCount} in-flight API key last-used write(s) after {DrainSeconds}s of shutdown; " +
                "they may not have been persisted.",
                pending.Count(task => !task.IsCompleted),
                DrainTimeout.TotalSeconds);
        }
    }

    /// <inheritdoc />
    public void RecordUsed(long keyId)
    {
        var now = _timeProvider.GetUtcNow();

        // AddOrUpdate returns the value now in the map, and the update arm returns the EXISTING
        // value unchanged when inside the window. So "the map now holds `now`" is precisely
        // "this call won the slot", decided atomically — two concurrent requests for the same key
        // cannot both win, which a read-then-write pair would allow.
        //
        // NOTE FOR A FUTURE CONCURRENCY TEST: this identity test picks out the winner only while
        // timestamps are DISTINCT, which they are against a real clock. Under a FROZEN TimeProvider
        // every racer reads the same instant, so all of them compare equal to `now` and all appear
        // to win — an artefact of the fake clock, not a product bug. Assert the throttle with an
        // advancing FakeTimeProvider rather than a frozen one.
        var claimed = _lastWrittenAt.AddOrUpdate(
            keyId,
            now,
            (_, previous) => now - previous >= IApiKeyLastUsedRecorder.ThrottleWindow ? now : previous);

        if (claimed != now)
        {
            return;
        }

        Dispatch(keyId, now);
    }

    /// <summary>
    /// Dispatches the write detached (the second of the two reductions above) while keeping it
    /// visible to <see cref="DrainAsync"/>.
    ///
    /// <para><b>The registration must happen BEFORE the task can complete</b>, or the drain could
    /// look at an empty set while a write is running. <c>Task.Run</c> may begin on another thread the
    /// instant it is called, so the task is created in a state where it cannot run yet — the inner
    /// body awaits a gate that is only released after the task is in the set. That
    /// ordering, not the set's thread-safety, is what makes the drain complete rather than racy.</para>
    /// </summary>
    private void Dispatch(long keyId, DateTimeOffset usedAt)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var write = Task.Run(async () =>
        {
            await gate.Task.ConfigureAwait(false);
            await WriteAsync(keyId, usedAt).ConfigureAwait(false);
        });

        _inFlight[write] = 0;

        // Self-removal keeps the set bounded by what is genuinely in flight rather than by how many
        // writes the process has ever made. Ordered after the add so the entry always exists by the
        // time the continuation tries to remove it.
        _ = write.ContinueWith(
            (completed, _) => _inFlight.TryRemove(completed, out byte _),
            state: null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        gate.SetResult();
    }

    private async Task WriteAsync(long keyId, DateTimeOffset usedAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ApiKeyRepository>();
            await repository.RecordLastUsedAsync(keyId, usedAt, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallowed, and logged at Warning, for the same reason ScopedEventSink swallows:
            // bookkeeping must never break the thing it is bookkeeping for. The request this
            // belongs to was authorized and answered long before this ran, so throwing here would
            // reach an unhandled-exception handler on a pool thread rather than any caller.
            // The cost of the failure is one stale last-used timestamp.
            _logger.LogWarning(
                ex,
                "Failed to record the last-used time for API key {ApiKeyId}; the request it belongs to was unaffected.",
                keyId);
        }
    }
}
