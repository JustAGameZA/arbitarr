using Microsoft.Extensions.Hosting;

namespace Arbitarr.Api.Admin;

/// <summary>
/// Makes the running process adopt restored state after <c>RestoreService</c> has replaced the
/// files on disk (#56).
///
/// <para><b>WHY A RESTART AND NOT A RELOAD:</b> see
/// <c>docs/adr/0007-restart-rather-than-reload-after-restore.md</c>, which records the three pieces
/// of process state pinned to the replaced files (the <c>ReleaseGuid</c> static, pooled connections
/// over the old inode, and <c>Database.Migrate()</c>), the in-process reload that was rejected, and
/// the consequence that the deployment's restart policy is load-bearing. It is not restated here:
/// two copies of that reasoning would drift, and the ADR is the one that gets updated.</para>
///
/// <para><b>THE STOP IS DEFERRED PAST THE CURRENT RESPONSE.</b> Calling StopApplication inline
/// would tear the host down while the restore response is still being written, so the operator's
/// browser would show a connection reset immediately after the most destructive action in the
/// product - indistinguishable from a crash, and exactly when a clear message matters most. The
/// request registers the intent; the stop is fired from a timer once the handler has returned.</para>
/// </summary>
public sealed class RestoreCoordinator
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// How long the host is given to finish writing the restore response before it is stopped.
    /// Short enough that an operator is not left wondering whether the restart happened, long
    /// enough that a local response completes many times over.
    /// </summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    public RestoreCoordinator(IHostApplicationLifetime lifetime, TimeProvider? timeProvider = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// True once <see cref="RequestRestart"/> has been called. Exposed so a test can assert the
    /// restore path actually asked for a restart, rather than the test having to stop a real host
    /// to find out.
    /// </summary>
    public bool RestartRequested { get; private set; }

    /// <summary>
    /// Schedules the host stop and returns true. Returns true rather than a status because the only
    /// failure mode — a host with no lifetime — cannot occur: the lifetime is a framework service
    /// present in every host this code runs in.
    /// </summary>
    public bool RequestRestart()
    {
        RestartRequested = true;

        var timer = _timeProvider.CreateTimer(
            static state => ((IHostApplicationLifetime)state!).StopApplication(),
            _lifetime,
            RestartDelay,
            Timeout.InfiniteTimeSpan);

        // The timer is deliberately not disposed here: disposing it would cancel the callback this
        // exists to fire. It is a one-shot with an infinite period, and the process it stops is the
        // one that would have collected it.
        _ = timer;

        return true;
    }
}
