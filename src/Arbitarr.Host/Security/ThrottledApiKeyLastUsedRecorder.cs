using System.Collections.Concurrent;
using Arbitarr.Core.Security;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;

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
/// </summary>
public sealed class ThrottledApiKeyLastUsedRecorder : IApiKeyLastUsedRecorder
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastWrittenAt = new();
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
    public void RecordUsed(long keyId)
    {
        var now = _timeProvider.GetUtcNow();

        // AddOrUpdate returns the value now in the map, and the update arm returns the EXISTING
        // value unchanged when inside the window. So "the map now holds `now`" is precisely
        // "this call won the slot", decided atomically — two concurrent requests for the same key
        // cannot both win, which a read-then-write pair would allow.
        var claimed = _lastWrittenAt.AddOrUpdate(
            keyId,
            now,
            (_, previous) => now - previous >= IApiKeyLastUsedRecorder.ThrottleWindow ? now : previous);

        if (claimed != now)
        {
            return;
        }

        _ = Task.Run(() => WriteAsync(keyId, now));
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
