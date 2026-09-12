using System.Collections.Concurrent;
using Arbitarr.Ai;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// Drives <see cref="ClassifierPollingWorker"/> cycles directly over fakes. The worker is the only
/// production caller of <see cref="ClassifierWorker.ClassifyAndCacheAsync"/> and the
/// only producer of cached title rewrites (R17), so these pin what one cycle does and does not
/// touch.
/// </summary>
public sealed class ClassifierPollingWorkerTests
{
    private const string SourceName = "TestSource";
    private const string NoisyTitle = "Movie 2024 1080p RARBG";
    private const string StrippedTitle = "Movie 2024 1080p";

    private static readonly AiModelIdentity Identity = new("test-model", "digest-1", "v1", "t0-s42");

    [Fact]
    public async Task RunCycle_UncachedCandidate_ClassifiesAndCachesVerdict()
    {
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(1, harness.Client.Calls);
        var cached = harness.Cache.TryGet(KeyFor(NoisyTitle, "g1"));
        Assert.NotNull(cached);
        Assert.Equal(Verdict.Accept, cached.Verdict);
        Assert.Null(cached.RewrittenTitle);
    }

    [Fact]
    public async Task RunCycle_AlreadyCached_NormalizationOff_DoesNotReclassify()
    {
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Cache.Seed(KeyFor(NoisyTitle, "g1"), new CachedVerdict(Verdict.Reject, 0.7));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(0, harness.Client.Calls);
        Assert.Equal(0, harness.Cache.Puts);
    }

    [Fact]
    public async Task RunCycle_NormalizationOn_CachesRewriteAlongsideVerdict()
    {
        var harness = new Harness(normalizationEnabled: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(1, harness.Client.Calls);
        var cached = harness.Cache.TryGet(KeyFor(NoisyTitle, "g1"));
        Assert.NotNull(cached);
        Assert.Equal(Verdict.Accept, cached.Verdict);
        Assert.Equal(StrippedTitle, cached.RewrittenTitle);
    }

    [Fact]
    public async Task RunCycle_NormalizationOn_CachedWithoutRewrite_BackfillsRewriteWithoutReclassifying()
    {
        var harness = new Harness(normalizationEnabled: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Cache.Seed(KeyFor(NoisyTitle, "g1"), new CachedVerdict(Verdict.Reject, 0.7));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(0, harness.Client.Calls);
        var cached = harness.Cache.TryGet(KeyFor(NoisyTitle, "g1"));
        Assert.NotNull(cached);
        Assert.Equal(Verdict.Reject, cached.Verdict);
        Assert.Equal(0.7, cached.Confidence);
        Assert.Equal(StrippedTitle, cached.RewrittenTitle);
    }

    [Fact]
    public async Task RunCycle_NormalizationOn_CachedWithRewrite_IsSkipped()
    {
        var harness = new Harness(normalizationEnabled: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Cache.Seed(KeyFor(NoisyTitle, "g1"), new CachedVerdict(Verdict.Accept, 0.9, StrippedTitle));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(0, harness.Client.Calls);
        Assert.Equal(0, harness.Cache.Puts);
    }

    [Fact]
    public async Task RunCycle_NormalizationOn_TitleWithNothingToStrip_CachesNoRewrite()
    {
        var harness = new Harness(normalizationEnabled: true);
        harness.Lookup.Record(Release(StrippedTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        var cached = harness.Cache.TryGet(KeyFor(StrippedTitle, "g1"));
        Assert.NotNull(cached);
        Assert.Null(cached.RewrittenTitle);
        Assert.Equal(1, harness.Cache.Puts);
    }

    /// <summary>
    /// The lookup records what was rendered, which after a cached rewrite carries the rewritten
    /// title. The worker must still key on the upstream title so it agrees with the render path
    /// (VerdictCacheKey keys on OriginalTitle); otherwise every cycle would re-classify a release
    /// that is already cached.
    /// </summary>
    [Fact]
    public async Task RunCycle_SnapshotHoldsRewrittenRelease_KeysOnOriginalTitle()
    {
        var harness = new Harness(normalizationEnabled: true);
        var rewritten = Release(NoisyTitle, "g1").Candidate.WithTitle(StrippedTitle, NoisyTitle);
        harness.Lookup.Record(new RenderedRelease(SourceName, rewritten));
        harness.Cache.Seed(KeyFor(NoisyTitle, "g1"), new CachedVerdict(Verdict.Accept, 0.9, StrippedTitle));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(0, harness.Client.Calls);
        Assert.Equal(0, harness.Cache.Puts);
    }

    [Fact]
    public async Task RunCycle_ClassifierFailsOpen_CachesNothing()
    {
        var harness = new Harness(normalizationEnabled: true, clientThrows: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(1, harness.Client.Calls);
        Assert.Null(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1")));
        Assert.Equal(0, harness.Cache.Puts);
    }

    [Fact]
    public async Task RunCycle_EmptyLookup_DoesNotConsultSettingsOrModel()
    {
        var harness = new Harness(normalizationEnabled: true);

        await harness.Worker.RunCycleAsync();

        Assert.Equal(0, harness.Client.Calls);
        Assert.Equal(0, harness.NormalizationReads);
    }

    /// <summary>P1 fail-open: a cycle that throws is logged and the loop keeps polling.</summary>
    [Fact]
    public async Task ExecuteAsync_CycleThrows_LoopContinuesOnNextTick()
    {
        var harness = new Harness(normalizationEnabled: false, pollInterval: TimeSpan.FromMilliseconds(1));
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Cache.FailNextTryGet();

        await harness.Worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (harness.Client.Calls == 0)
            {
                await Task.Delay(5, timeout.Token);
            }
        }
        finally
        {
            await harness.Worker.StopAsync(CancellationToken.None);
        }

        Assert.True(harness.Cache.TryGetCalls >= 2, "the failed cycle should have been retried");
        Assert.NotNull(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1")));
    }

    /// <summary>
    /// The EF HasMaxLength bound is advisory on SQLite, so the producer must cap the rewrite
    /// itself: an upstream title of arbitrary length must never persist an unbounded rewrite.
    /// </summary>
    [Fact]
    public async Task RunCycle_NormalizationOn_OversizedTitle_CachesRewriteCappedAtLimit()
    {
        var harness = new Harness(normalizationEnabled: true);
        var oversized = new string('a', VerdictCacheLimits.MaxRewrittenTitleLength + 500) + " RARBG";
        harness.Lookup.Record(Release(oversized, "g1"));

        await harness.Worker.RunCycleAsync();

        var cached = harness.Cache.TryGet(KeyFor(oversized, "g1"));
        Assert.NotNull(cached);
        Assert.NotNull(cached.RewrittenTitle);
        Assert.Equal(VerdictCacheLimits.MaxRewrittenTitleLength, cached.RewrittenTitle.Length);
    }

    /// <summary>
    /// The worker must key each verdict under the release's own upstream
    /// <see cref="RenderedRelease.SourceName"/> (what FilterStage reads with), not a fixed name of
    /// its own. Two releases with identical candidates but different sources get distinct entries,
    /// and a second cycle finds the first cycle's writes instead of re-classifying.
    /// </summary>
    [Fact]
    public async Task RunCycle_KeysVerdictUnderEachReleasesOwnSourceName()
    {
        const string otherSource = "NZBHydra2";
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Lookup.Record(new RenderedRelease(otherSource, Release(NoisyTitle, "g1").Candidate));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(2, harness.Client.Calls);
        Assert.NotNull(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1")));
        Assert.NotNull(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1", otherSource)));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(2, harness.Client.Calls);
    }

    // ---- arb-itw: the cycle records what it DID ------------------------------------------------
    //
    // RefreshWorker's rule, applied verbatim: record what the cycle DID, never merely that it
    // ticked. The bead was written believing this worker recorded nothing at all, which was
    // correct — RefreshWorker already emitted WorkerCycle, this one did not.
    //
    // COUNTS ONLY. No title, query, source host or key may reach the event: EventEntry carries no
    // credential by construction, and the Activity surface serving these rows is un-gated
    // (RouteClassification.PublicRead), so a release title leaking here would be publicly
    // readable. The last test below is the guard on exactly that.

    [Fact]
    public async Task RunCycle_WithCandidates_RecordsOneWorkerCycleEventCarryingTheCounts()
    {
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Lookup.Record(new RenderedRelease("NZBHydra2", Release(NoisyTitle, "g1").Candidate));

        await harness.Worker.RunCycleAsync();

        var recorded = Assert.Single(harness.Sink.Events);
        Assert.Equal(RecordedEventKind.WorkerCycle, recorded.Kind);
        Assert.Contains("2", recorded.Summary);
    }

    /// <summary>
    /// A cycle with nothing to do records NOTHING. This is the half of RefreshWorker's rule that
    /// actually costs something to get wrong: the worker polls on a timer, so an event per tick
    /// would write a row every poll interval forever, drown every real event on the Activity feed
    /// and evict genuine history through the retention window — the very flood arb-itw's
    /// coalescing exists to stop, manufactured by the surface meant to report it.
    /// </summary>
    [Fact]
    public async Task RunCycle_EmptyLookup_RecordsNothing()
    {
        var harness = new Harness(normalizationEnabled: false);

        await harness.Worker.RunCycleAsync();

        Assert.Empty(harness.Sink.Events);
    }

    /// <summary>
    /// A cycle where every candidate was already cached did no work either, so it records nothing.
    /// Distinct from the empty-lookup case above: here the worker DID enumerate candidates and
    /// found them all cached, which an implementation keyed on "were there candidates?" rather
    /// than "did anything get classified?" would wrongly report as a cycle worth an event.
    /// </summary>
    [Fact]
    public async Task RunCycle_EverythingAlreadyCached_RecordsNothing()
    {
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Cache.Seed(KeyFor(NoisyTitle, "g1"), new CachedVerdict(Verdict.Accept, 0.9));

        await harness.Worker.RunCycleAsync();

        Assert.Empty(harness.Sink.Events);
    }

    /// <summary>
    /// A cycle where the model failed still records, and says so. The classifier fails OPEN — it
    /// caches nothing and the search keeps serving — so without this event a total model outage is
    /// invisible on the Activity surface: no rows, no errors, indistinguishable from an idle
    /// system. Reporting "classified nothing, 1 of 1 attempts failed" is the whole point.
    /// </summary>
    [Fact]
    public async Task RunCycle_ClassifierFailsOpen_RecordsTheFailureCount()
    {
        var harness = new Harness(normalizationEnabled: false, clientThrows: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        var recorded = Assert.Single(harness.Sink.Events);
        Assert.Equal(RecordedEventKind.WorkerCycle, recorded.Kind);
        Assert.Contains("failed", recorded.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", recorded.Summary);
    }

    /// <summary>
    /// arb-1of: a cycle with failures writes exactly ONE Warning carrying the counts. The
    /// WorkerCycle event records the same numbers, but only onto the Activity surface — an operator
    /// diagnosing "the classifier is doing nothing" reads the Logs tab and docker logs, where a
    /// fail-open classifier is otherwise entirely silent.
    ///
    /// <para>ONE line per cycle, not one per failure: this asserts <c>Single</c> rather than
    /// <c>NotEmpty</c>, because a per-candidate Warning would turn a total model outage into a log
    /// flood at exactly the moment the log is being read.</para>
    /// </summary>
    [Fact]
    public async Task RunCycle_ClassifierFailsOpen_LogsOneWarningCarryingTheFailureCount()
    {
        var harness = new Harness(normalizationEnabled: false, clientThrows: true);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Lookup.Record(Release("Another Movie 2024 1080p RARBG", "g2"));

        await harness.Worker.RunCycleAsync();

        // Positive control: both candidates really were attempted, so "2" below is the worker
        // counting two failures rather than a coincidence of some other number.
        Assert.Equal(2, harness.Client.Calls);

        // arb-s4lg: still exactly ONE *cycle* line. The per-call detail rows alongside it are the
        // rate-capped diagnostics, which is why this filters by the cycle line's text rather than
        // asserting Single over every Warning — the flood this guards against is a cycle line per
        // candidate, and that assertion is unchanged in strength.
        var warning = Assert.Single(harness.Logger.Warnings, w => w.Contains("Classifier cycle", StringComparison.Ordinal));
        Assert.Contains("2", warning, StringComparison.Ordinal);

        // No release identity in the line, the same constraint the WorkerCycle event works under.
        Assert.DoesNotContain(NoisyTitle, warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("g1", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceName, warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// arb-s4lg: the cycle Warning now names the exception TYPES and their counts, so an operator
    /// reading one line knows what broke as well as how much. Two distinct types in one cycle to
    /// prove the breakdown is a real aggregation rather than the single type echoed back.
    /// </summary>
    [Fact]
    public async Task RunCycle_FailuresOfTwoTypes_LogsOneCycleWarningNamingBothTypesAndCounts()
    {
        var harness = new Harness(
            normalizationEnabled: false,
            failureTypes: new Func<Exception>[]
            {
                () => new HttpRequestException("simulated model failure"),
                () => new InvalidOperationException("malformed verdict"),
            });
        harness.Lookup.Record(Release(NoisyTitle, "g1"));
        harness.Lookup.Record(Release("Another Movie 2024 1080p RARBG", "g2"));

        await harness.Worker.RunCycleAsync();

        // Positive control: both candidates were attempted, so the counts below are the worker
        // aggregating two real failures.
        Assert.Equal(2, harness.Client.Calls);

        var cycleWarning = Assert.Single(harness.Logger.Warnings, w => w.Contains("Classifier cycle", StringComparison.Ordinal));
        Assert.Contains($"{nameof(HttpRequestException)}=1", cycleWarning, StringComparison.Ordinal);
        Assert.Contains($"{nameof(InvalidOperationException)}=1", cycleWarning, StringComparison.Ordinal);

        // arb-kwtn: two failures are under MaxDetailedFailuresPerCycle, so nothing was suppressed and
        // the clause is omitted entirely rather than rendering "and 0 more at Debug" — a pointer to
        // Debug rows that do not exist.
        Assert.Contains("2 logged in full above.", cycleWarning, StringComparison.Ordinal);
        Assert.DoesNotContain("more at Debug", cycleWarning, StringComparison.Ordinal);

        // Both per-call details are Warnings (under the cap) plus the one cycle summary.
        Assert.Equal(3, harness.Logger.Warnings.Count);

        // arb-u6b1: pin the two-details-plus-summary split directly, rather than only via the
        // Assert.Single on the cycle line above — that assertion alone would still pass if the
        // count of 3 were made up of, say, zero detail rows and two other unrelated Warnings.
        Assert.Equal(2, harness.Logger.Warnings.Count(w => !w.Contains("Classifier cycle", StringComparison.Ordinal)));

        // Types and counts only — never the release or the source.
        Assert.DoesNotContain(NoisyTitle, cycleWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("g1", cycleWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceName, cycleWarning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The rate cap itself: five failures in one cycle produce exactly THREE per-call Warnings
    /// (<c>MaxDetailedFailuresPerCycle</c>) plus the one cycle summary, with the remaining two
    /// demoted to Debug and counted as suppressed. Without the cap this cycle would write six
    /// Warnings, and a real outage would write one per candidate indefinitely.
    /// </summary>
    [Fact]
    public async Task RunCycle_MoreFailuresThanTheCap_DetailsOnlyTheFirstThreeAtWarning()
    {
        var harness = new Harness(normalizationEnabled: false, clientThrows: true);
        for (var i = 1; i <= 5; i++)
        {
            harness.Lookup.Record(Release($"Movie {i} 2024 1080p", $"g{i}"));
        }

        await harness.Worker.RunCycleAsync();

        // Positive control: all five really were attempted, so the split below is the cap acting
        // rather than candidates never being classified.
        Assert.Equal(5, harness.Client.Calls);

        var detail = harness.Logger.Warnings
            .Where(w => w.Contains("Classification failed", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(3, detail.Count);

        // The suppressed two are demoted, not dropped.
        var demoted = harness.Logger.Debugs
            .Where(d => d.Contains("Classification failed", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, demoted.Count);

        var cycleWarning = Assert.Single(harness.Logger.Warnings, w => w.Contains("Classifier cycle", StringComparison.Ordinal));
        Assert.Contains($"{nameof(HttpRequestException)}=5", cycleWarning, StringComparison.Ordinal);
        Assert.Contains("3 logged in full above and 2 more at Debug", cycleWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// POSITIVE CONTROL for the Warning above (CLAUDE.md §4). "No warning was logged" is vacuous
    /// unless a warning WOULD have been captured had one been written — the test above proves the
    /// recorder catches it, and this proves a clean cycle produces none. Together they show the
    /// line tracks the failure count rather than always or never firing.
    /// </summary>
    [Fact]
    public async Task RunCycle_WithNoFailures_LogsNoWarning()
    {
        var harness = new Harness(normalizationEnabled: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        // Positive control: the cycle did real work, so the absence below is a clean cycle staying
        // quiet rather than a cycle that never ran.
        Assert.Equal(1, harness.Client.Calls);
        Assert.NotNull(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1")));

        Assert.Empty(harness.Logger.Warnings);
    }

    /// <summary>
    /// A worker constructed without a sink still classifies. Recording is a diagnostic, never a
    /// precondition for the work: a missing registration must not stop releases being classified.
    /// </summary>
    [Fact]
    public async Task RunCycle_WithoutAnEventSink_StillClassifies()
    {
        var harness = new Harness(normalizationEnabled: false, withSink: false);
        harness.Lookup.Record(Release(NoisyTitle, "g1"));

        await harness.Worker.RunCycleAsync();

        Assert.Equal(1, harness.Client.Calls);
        Assert.NotNull(harness.Cache.TryGet(KeyFor(NoisyTitle, "g1")));
    }

    /// <summary>
    /// NO RELEASE IDENTITY REACHES THE EVENT — not the title, not the guid, not the source name.
    /// These rows are served un-gated by GET /api/activity, so anything recorded here is public.
    ///
    /// The title used is deliberately distinctive and the assertion sweeps EVERY field of the
    /// event rather than only the summary, because a "counts only" implementation is most likely
    /// to slip identity into Reason or Detail while leaving the summary clean.
    /// </summary>
    [Fact]
    public async Task RunCycle_RecordsNoReleaseIdentity()
    {
        const string secretish = "Some.Very.Distinctive.Release.Name.2024";
        var harness = new Harness(normalizationEnabled: true);
        harness.Lookup.Record(Release(secretish, "guid-abc-123"));

        await harness.Worker.RunCycleAsync();

        var recorded = Assert.Single(harness.Sink.Events);

        // Positive control: the assertions below are only meaningful if the title was actually in
        // play this cycle. It was — the classifier saw it — so a "does not contain" that passes
        // here is passing because the worker withheld it, not because it never existed.
        Assert.Equal(1, harness.Client.Calls);

        var everyField = string.Join(
            "\n", recorded.Summary, recorded.Reason, recorded.SourceDisplayName, recorded.Detail);
        Assert.DoesNotContain(secretish, everyField, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("guid-abc-123", everyField, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SourceName, everyField, StringComparison.OrdinalIgnoreCase);
    }

    private static string KeyFor(string title, string guid, string sourceName = SourceName) =>
        VerdictCacheKey.Compute(Release(title, guid).Candidate, sourceName, Identity.ModelName, Identity.ModelDigest, Identity.PromptVersion, Identity.DecodingIdentity);

    private static RenderedRelease Release(string title, string guid) => new(
        SourceName,
        new ReleaseCandidate
        {
            Title = title,
            Guid = guid,
            PubDate = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero),
            Size = 1234,
            Link = new Uri("https://example.invalid/r"),
        });

    private sealed class Harness
    {
        public Harness(
            bool normalizationEnabled,
            bool clientThrows = false,
            TimeSpan? pollInterval = null,
            bool withSink = true,
            IReadOnlyList<Func<Exception>>? failureTypes = null)
        {
            Client = new CountingOllamaClient(clientThrows, failureTypes);
            // arb-s4lg: the classifier gets the SAME recorder as the worker, so a test sees the
            // per-call detail rows and the per-cycle summary in one place — which is the only way
            // to assert that the cap suppressed the rows it claims to have suppressed. Production
            // wires both from DI; here one logger stands in for the whole Arbitarr category.
            var classifierWorker = new ClassifierWorker(
                new ReleaseClassifier(Client, new TypedRecordingLogger<ReleaseClassifier>(Logger)), Cache, Identity);
            Worker = new ClassifierPollingWorker(
                classifierWorker,
                Lookup,
                Cache,
                Cache,
                Identity,
                _ => Task.FromResult(pollInterval ?? TimeSpan.FromMinutes(1)),
                _ =>
                {
                    NormalizationReads++;
                    return Task.FromResult(normalizationEnabled);
                },
                TimeProvider.System,
                titleNormalizer: null,
                logger: Logger,
                eventSink: withSink ? Sink : null);
        }

        public InMemoryReleaseLookup Lookup { get; } = new();
        public InMemoryVerdictCache Cache { get; } = new();
        public CountingOllamaClient Client { get; }
        public RecordingEventSink Sink { get; } = new();
        public RecordingLogger Logger { get; } = new();
        public ClassifierPollingWorker Worker { get; }
        public int NormalizationReads { get; private set; }
    }

    /// <summary>
    /// Captures Warning-and-above lines so a test can assert what the operator was told (arb-1of).
    /// Records the RENDERED text, because that is what reaches the Logs tab and docker logs.
    /// </summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Warnings =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public IReadOnlyList<string> Debugs =>
            _entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // arb-s4lg: every level, so the cap's demoted rows are observable. Gating at Warning here
        // would make "the rest went to Debug" untestable — and an absence assertion over rows the
        // recorder refused to accept is exactly the vacuous shape CLAUDE.md §4 forbids.
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            _entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Adapts the untyped <see cref="RecordingLogger"/> to the <see cref="ILogger{TCategoryName}"/>
    /// the classifier wants, so both layers' rows land in one recorder in cycle order.
    /// </summary>
    private sealed class TypedRecordingLogger<T> : ILogger<T>
    {
        private readonly RecordingLogger _inner;

        public TypedRecordingLogger(RecordingLogger inner) => _inner = inner;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>
    /// Captures what the worker recorded, whole, so a test can assert on EVERY field rather than
    /// only the summary — which is what the no-identity sweep above needs.
    /// </summary>
    private sealed class RecordingEventSink : IEventSink
    {
        private readonly ConcurrentQueue<RecordedEvent> _events = new();

        public IReadOnlyList<RecordedEvent> Events => _events.ToList();

        public ValueTask RecordAsync(
            RecordedEventKind kind,
            string summary,
            string? reason = null,
            string? sourceDisplayName = null,
            string? detail = null,
            CancellationToken cancellationToken = default,
            bool? shadowMode = null)
        {
            _events.Enqueue(new RecordedEvent(kind, summary, reason, sourceDisplayName, detail, shadowMode));
            return ValueTask.CompletedTask;
        }

        // RecordBatchAsync is left to the interface's default, which fans out to RecordAsync
        // above: the worker records one event per cycle and never batches, so overriding it here
        // would add an untested path that no test drives.
    }

    private sealed class CountingOllamaClient : IOllamaClient
    {
        private readonly bool _throws;
        private readonly IReadOnlyList<Func<Exception>>? _failureTypes;
        private int _calls;

        public CountingOllamaClient(bool throws, IReadOnlyList<Func<Exception>>? failureTypes = null)
        {
            _throws = throws || failureTypes is { Count: > 0 };
            _failureTypes = failureTypes;
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (_throws)
            {
                // arb-s4lg: cycling the supplied factories lets one cycle produce more than one
                // exception TYPE, which is what the breakdown assertion needs.
                throw _failureTypes is { Count: > 0 }
                    ? _failureTypes[(call - 1) % _failureTypes.Count]()
                    : new HttpRequestException("simulated model failure");
            }

            return Task.FromResult(new OllamaVerdict("accept", 0.9));
        }
    }

    private sealed class InMemoryVerdictCache : IVerdictCacheReader, IVerdictCacheWriter
    {
        private readonly ConcurrentDictionary<string, CachedVerdict> _entries = new();
        private int _failNextTryGet;
        private int _puts;
        private int _tryGetCalls;

        public int Puts => Volatile.Read(ref _puts);
        public int TryGetCalls => Volatile.Read(ref _tryGetCalls);

        public void Seed(string key, CachedVerdict verdict) => _entries[key] = verdict;

        public void FailNextTryGet() => Volatile.Write(ref _failNextTryGet, 1);

        public CachedVerdict? TryGet(string releaseKeyHash)
        {
            Interlocked.Increment(ref _tryGetCalls);
            if (Interlocked.Exchange(ref _failNextTryGet, 0) == 1)
            {
                throw new InvalidOperationException("simulated cache outage");
            }

            return _entries.TryGetValue(releaseKeyHash, out var cached) ? cached : null;
        }

        public Task PutAsync(
            string releaseKeyHash, string modelName, string modelDigest, string promptVersion,
            Verdict verdict, double confidence, string? rewrittenTitle = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _puts);
            _entries.AddOrUpdate(
                releaseKeyHash,
                _ => new CachedVerdict(verdict, confidence, rewrittenTitle),
                (_, existing) => new CachedVerdict(verdict, confidence, rewrittenTitle ?? existing.RewrittenTitle));
            return Task.CompletedTask;
        }
    }
}
