using Arbitarr.Ai;
using Arbitarr.Ai.Normalization;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host;

/// <summary>
/// Background hosted service that drives <see cref="ClassifierWorker.ClassifyAndCacheAsync"/>
/// against whatever this process has recently rendered (verify-m5 HIGH: previously nothing ever
/// called it in the running Host, so <c>ClassifierWorker</c> was dead code in production).
///
/// <para>
/// <see cref="ClassifierWorker"/> itself is deliberately left unchanged (not a
/// <see cref="BackgroundService"/>) because its existing fixed 4-arg constructor is used directly
/// by <c>ClassifierWorkerLoadTests</c>; this type wraps it instead, mirroring
/// <see cref="Arbitarr.Core.Caching.RefreshWorker"/>'s pattern: a fixed-deps constructor for tests,
/// an <see cref="IServiceScopeFactory"/>-based constructor for Host wiring (each cycle resolves a
/// fresh scope so the EF-backed verdict cache reader/writer never outlive one cycle), and a shared
/// private constructor.
/// </para>
///
/// <para>
/// Per cycle: snapshots candidates via <see cref="InMemoryReleaseLookup.Snapshot"/> (the worker
/// never talks to upstream sources directly), skips any candidate already present in the verdict
/// cache (<see cref="VerdictCacheKey.Compute"/> + <see cref="IVerdictCacheReader.TryGet"/>), and
/// classifies the rest. AC24: the poll interval is re-read from settings at the top of every cycle,
/// so an operator's change takes effect on the next cycle with no restart required. P1 fail-open:
/// a failed cycle is logged and retried next tick, never take the Host down.
/// </para>
///
/// <para>
/// M5-8/AC26b (title normalization, R17): when a candidate is classified, this worker also runs
/// <see cref="TitleNormalizer.Normalize"/> (gated by <see cref="Data.Settings.SettingsReader.GetTitleNormalizationEnabledAsync"/>)
/// and persists the resulting rewrite (if any) into the verdict cache entry alongside the verdict —
/// the rewrite is worker-produced and cached here, never computed inline on the render path.
/// </para>
/// </summary>
public sealed class ClassifierPollingWorker : BackgroundService
{
    private readonly Func<(ClassifierPollingWorkerDependencies Dependencies, IDisposable? Scope)> _resolveDependencies;
    private readonly TimeProvider _timeProvider;
    private readonly TitleNormalizer _titleNormalizer;
    private readonly ILogger _logger;

    /// <summary>Constructs a worker over fixed dependencies. Used by tests (fakes, injected clock).</summary>
    public ClassifierPollingWorker(
        ClassifierWorker classifierWorker,
        InMemoryReleaseLookup releaseLookup,
        IVerdictCacheReader verdictCacheReader,
        IVerdictCacheWriter verdictCacheWriter,
        AiModelIdentity modelIdentity,
        Func<CancellationToken, Task<TimeSpan>> getPollInterval,
        Func<CancellationToken, Task<bool>> getTitleNormalizationEnabled,
        TimeProvider timeProvider,
        TitleNormalizer? titleNormalizer = null,
        ILogger? logger = null)
        : this(
            () => (new ClassifierPollingWorkerDependencies(
                classifierWorker, releaseLookup, verdictCacheReader, verdictCacheWriter, modelIdentity,
                getPollInterval, getTitleNormalizationEnabled), null),
            timeProvider,
            titleNormalizer,
            logger)
    {
        ArgumentNullException.ThrowIfNull(classifierWorker);
        ArgumentNullException.ThrowIfNull(releaseLookup);
        ArgumentNullException.ThrowIfNull(verdictCacheReader);
        ArgumentNullException.ThrowIfNull(verdictCacheWriter);
        ArgumentNullException.ThrowIfNull(modelIdentity);
        ArgumentNullException.ThrowIfNull(getPollInterval);
        ArgumentNullException.ThrowIfNull(getTitleNormalizationEnabled);
    }

    /// <summary>
    /// Constructs a worker that resolves its scoped dependencies from a fresh DI scope on every
    /// cycle. This is the Host wiring: the worker is a singleton hosted service, but
    /// <see cref="ClassifierWorker"/> and the EF-backed verdict cache reader/writer are scoped.
    /// </summary>
    public ClassifierPollingWorker(
        IServiceScopeFactory scopeFactory,
        InMemoryReleaseLookup releaseLookup,
        AiModelIdentity modelIdentity,
        TimeProvider timeProvider,
        TitleNormalizer? titleNormalizer = null,
        ILogger<ClassifierPollingWorker>? logger = null)
        : this(
            () =>
            {
                var scope = scopeFactory.CreateScope();
                try
                {
                    var provider = scope.ServiceProvider;
                    var settingsReader = provider.GetRequiredService<SettingsReader>();
                    var dependencies = new ClassifierPollingWorkerDependencies(
                        provider.GetRequiredService<ClassifierWorker>(),
                        releaseLookup,
                        provider.GetRequiredService<IVerdictCacheReader>(),
                        provider.GetRequiredService<IVerdictCacheWriter>(),
                        modelIdentity,
                        settingsReader.GetClassifierPollIntervalAsync,
                        settingsReader.GetTitleNormalizationEnabledAsync);
                    return (dependencies, scope);
                }
                catch
                {
                    scope.Dispose();
                    throw;
                }
            },
            timeProvider,
            titleNormalizer,
            logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(releaseLookup);
        ArgumentNullException.ThrowIfNull(modelIdentity);
    }

    private ClassifierPollingWorker(
        Func<(ClassifierPollingWorkerDependencies Dependencies, IDisposable? Scope)> resolveDependencies,
        TimeProvider timeProvider,
        TitleNormalizer? titleNormalizer,
        ILogger? logger)
    {
        _resolveDependencies = resolveDependencies;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _titleNormalizer = titleNormalizer ?? new TitleNormalizer();
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // P1 fail-open: a failed classification cycle must never take the Host down. Log
                // and retry on the next tick.
                _logger.LogError(ex, "Classifier polling cycle failed; will retry next cycle.");
            }

            TimeSpan delay;
            try
            {
                var (deps, scope) = _resolveDependencies();
                using (scope)
                {
                    delay = await deps.GetPollInterval(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // Could not even resolve the poll interval this tick (e.g. DB unreachable): fall
                // back to the code-defined default rather than spinning hot or crashing the loop.
                delay = (TimeSpan)Core.Settings.SettingsCatalog.GetDefault(Core.Settings.SettingKey.ClassifierPollInterval);
            }

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one snapshot-and-classify cycle. Public so tests can drive individual cycles directly
    /// without waiting on the hosted-service loop.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken cancellationToken = default)
    {
        var (dependencies, scope) = _resolveDependencies();
        using (scope)
        {
            await RunCycleAsync(dependencies, cancellationToken);
        }
    }

    private async Task RunCycleAsync(ClassifierPollingWorkerDependencies deps, CancellationToken cancellationToken)
    {
        var candidates = deps.ReleaseLookup.Snapshot();
        if (candidates.Count == 0)
        {
            return;
        }

        var titleNormalizationEnabled = await deps.GetTitleNormalizationEnabled(cancellationToken).ConfigureAwait(false);

        foreach (var rendered in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The lookup holds what was rendered, which may already carry a cached rewrite
            // (Title = rewritten, OriginalTitle = upstream). Classification, keying, and
            // normalization all work from the upstream title so the worker's key matches the
            // render path's (VerdictCacheKey keys on OriginalTitle) and a rewrite is never
            // derived from an earlier rewrite.
            var candidate = rendered.Candidate.WithTitle(rendered.Candidate.OriginalTitle, originalTitleRaw: null);
            var key = VerdictCacheKey.Compute(
                candidate, rendered.SourceName, deps.ModelIdentity.ModelName, deps.ModelIdentity.ModelDigest, deps.ModelIdentity.PromptVersion);

            var cached = deps.VerdictCacheReader.TryGet(key);
            if (cached is not null && (cached.RewrittenTitle is not null || !titleNormalizationEnabled))
            {
                // Already classified under this model/prompt identity, and either a rewrite is
                // already attached or normalization is off: nothing to do this cycle.
                continue;
            }

            // R17: the rewrite is produced and cached here (worker-side), never computed inline on
            // the render path. P1 fail-open: normalization failure/kill-switch-off simply means no
            // rewrite is cached, and the render path already falls back to the original title.
            var normalized = _titleNormalizer.Normalize(candidate, titleNormalizationEnabled);
            var rewrittenTitle = string.Equals(normalized.Title, candidate.Title, StringComparison.Ordinal)
                ? null
                : VerdictCacheLimits.TruncateRewrittenTitle(normalized.Title);

            if (cached is null)
            {
                await deps.ClassifierWorker.ClassifyAndCacheAsync(candidate, rendered.SourceName, cancellationToken).ConfigureAwait(false);
                // Re-read rather than assume: a fail-open classification writes nothing, and an
                // orphaned rewrite must never be attached to a verdict that was never cached.
                cached = deps.VerdictCacheReader.TryGet(key);
            }

            if (rewrittenTitle is not null && cached is not null)
            {
                // Attach (or back-fill, for entries classified while normalization was off) the
                // rewrite as a follow-up update under the same key; the verdict is left as cached.
                await deps.VerdictCacheWriter.PutAsync(
                    key,
                    deps.ModelIdentity.ModelName,
                    deps.ModelIdentity.ModelDigest,
                    deps.ModelIdentity.PromptVersion,
                    cached.Verdict,
                    cached.Confidence,
                    rewrittenTitle,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// The per-cycle collaborators a <see cref="ClassifierPollingWorker"/> needs: resolved once per
/// cycle from a DI scope in the Host, or supplied directly (fakes) in tests.
/// </summary>
public sealed record ClassifierPollingWorkerDependencies(
    ClassifierWorker ClassifierWorker,
    InMemoryReleaseLookup ReleaseLookup,
    IVerdictCacheReader VerdictCacheReader,
    IVerdictCacheWriter VerdictCacheWriter,
    AiModelIdentity ModelIdentity,
    Func<CancellationToken, Task<TimeSpan>> GetPollInterval,
    Func<CancellationToken, Task<bool>> GetTitleNormalizationEnabled);
