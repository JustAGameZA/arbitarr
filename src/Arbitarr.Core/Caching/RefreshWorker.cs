using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources.CircuitBreaker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly Func<(RefreshWorkerDependencies Dependencies, IDisposable? Scope)> _resolveDependencies;
    private readonly Func<CancellationToken, ValueTask<RefreshWorkerOptions>> _resolveOptions;
    private readonly TimeProvider _timeProvider;
    private readonly string _sourceName;
    private readonly RepopulationPacer _pacer;
    private readonly ILogger _logger;
    private readonly IRefreshWorkerHealth _health;

    /// <summary>
    /// Constructs a worker over fixed dependencies. Used by tests (fakes, injected clock) and valid
    /// wherever the store/cache/breaker are themselves long-lived. <paramref name="options"/> is
    /// wrapped in a static <see cref="IRefreshWorkerOptionsSource"/> — existing tests that pass a
    /// fixed <see cref="RefreshWorkerOptions"/> keep their exact semantics (the options never
    /// change out from under them mid-test).
    /// </summary>
    public RefreshWorker(
        ISearchResultCacheStore store,
        SearchResultCache cache,
        IAsyncCircuitBreaker circuitBreaker,
        RefreshFetcher fetcher,
        TimeProvider timeProvider,
        RefreshWorkerOptions options,
        string sourceName,
        RepopulationPacer? pacer = null,
        ILogger? logger = null,
        IRefreshWorkerHealth? health = null)
        : this(
            () => (new RefreshWorkerDependencies(store, cache, circuitBreaker, fetcher), null),
            new StaticRefreshWorkerOptionsSource(options),
            timeProvider,
            sourceName,
            pacer,
            logger,
            health)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(circuitBreaker);
        ArgumentNullException.ThrowIfNull(fetcher);
    }

    /// <summary>
    /// Constructs a worker that resolves <see cref="RefreshWorkerDependencies"/> and the current
    /// <see cref="RefreshWorkerOptions"/> (via <see cref="IRefreshWorkerOptionsSource"/>) from a
    /// fresh DI scope on <b>every cycle</b>. This is the Host wiring: the worker is a singleton
    /// hosted service, but the EF-backed store, the persistent breaker, the fetcher's upstream
    /// sources, and the live settings read are scoped (they share a per-request <c>DbContext</c>),
    /// so each cycle gets — and disposes — its own scope rather than holding a single
    /// <c>DbContext</c> open for the process lifetime. Because options are re-resolved every cycle
    /// (M7-8b), toggling <c>WorkerEnabled</c> or changing the cycle interval or any other tunable
    /// takes effect at the next tick without a restart.
    /// </summary>
    public RefreshWorker(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        string sourceName,
        RepopulationPacer? pacer = null,
        ILogger<RefreshWorker>? logger = null,
        IRefreshWorkerHealth? health = null)
        : this(
            () =>
            {
                var scope = scopeFactory.CreateScope();
                try
                {
                    var provider = scope.ServiceProvider;
                    var dependencies = new RefreshWorkerDependencies(
                        provider.GetRequiredService<ISearchResultCacheStore>(),
                        provider.GetRequiredService<SearchResultCache>(),
                        provider.GetRequiredService<IAsyncCircuitBreaker>(),
                        provider.GetRequiredService<RefreshFetcher>());
                    return (dependencies, scope);
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            },
            new ScopedRefreshWorkerOptionsSource(scopeFactory),
            timeProvider,
            sourceName,
            pacer,
            logger,
            health)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
    }

    private RefreshWorker(
        Func<(RefreshWorkerDependencies Dependencies, IDisposable? Scope)> resolveDependencies,
        IRefreshWorkerOptionsSource optionsSource,
        TimeProvider timeProvider,
        string sourceName,
        RepopulationPacer? pacer,
        ILogger? logger,
        IRefreshWorkerHealth? health)
    {
        ArgumentNullException.ThrowIfNull(optionsSource);
        _resolveDependencies = resolveDependencies;
        _resolveOptions = optionsSource.GetAsync;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _sourceName = sourceName ?? throw new ArgumentNullException(nameof(sourceName));
        _pacer = pacer ?? new RepopulationPacer();
        _logger = logger ?? NullLogger.Instance;
        _health = health ?? NullRefreshWorkerHealth.Instance;
    }

    /// <summary>
    /// A DI scope for resolving a fresh <see cref="IRefreshWorkerOptionsSource"/> each cycle (Host
    /// wiring): the underlying settings read is scoped alongside the other per-cycle dependencies.
    /// </summary>
    private sealed class ScopedRefreshWorkerOptionsSource(IServiceScopeFactory scopeFactory) : IRefreshWorkerOptionsSource
    {
        public async ValueTask<RefreshWorkerOptions> GetAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var source = scope.ServiceProvider.GetRequiredService<IRefreshWorkerOptionsSource>();
            return await source.GetAsync(cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var options = await _resolveOptions(stoppingToken);

            if (options.Enabled)
            {
                try
                {
                    await RunCycleAsync(options, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failed cycle (e.g. the store is unreachable) must not take the host down:
                    // BackgroundService faults propagate to the host by default. Log and retry on
                    // the next tick; the breaker already governs per-source upstream failures.
                    _logger.LogError(ex, "Search-result refresh cycle for source {SourceName} failed; will retry next cycle.", _sourceName);
                    _health.CycleFaulted(_timeProvider.GetUtcNow(), SanitizedErrorDescription.Describe(ex));
                }
            }

            try
            {
                await Task.Delay(options.WorkerCycleInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one selection-and-refresh cycle, re-reading options from the source first (M7-8b) so a
    /// setting changed since the last cycle applies immediately. Public so tests can drive
    /// individual cycles under a fake clock without waiting on the hosted-service loop.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var options = await _resolveOptions(cancellationToken);
        await RunCycleAsync(options, cancellationToken);
    }

    private async Task RunCycleAsync(RefreshWorkerOptions options, CancellationToken cancellationToken)
    {
        var (dependencies, scope) = _resolveDependencies();
        using (scope)
        {
            await RunCycleAsync(dependencies, options, cancellationToken);
        }
    }

    private async Task RunCycleAsync(RefreshWorkerDependencies deps, RefreshWorkerOptions options, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await deps.Store.GetRefreshCandidatesAsync(now, options.ActiveWindow, options.RefreshLead, cancellationToken);
        _health.CycleStarted(now, options.Enabled, candidates.Count);

        if (candidates.Count == 0)
        {
            _health.CycleCompleted(_timeProvider.GetUtcNow(), refreshed: 0, failed: 0);
            return;
        }

        if (!await deps.CircuitBreaker.CanCallAsync(_sourceName, cancellationToken))
        {
            // Breaker open for this source: the worker defers to the same breaker the inline
            // path uses and does not attempt any refresh this cycle.
            _health.CycleCompleted(_timeProvider.GetUtcNow(), refreshed: 0, failed: 0);
            return;
        }

        // Plan a paced schedule (R22): spread refresh starts across a full fresh_until interval
        // so a large backlog (e.g. following a circuit close) does not fire as a synchronized
        // burst, and bound how many refreshes may be in flight against this source at once.
        var plan = _pacer.Plan(candidates, options.RepopulationSpreadWindow, options.MaxConcurrentRefreshes, _sourceName);
        var entriesByKey = candidates.ToDictionary(c => c.QueryKey);
        using var throttle = new SemaphoreSlim(options.MaxConcurrentRefreshes);

        var refreshedCount = 0;
        var failedCount = 0;

        var tasks = plan.Select(async paced =>
        {
            if (paced.StartOffset > TimeSpan.Zero)
            {
                await Task.Delay(paced.StartOffset, _timeProvider, cancellationToken);
            }

            await throttle.WaitAsync(cancellationToken);
            try
            {
                var succeeded = await RefreshOneAsync(deps, options, entriesByKey[paced.QueryKey], cancellationToken);
                if (succeeded)
                {
                    Interlocked.Increment(ref refreshedCount);
                }
                else
                {
                    Interlocked.Increment(ref failedCount);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        _health.CycleCompleted(_timeProvider.GetUtcNow(), refreshedCount, failedCount);
    }

    /// <returns>true if the entry was successfully refreshed and written back; false otherwise.</returns>
    private async Task<bool> RefreshOneAsync(RefreshWorkerDependencies deps, RefreshWorkerOptions options, CachedSearchResult entry, CancellationToken cancellationToken)
    {
        string? payload;
        try
        {
            payload = await deps.Fetcher(_sourceName, entry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await deps.CircuitBreaker.RecordFailureAsync(_sourceName, ex, cancellationToken);
            return false;
        }

        if (payload is null)
        {
            // Fetcher reported a soft failure without throwing: existing entry must be left
            // untouched (M3-10). Do not record this as a breaker failure — the fetcher already
            // knows the difference between "upstream call failed" (records via exception above)
            // and "nothing new to write back".
            return false;
        }

        await deps.CircuitBreaker.RecordSuccessAsync(_sourceName, cancellationToken);
        await deps.Cache.SaveAsync(entry.QueryKey, payload, options.FreshUntilAge, options.ServeUntilAge, cancellationToken);
        return true;
    }
}

/// <summary>
/// The per-cycle collaborators a <see cref="RefreshWorker"/> needs: resolved once per cycle from a
/// DI scope in the Host, or supplied directly (fakes) in tests.
/// </summary>
public sealed record RefreshWorkerDependencies(
    ISearchResultCacheStore Store,
    SearchResultCache Cache,
    IAsyncCircuitBreaker CircuitBreaker,
    RefreshFetcher Fetcher);

/// <summary>
/// Supplies the current <see cref="RefreshWorkerOptions"/> on demand (M7-8b/AC24 settings
/// liveness): <see cref="RefreshWorker"/> calls this at the top of every loop iteration and every
/// cycle so a setting changed via the admin API takes effect at the next tick without a restart.
/// </summary>
public interface IRefreshWorkerOptionsSource
{
    ValueTask<RefreshWorkerOptions> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Wraps a fixed <see cref="RefreshWorkerOptions"/> value that never changes — used by the
/// fixed-deps <see cref="RefreshWorker"/> constructor so existing tests keep their exact semantics.
/// </summary>
public sealed class StaticRefreshWorkerOptionsSource(RefreshWorkerOptions options) : IRefreshWorkerOptionsSource
{
    private readonly RefreshWorkerOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public ValueTask<RefreshWorkerOptions> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_options);
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
