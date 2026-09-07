using Arbitarr.Api.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// #56: the restart the UI promises after a restore actually happens, and happens AFTER the
/// response rather than during it.
///
/// The plan's step 5 and AC6 forbid claiming a restart that is not verified. This is that
/// verification for the half this process controls: that <see cref="IHostApplicationLifetime.StopApplication"/>
/// is really called. The other half — that something starts the process again — is the deployment's
/// <c>restart: unless-stopped</c> policy in <c>docker-compose.yml</c> and cannot be asserted from
/// inside the process, which is exactly why the UI copy says "Arbitarr will restart if your
/// deployment is configured to restart it" rather than promising unconditionally.
/// </summary>
public sealed class RestoreCoordinatorTests
{
    [Fact]
    public void The_host_is_not_stopped_while_the_response_is_still_being_written()
    {
        // Stopping inline would tear the host down mid-response, so the operator would see a
        // connection reset immediately after the most destructive action in the product —
        // indistinguishable from a crash, and exactly when a clear message matters most.
        var lifetime = new RecordingLifetime();
        var time = new FakeTimeProvider();

        var coordinator = new RestoreCoordinator(lifetime, time);
        Assert.True(coordinator.RequestRestart());

        Assert.True(coordinator.RestartRequested);
        Assert.False(lifetime.Stopped, "The host was stopped synchronously, aborting the response.");

        // Just short of the delay: still not stopped.
        time.Advance(RestoreCoordinator.RestartDelay - TimeSpan.FromMilliseconds(1));
        Assert.False(lifetime.Stopped);
    }

    [Fact]
    public void The_host_is_stopped_once_the_delay_elapses()
    {
        var lifetime = new RecordingLifetime();
        var time = new FakeTimeProvider();

        new RestoreCoordinator(lifetime, time).RequestRestart();

        time.Advance(RestoreCoordinator.RestartDelay);

        Assert.True(
            lifetime.Stopped,
            "The host was never stopped, so the restored database and release-GUID secret would " +
            "never be loaded and the UI's restart claim would be false.");
    }

    [Fact]
    public void A_restore_that_was_never_requested_never_stops_the_host()
    {
        // The negative control: the assertion above must be detecting the request, not merely the
        // passage of time.
        var lifetime = new RecordingLifetime();
        var time = new FakeTimeProvider();

        _ = new RestoreCoordinator(lifetime, time);

        time.Advance(RestoreCoordinator.RestartDelay * 10);

        Assert.False(lifetime.Stopped);
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication() => Stopped = true;
    }
}
