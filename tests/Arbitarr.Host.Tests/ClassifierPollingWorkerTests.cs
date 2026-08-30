using System.Collections.Concurrent;
using Arbitarr.Ai;
using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Filtering;
using Arbitarr.Core.Releases;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// Drives <see cref="ClassifierPollingWorker"/> cycles directly over fakes. The worker is the only
/// production caller of <see cref="ClassifierWorker.ClassifyAndCacheAsync"/> (verify-m5 V1) and the
/// only producer of cached title rewrites (V2, R17), so these pin what one cycle does and does not
/// touch.
/// </summary>
public sealed class ClassifierPollingWorkerTests
{
    private const string SourceName = "TestSource";
    private const string NoisyTitle = "Movie 2024 1080p RARBG";
    private const string StrippedTitle = "Movie 2024 1080p";

    private static readonly AiModelIdentity Identity = new("test-model", "digest-1", "v1");

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

    private static string KeyFor(string title, string guid) =>
        VerdictCacheKey.Compute(Release(title, guid).Candidate, SourceName, Identity.ModelName, Identity.ModelDigest, Identity.PromptVersion);

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
        public Harness(bool normalizationEnabled, bool clientThrows = false, TimeSpan? pollInterval = null)
        {
            Client = new CountingOllamaClient(clientThrows);
            var classifierWorker = new ClassifierWorker(new ReleaseClassifier(Client), Cache, Identity, SourceName);
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
                SourceName);
        }

        public InMemoryReleaseLookup Lookup { get; } = new();
        public InMemoryVerdictCache Cache { get; } = new();
        public CountingOllamaClient Client { get; }
        public ClassifierPollingWorker Worker { get; }
        public int NormalizationReads { get; private set; }
    }

    private sealed class CountingOllamaClient : IOllamaClient
    {
        private readonly bool _throws;
        private int _calls;

        public CountingOllamaClient(bool throws) => _throws = throws;

        public int Calls => Volatile.Read(ref _calls);

        public Task<OllamaVerdict> ClassifyAsync(ReleaseCandidate candidate, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (_throws)
            {
                throw new HttpRequestException("simulated model failure");
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
