using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.Hosting;

namespace Arbitarr.Core.Caching;

/// <summary>
/// Delegate a caller supplies to actually perform one entry's refresh against a real upstream
/// source. <see cref="RefreshWorker"/> owns selection, breaker consultation, and pacing only —
/// it never talks to <c>IUpstreamSource</c> directly, keeping it unit-testable against fakes.
/// </summary>
/// <param name="sourceName">Which source to refresh this entry against.</param>
/// <param name="entry">The stale entry selected for refresh.</param>
/// <returns>The freshly fetched payload on success, or null if the attempt failed (the existing entry must then be left untouched — M3-10).</returns>
public delegate Task<string?> RefreshFetcher(string sourceName, CachedSearchResult entry, CancellationToken cancellationToken);

/// <summary>
/// Proactive background refresh worker (plan Step 4a Step 3/4/5). On each cycle:
/// 1. Selects entries matching <c>LastRequestedAt &gt; now - active_window AND
///    now &gt;= FreshUntil - refresh_lead</c> — actively-requested entries approaching staleness.
/// 2. Consults the same per-source <see cref="IAsyncCircuitBreaker"/> the inline search path
///    uses before attempting a refresh, so a tripped breaker halts worker traffic to that source
///    exactly as it halts inline traffic. Worker failures feed the same breaker.
/// 3. On failure, the existing entry is left completely untouched — byte-identical payload,
///    unchanged <c>ServeUntil</c> (M3-10). Only a successful fetch calls
///    <see cref="SearchResultCache.SaveAsync"/>.
///
/// Per-entry refresh scheduling (steps 1-3 above) is deliberately separate from per-source
/// breaker tripping (a per-source, not per-entry, concept) — see plan Step 4.
/// </summary>
public sealed class RefreshWorker : BackgroundService
{
    private readonly ISearchResultCacheStore _store;
    private readonly SearchResultCache _cache;
    private readonly IAsyncCircuitBreaker _circuitBreaker;
    private readonly RefreshFetcher _fetcher;
    private readonly TimeProvider _timeProvider;
    private readonly RefreshWorkerOptions _options;
    private readonly string _sourceName;
    private readonly RepopulationPacer _pacer;

    public RefreshWorker(
        ISearchResultCacheStore store,
        SearchResultCache cache,
        IAsyncCircuitBreaker circuitBreaker,
        RefreshFetcher fetcher,
        TimeProvider timeProvider,
        RefreshWorkerOptions options,
        string sourceName,
        RepopulationPacer? pacer = null)
    {
        _store = store;
        _cache = cache;
        _circuitBreaker = circuitBreaker;
        _fetcher = fetcher;
        _timeProvider = timeProvider;
        _options = options;
        _sourceName = sourceName;
        _pacer = pacer ?? new RepopulationPacer();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_options.Enabled)
            {
                await RunCycleAsync(stoppingToken);
            }

            try
            {
                await Task.Delay(_options.WorkerCycleInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one selection-and-refresh cycle. Public so tests can drive individual cycles under a
    /// fake clock without waiting on the hosted-service loop.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await _store.GetRefreshCandidatesAsync(now, _options.ActiveWindow, _options.RefreshLead, cancellationToken);
        if (candidates.Count == 0)
        {
            return;
        }

        if (!await _circuitBreaker.CanCallAsync(_sourceName, cancellationToken))
        {
            // Breaker open for this source: the worker defers to the same breaker the inline
            // path uses and does not attempt any refresh this cycle.
            return;
        }

        // Plan a paced schedule (R22): spread refresh starts across a full fresh_until interval
        // so a large backlog (e.g. following a circuit close) does not fire as a synchronized
        // burst, and bound how many refreshes may be in flight against this source at once.
        var plan = _pacer.Plan(candidates, _options.RepopulationSpreadWindow, _options.MaxConcurrentRefreshes, _sourceName);
        var entriesByKey = candidates.ToDictionary(c => c.QueryKey);
        using var throttle = new SemaphoreSlim(_options.MaxConcurrentRefreshes);

        var tasks = plan.Select(async paced =>
        {
            if (paced.StartOffset > TimeSpan.Zero)
            {
                await Task.Delay(paced.StartOffset, _timeProvider, cancellationToken);
            }

            await throttle.WaitAsync(cancellationToken);
            try
            {
                await RefreshOneAsync(entriesByKey[paced.QueryKey], cancellationToken);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private async Task RefreshOneAsync(CachedSearchResult entry, CancellationToken cancellationToken)
    {
        string? payload;
        try
        {
            payload = await _fetcher(_sourceName, entry, cancellationToken);
        }
        catch (Exception ex)
        {
            await _circuitBreaker.RecordFailureAsync(_sourceName, ex, cancellationToken);
            return;
        }

        if (payload is null)
        {
            // Fetcher reported a soft failure without throwing: existing entry must be left
            // untouched (M3-10). Do not record this as a breaker failure — the fetcher already
            // knows the difference between "upstream call failed" (records via exception above)
            // and "nothing new to write back".
            return;
        }

        await _circuitBreaker.RecordSuccessAsync(_sourceName, cancellationToken);
        await _cache.SaveAsync(entry.QueryKey, payload, _options.FreshUntilAge, _options.ServeUntilAge, cancellationToken);
    }
}

/// <summary>Configuration for one <see cref="RefreshWorker"/> instance's cycle/selection/write-back behaviour.</summary>
/// <param name="Enabled">Global on/off for proactive refresh (<see cref="Settings.SettingKey.WorkerEnabled"/>).</param>
/// <param name="WorkerCycleInterval">How often the worker wakes and evaluates the selection predicate.</param>
/// <param name="ActiveWindow">Trailing window defining "actively being requested".</param>
/// <param name="RefreshLead">How far ahead of FreshUntil the worker aims to refresh.</param>
/// <param name="FreshUntilAge">The FreshUntil age to apply when writing back a successful refresh.</param>
/// <param name="ServeUntilAge">The ServeUntil age to apply when writing back a successful refresh.</param>
/// <param name="RepopulationSpreadWindow">Window across which refresh starts are spread on each cycle (R22); typically the configured <see cref="FreshUntilAge"/>.</param>
/// <param name="MaxConcurrentRefreshes">Maximum number of refreshes against this source permitted in flight at once (R22).</param>
public sealed record RefreshWorkerOptions(
    bool Enabled,
    TimeSpan WorkerCycleInterval,
    TimeSpan ActiveWindow,
    TimeSpan RefreshLead,
    TimeSpan FreshUntilAge,
    TimeSpan ServeUntilAge,
    TimeSpan RepopulationSpreadWindow,
    int MaxConcurrentRefreshes);
