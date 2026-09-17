using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Settings;
using Arbitarr.Host.Maintenance;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-km0a: <see cref="MaintenanceHostedService.FirstPassCompleted"/> is the seam an integration
/// host awaits so a test body no longer races the startup maintenance pass. These three cases pin
/// the whole of its contract, and each is written so it FAILS if the seam regresses in the
/// corresponding direction.
///
/// <para><b>Why the completion is worth three tests rather than one.</b> A signal that completes
/// only on the happy path is worse than no signal: a waiter would hang on exactly the runs where
/// the first pass misbehaved, which is the case it exists to cover. So "not yet" (it does not
/// publish early), "threw" and "stopped" are separate properties, and each is asserted on its own.
/// This mirrors the <c>SqliteLoggerProvider.DrainCompleted</c> precedent, whose publication is in a
/// <c>finally</c> for the same reason.</para>
///
/// <para><b>No sleeps and no retries anywhere here.</b> The gate below is a
/// <see cref="TaskCompletionSource"/> the test releases deliberately, so "has it published yet" is
/// answered by inspecting <see cref="Task.IsCompleted"/> at a moment the test CONTROLS rather than
/// by waiting to see whether it shows up. Where a wait is unavoidable it is bounded by
/// <see cref="CompletionWait"/> and its elapsing FAILS the test with a message naming what did not
/// happen — never a silent continue.</para>
/// </summary>
public sealed class MaintenanceHostedServiceFirstPassCompletionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The bound on every wait in this class. Generous, because it exists to turn a HANG into a
    /// named failure rather than to police how fast a pass runs on a loaded parallel runner.
    /// </summary>
    private static readonly TimeSpan CompletionWait = TimeSpan.FromSeconds(30);

    private readonly SqliteTestDatabase _database = new("arbitarr-maintenance-first-pass-test");

    public MaintenanceHostedServiceFirstPassCompletionTests()
    {
        using var context = CreateContext();
        context.Database.Migrate();
    }

    public void Dispose() => _database.Dispose();

    /// <summary>
    /// The completion is NOT published while the first pass is still running, and IS published once
    /// it finishes.
    ///
    /// <para><b>The negative half is the load-bearing one</b>, and it is what a planted-fixture
    /// style test would miss. A <c>FirstPassCompleted</c> wired to <c>Task.CompletedTask</c>, or
    /// published from <c>StartAsync</c> rather than after the pass, satisfies "it completes" — and
    /// would leave every caller racing the pass exactly as before while reporting green. So the
    /// first assertion is that it has NOT completed at a moment the test knows the pass is still
    /// in flight, which is established by the gate rather than inferred from timing.</para>
    /// </summary>
    [Fact]
    public async Task The_completion_publishes_only_once_the_first_pass_has_finished()
    {
        var clock = new FakeTimeProvider(Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = BuildProvider(clock, gate.Task);
        var service = new MaintenanceHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock);

        await service.StartAsync(CancellationToken.None);

        try
        {
            // The pass is blocked inside the gated step, so this is not a "not yet observed"
            // reading taken hopefully early: the pass demonstrably cannot have finished.
            await WaitUntilBlockedAsync(provider);
            Assert.False(
                service.FirstPassCompleted.IsCompleted,
                "FirstPassCompleted published while the first pass was still blocked in one of its " +
                "steps, so awaiting it does not establish the pass has finished.");

            gate.SetResult();

            await AssertCompletesAsync(
                service.FirstPassCompleted,
                "the first pass was released and ran to the end");
        }
        finally
        {
            gate.TrySetResult();
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A first pass whose step THROWS still publishes the completion.
    ///
    /// <para>This is the case a naive implementation loses: publishing after the last step, rather
    /// than from a <c>finally</c>, strands every waiter precisely when the pass went wrong — and a
    /// failing startup backup is the situation the seam was built for, so losing it there would
    /// make the whole change decorative. The thrown exception is absorbed by the service's own
    /// per-step catch (which logs and retries next cycle), so the assertion is that the completion
    /// arrives, not that the service faults.</para>
    /// </summary>
    [Fact]
    public async Task The_completion_publishes_when_the_first_pass_throws()
    {
        var clock = new FakeTimeProvider(Now);

        using var provider = BuildProvider(clock, gatedStep: null, throwFromStep: true);
        var service = new MaintenanceHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock);

        await service.StartAsync(CancellationToken.None);

        try
        {
            await AssertCompletesAsync(
                service.FirstPassCompleted,
                "a step of the first pass threw and the completion is published from a finally");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A host STOPPED mid-first-pass does not strand its waiter.
    ///
    /// <para><b>This is the stranding test.</b> Every cancellation exit of the first pass — the four
    /// per-step <c>OperationCanceledException</c> arms, the trailing delay's, and
    /// <c>ResolveIntervalAsync</c> failing before a pass is entered at all — has to publish, because
    /// an integration host that is torn down while its first pass is in flight would otherwise leave
    /// the awaiting test hanging until its own bound elapsed. The bound here is a cancellation
    /// token, NOT a sleep: the assertion is that the completion arrives within it, and a timeout is
    /// the failure rather than something the test rides out.</para>
    ///
    /// <para>The gate is never released, so the pass really is mid-flight when the stop is
    /// signalled; the stop is what unblocks it, through the token the gated step observes.</para>
    /// </summary>
    [Fact]
    public async Task A_host_stopped_mid_first_pass_does_not_strand_the_waiter()
    {
        var clock = new FakeTimeProvider(Now);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var provider = BuildProvider(clock, gate.Task);
        var service = new MaintenanceHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), clock);

        await service.StartAsync(CancellationToken.None);
        await WaitUntilBlockedAsync(provider);

        // Deliberately NOT released. The stop must be what ends the pass.
        await service.StopAsync(CancellationToken.None);

        await AssertCompletesAsync(
            service.FirstPassCompleted,
            "the host was stopped while the first pass was still blocked");
    }

    /// <summary>
    /// Fails with a message naming what did not happen, rather than hanging or continuing quietly.
    /// <see cref="Task.WaitAsync(TimeSpan)"/> throws <see cref="TimeoutException"/> on the bound, so
    /// the bound is a failure mode and not a tolerated outcome.
    /// </summary>
    private static async Task AssertCompletesAsync(Task completion, string because)
    {
        try
        {
            await completion.WaitAsync(CompletionWait);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"FirstPassCompleted did not publish within {CompletionWait} although {because}. " +
                "It is completed from a finally covering every exit of the first pass, so a waiter " +
                "reaching this bound means that publication was removed or the pass is wedged.");
        }
    }

    /// <summary>
    /// Waits until the gated step has actually been ENTERED, so a subsequent "has not completed yet"
    /// assertion is about the seam rather than about the test having looked too early. Bounded and
    /// failing, for the reason on <see cref="AssertCompletesAsync"/>.
    /// </summary>
    private static async Task WaitUntilBlockedAsync(ServiceProvider provider)
    {
        var entered = provider.GetRequiredService<GatedSourceRegistry>().Entered;

        try
        {
            await entered.WaitAsync(CompletionWait);
        }
        catch (TimeoutException)
        {
            Assert.Fail(
                $"The first maintenance pass did not reach its gated step within {CompletionWait}, " +
                "so this test could not establish that the pass was in flight.");
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        return new ArbitarrDbContext(optionsBuilder.Options);
    }

    /// <summary>
    /// A provider whose <see cref="ISourceRegistry"/> is the GATED one, so the test controls when
    /// the first pass may finish.
    ///
    /// <para><b>Why the caps-refresh step is the one gated.</b> It is the LAST step of a pass, so a
    /// pass blocked there has genuinely run everything else and is unambiguously unfinished — which
    /// is exactly the state the negative assertion needs. It is also the only step reachable through
    /// an INTERFACE the test can implement: <c>SettingsRepository</c> and <c>AutomaticBackupJob</c>
    /// are both sealed, and unsealing production code to give a test a seam would be a worse trade
    /// than gating one step later.</para>
    ///
    /// <para>Every other registration is the narrow set the first test in
    /// <see cref="MaintenanceHostedServiceTests"/> uses: the backup and log-trim steps resolve null
    /// through <c>GetService</c> and return early, which is fine here because this class is about
    /// the COMPLETION SEAM and not about what the steps do.</para>
    /// </summary>
    private ServiceProvider BuildProvider(
        FakeTimeProvider clock,
        Task? gatedStep,
        bool throwFromStep = false)
    {
        var registry = new GatedSourceRegistry(gatedStep, throwFromStep);

        var services = new ServiceCollection();
        services.AddSingleton(registry);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped(_ => CreateContext());
        services.AddScoped(sp => new SettingsRepository(
            sp.GetRequiredService<ArbitarrDbContext>(), TimeSpan.FromMinutes(15)));
        services.AddSingleton<ICapsCacheStore, NullCapsCacheStore>();
        services.AddScoped<CapsRefresher>();
        services.AddScoped<ISourceRegistry>(sp => sp.GetRequiredService<GatedSourceRegistry>());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The source registry the caps-refresh step resolves, rigged to block (or throw) and to signal
    /// that it was entered. A SINGLETON, registered behind a scoped resolution, because the service
    /// opens a FRESH SCOPE per step — a purely scoped object could not carry the gate from the test
    /// into the pass.
    /// </summary>
    private sealed class GatedSourceRegistry(Task? gate, bool shouldThrow) : ISourceRegistry
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes the first time the gated step is entered.</summary>
        public Task Entered => _entered.Task;

        public async Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();

            if (shouldThrow)
            {
                throw new InvalidOperationException(
                    "Planted by MaintenanceHostedServiceFirstPassCompletionTests: the first pass " +
                    "must publish its completion even when a step fails.");
            }

            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            // An empty set is an ordinary answer (see ISourceRegistry), so the released pass runs
            // the refresh to completion with nothing to fetch and reaches the end normally.
            return [];
        }

        /// <summary>
        /// Not reached: the caps-refresh step never asks for an access mode, and no source is
        /// returned above for anything to ask about. Throwing rather than returning a plausible
        /// default so a future pass that DOES call this fails loudly here instead of silently
        /// running against a value this class invented.
        /// </summary>
        public Task<string> ResolveNzbAccessModeAsync(
            string sourceName, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "MaintenanceHostedServiceFirstPassCompletionTests resolves no sources, so nothing " +
                "should be asking this registry for an access mode.");
    }

    /// <summary>
    /// A caps store that records nothing. The refresher needs one registered to resolve at all, and
    /// this class asserts on the completion seam rather than on what the refresh wrote.
    /// </summary>
    private sealed class NullCapsCacheStore : ICapsCacheStore
    {
        public Task<SourceCaps?> GetLastKnownGoodAsync(
            string sourceName, CancellationToken cancellationToken = default) =>
            Task.FromResult<SourceCaps?>(null);

        public Task SaveAsync(
            string sourceName, SourceCaps caps, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
