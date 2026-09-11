using Arbitarr.Api.Rendering;
using Arbitarr.Api.Search;
using Arbitarr.Core.Releases;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// arb-zwk (root cause of the arb-agh flake): a release-lookup STORE failure on the download path
/// still answers as a miss, but no longer SILENTLY.
///
/// <para><b>Why the silence was the defect, not the degrade.</b> Answering a store fault as a miss
/// is correct — it leaves the caller exactly where the memory-only implementation left them, a 404
/// rather than a 500. But returning null with nothing logged made a broken store
/// indistinguishable from an ordinary expired-link miss: the download 404s, the dashboard stays
/// green, and the only symptom is an intermittent failure with nothing to attribute it to. That is
/// precisely how arb-agh presented. The degrade stays; the invisibility does not.</para>
///
/// <para><b>Positive control on every assertion</b> (CLAUDE.md §4). The throwing case is paired with
/// a non-throwing one proving the store is actually consulted, so "a warning was logged" cannot pass
/// against a lookup that never reached the store, and "no warning" cannot pass vacuously.</para>
/// </summary>
public sealed class PersistentReleaseLookupFallbackLoggingTests
{
    private const string ProxyGuid = "arb-zwk-proxy-guid";

    private static ReleaseCandidate Candidate() => new()
    {
        Title = "Some.Show.S01E01.1080p.WEB-DL",
        Guid = "arb-zwk-release-guid",
        PubDate = DateTimeOffset.UnixEpoch,
        Link = new Uri("https://indexer.example.invalid/get/arb-zwk"),
    };

    /// <summary>
    /// <b>THE POINT OF THE ITEM.</b> A throwing store yields a miss (the 404 the download proxy
    /// renders) AND a warning carrying the exception.
    /// </summary>
    [Fact]
    public async Task A_throwing_store_answers_as_a_miss_and_logs_a_warning()
    {
        var logger = new CapturingLogger();
        var store = new ThrowingStore();
        var lookup = new PersistentReleaseLookup(new InMemoryReleaseLookup(), store.FindAsync, logger);

        var resolved = await lookup.FindAsync(ProxyGuid);

        // The degrade: a miss, which the download proxy turns into a 404 — not a 500.
        Assert.Null(resolved);
        // NON-VACUITY: the store really was consulted, so the null came from the failure path.
        Assert.True(store.WasCalled);
        // The failure is now attributable.
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("Release lookup store failed", warning.Message, StringComparison.Ordinal);
        Assert.Same(store.Thrown, warning.Exception);
    }

    /// <summary>
    /// <b>POSITIVE CONTROL.</b> A healthy store resolves and logs NOTHING. Without this, the test
    /// above would pass against an implementation that warned on every lookup — which would bury the
    /// real signal in noise just as effectively as silence hid it.
    /// </summary>
    [Fact]
    public async Task A_healthy_store_resolves_without_logging_a_warning()
    {
        var logger = new CapturingLogger();
        var stored = new StoredRelease(ProxyGuid, "TestSource", Candidate());
        var lookup = new PersistentReleaseLookup(
            new InMemoryReleaseLookup(),
            (_, _) => Task.FromResult<StoredRelease?>(stored),
            logger);

        var resolved = await lookup.FindAsync(ProxyGuid);

        // Detectability: this lookup genuinely resolves, so "no warning" is about a working store
        // rather than about a path that never ran. The resolved ProxyGuid is DERIVED from the
        // candidate rather than echoing the key that was looked up, so the candidate's own identity
        // is what proves the right release came back.
        Assert.NotNull(resolved);
        Assert.Equal(Candidate().Guid, resolved!.Candidate.Guid);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// A MEMORY hit never reaches the store, so it cannot log either. This pins that the warning is
    /// tied to an actual store failure rather than to the lookup being called at all.
    /// </summary>
    [Fact]
    public async Task A_memory_hit_never_consults_the_store_and_logs_nothing()
    {
        var logger = new CapturingLogger();
        var store = new ThrowingStore();
        var memory = new InMemoryReleaseLookup();
        var rendered = new RenderedRelease("TestSource", Candidate());
        memory.RecordRange(new[] { rendered });

        var lookup = new PersistentReleaseLookup(memory, store.FindAsync, logger);

        var resolved = await lookup.FindAsync(rendered.ProxyGuid);

        Assert.NotNull(resolved);
        // The store would have thrown had it been consulted, so this proves the memory tier answered.
        Assert.False(store.WasCalled);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// The warning carries the proxy guid — which the caller just presented and which already
    /// appears in the request line — but never the release payload or its source URL. These lines
    /// land in the persistent log store served at <c>/api/admin/logs</c> (CLAUDE.md §1).
    ///
    /// <para>Positive control: the link is first shown findable in the candidate, so the absence
    /// assertion cannot pass against a release that never carried one.</para>
    /// </summary>
    [Fact]
    public async Task The_warning_carries_no_payload_or_link()
    {
        var link = Candidate().Link!.ToString();
        Assert.Contains("indexer.example.invalid", link, StringComparison.Ordinal);

        var logger = new CapturingLogger();
        var lookup = new PersistentReleaseLookup(
            new InMemoryReleaseLookup(),
            new ThrowingStore().FindAsync,
            logger);

        await lookup.FindAsync(ProxyGuid);

        var warning = Assert.Single(logger.Warnings);
        Assert.DoesNotContain(link, warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("indexer.example.invalid", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Candidate().Title, warning.Message, StringComparison.Ordinal);
        // It still says which link failed, which is what makes the line actionable.
        Assert.Contains(ProxyGuid, warning.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A CANCELLED lookup is not a store fault: it must propagate rather than being logged as one
    /// and converted into a miss, which would report "no such release" for a request that was simply
    /// abandoned.
    /// </summary>
    [Fact]
    public async Task A_cancellation_propagates_rather_than_becoming_a_logged_miss()
    {
        var logger = new CapturingLogger();
        var lookup = new PersistentReleaseLookup(
            new InMemoryReleaseLookup(),
            (_, _) => throw new OperationCanceledException(),
            logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup.FindAsync(ProxyGuid));

        Assert.Empty(logger.Warnings);
    }

    /// <summary>Stands in for a store that cannot answer — a locked or corrupt SQLite file.</summary>
    private sealed class ThrowingStore
    {
        public bool WasCalled { get; private set; }

        public InvalidOperationException Thrown { get; } = new("release lookup store unavailable");

        public Task<StoredRelease?> FindAsync(string proxyGuid, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw Thrown;
        }
    }

    /// <summary>
    /// Captures the FORMATTED message, which is what a real provider renders into the log store — a
    /// structured argument carrying a URL would leak through exactly that rendering, so asserting on
    /// the formatted text is what makes the no-payload test above meaningful.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add((formatter(state, exception), exception));
            }
        }
    }
}
