using Arbitarr.Ai;
using Arbitarr.Ai.Normalization;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Data.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Arbitarr.Host;

/// <summary>
/// Background hosted service that drives <see cref="ClassifierWorker.ClassifyAndCacheAsync"/>
/// against whatever this process has recently rendered.
///
/// <para>
/// Mirrors <see cref="Arbitarr.Core.Caching.RefreshWorker"/>'s shape: a fixed-deps constructor for
/// tests, an <see cref="IServiceScopeFactory"/>-based constructor for Host wiring (each cycle resolves
/// a fresh scope so the EF-backed verdict cache reader/writer never outlive one cycle), and a shared
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

    /// <summary>
    /// Constructs a worker over fixed dependencies. Used by tests (fakes, injected clock).
    /// </summary>
    /// <param name="eventSink">
    /// Optional, and last in the list, so every construction that predates arb-itw still compiles
    /// and simply records nothing — a worker that cannot record must still classify. Tests that
    /// assert on the recorded cycle pass one explicitly.
    /// </param>
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
        ILogger? logger = null,
        IEventSink? eventSink = null)
        : this(
            () => (new ClassifierPollingWorkerDependencies(
                classifierWorker, releaseLookup, verdictCacheReader, verdictCacheWriter, modelIdentity,
                getPollInterval, getTitleNormalizationEnabled, eventSink), null),
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
    ///
    /// <para><b>#112: <see cref="AiModelIdentity"/> comes from the per-cycle SCOPE now, and no
    /// longer from a captured singleton.</b> The identity follows the resolved Ollama model, which
    /// an operator can change from the Settings page at any time — captured once at construction it
    /// would have pinned the boot-time model name into every verdict cache key for the life of the
    /// process, so a model change would keep writing and reading verdicts under a model that was no
    /// longer being asked (the exact invalidation R17 requires). Taking it from the scope the cycle
    /// already creates costs nothing extra: that scope exists for the cache reader/writer anyway.</para>
    /// </summary>
    public ClassifierPollingWorker(
        IServiceScopeFactory scopeFactory,
        InMemoryReleaseLookup releaseLookup,
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
                        provider.GetRequiredService<AiModelIdentity>(),
                        settingsReader.GetClassifierPollIntervalAsync,
                        settingsReader.GetTitleNormalizationEnabledAsync,
                        // Resolved from the per-cycle scope alongside everything else. It is
                        // optional in the record but required here: in the Host the sink is always
                        // registered, and silently recording nothing in production because a
                        // registration was missed is the failure this would hide.
                        provider.GetRequiredService<IEventSink>());
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

        var classified = 0;
        var failed = 0;
        var rewritten = 0;

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

                // The re-read IS the success test, for the same reason it is the correctness test
                // above: a fail-open classification returns normally and writes nothing, so
                // counting attempts rather than cache hits would report every failed cycle as a
                // fully successful one — exactly the silent failure the WorkerCycle row exists to
                // make visible.
                if (cached is null)
                {
                    failed++;
                }
                else
                {
                    classified++;
                }
            }

            if (rewrittenTitle is not null && cached is not null)
            {
                rewritten++;
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

        WarnOnFailures(classified, failed);

        await RecordCycleAsync(deps, classified, failed, rewritten, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One Warning per cycle that had failures, and none for a clean cycle (arb-1of).
    ///
    /// <para>
    /// The WorkerCycle event added by arb-itw already records the same counts, but only onto the
    /// Activity surface. An operator diagnosing "the classifier is doing nothing" reads the Logs
    /// tab and docker logs, where a fail-open classifier is otherwise completely silent — it caches
    /// nothing, throws nothing, and the search keeps serving. This is the line that makes a total
    /// model outage visible there. The event text is left exactly as arb-itw wrote it.
    /// </para>
    ///
    /// <para>
    /// COUNTS ONLY, and NOT the failing error. That is a deliberate limit, not an oversight: no
    /// exception reaches this layer to report. <c>ReleaseClassifier.TryClassifyAsync</c> catches
    /// and discards it, <c>ClassifierWorker.ClassifyAndCacheAsync</c> returns void, and the
    /// <c>failed</c> counter above is incremented from a null cache RE-READ rather than from any
    /// error. Carrying the last error's type and message would mean changing that fail-open
    /// contract across Arbitarr.Ai, which is bead arb-p94g's, not this one's. Until it lands, the
    /// counts are the whole of what this layer honestly knows.
    /// </para>
    ///
    /// <para>
    /// The same no-identity rule as the event applies: never a title, GUID, source name, prompt or
    /// URL. The counts cannot carry any of them, which is the other reason not to reach for the
    /// error text here — an exception message from an HTTP client is exactly the shape that would.
    /// </para>
    /// </summary>
    private void WarnOnFailures(int classified, int failed)
    {
        if (failed == 0)
        {
            return;
        }

        _logger.LogWarning(
            "Classifier cycle classified {Classified} of {Attempted} candidates; {Failed} attempt(s) failed. " +
            "The classifier fails open, so searches are unaffected and the releases are left unclassified.",
            classified,
            classified + failed,
            failed);
    }

    /// <summary>
    /// Records ONE event per cycle that actually did something (arb-itw).
    ///
    /// This applies <see cref="Arbitarr.Core.Caching.RefreshWorker"/>'s rule verbatim: record WHAT
    /// THE CYCLE DID, never merely that it ticked. A cycle whose candidates were all already
    /// classified did no work and records nothing, which is why this is reached with every
    /// counter at zero and returns — an event log that records every tick is docker logs with
    /// extra steps. The empty snapshot case returns even earlier, before any of this.
    ///
    /// COUNTS ONLY — never a title, query, source host or key. The same constraint RefreshWorker's
    /// emission works under, and the reason <c>EventEntry</c> has no field shaped like one.
    ///
    /// Kind mirrors RefreshWorker's split: anything successfully classified answers the reader's
    /// real question and is not merely a tick, but a cycle that attempted work and classified none
    /// of it is the case that earns a row precisely because it is the shape a silent failure takes.
    /// Both are WorkerCycle here — unlike RefreshWorker there is no SnapshotRefreshed-equivalent
    /// kind for "the classifier produced verdicts", and inventing one would mean adding a
    /// RecordedEventKind plus its mapping for a distinction the Activity reader does not draw.
    /// </summary>
    private static async Task RecordCycleAsync(
        ClassifierPollingWorkerDependencies deps,
        int classified,
        int failed,
        int rewritten,
        CancellationToken cancellationToken)
    {
        if (deps.EventSink is null || (classified == 0 && failed == 0))
        {
            return;
        }

        var attempted = classified + failed;
        var summary = classified > 0
            ? $"Classifier cycle classified {classified} of {attempted} candidates"
            : $"Classifier cycle classified nothing ({failed} of {attempted} attempts failed)";

        // Stated only when non-zero: "0 titles normalized" on every cycle of an instance with
        // normalization switched off is noise that makes the number stop being read.
        var reason = rewritten > 0
            ? $"{rewritten} title(s) normalized this cycle"
            : null;

        await deps.EventSink.RecordAsync(
            RecordedEventKind.WorkerCycle,
            summary,
            reason: reason,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The per-cycle collaborators a <see cref="ClassifierPollingWorker"/> needs: resolved once per
/// cycle from a DI scope in the Host, or supplied directly (fakes) in tests.
/// </summary>
/// <param name="EventSink">
/// Where the per-cycle <c>WorkerCycle</c> event goes (arb-itw). Optional, defaulting to null, so
/// the existing test constructions that predate it keep compiling and simply record nothing — a
/// worker that cannot record must still classify. The Host always supplies it.
/// </param>
public sealed record ClassifierPollingWorkerDependencies(
    ClassifierWorker ClassifierWorker,
    InMemoryReleaseLookup ReleaseLookup,
    IVerdictCacheReader VerdictCacheReader,
    IVerdictCacheWriter VerdictCacheWriter,
    AiModelIdentity ModelIdentity,
    Func<CancellationToken, Task<TimeSpan>> GetPollInterval,
    Func<CancellationToken, Task<bool>> GetTitleNormalizationEnabled,
    IEventSink? EventSink = null);
