namespace Arbitarr.Data.Sources;

/// <summary>
/// How a call to a source turned out, as far as backoff is concerned (arb-x7w8.10). Three outcomes,
/// because the three lead to three genuinely different actions and collapsing any pair loses one.
/// </summary>
public enum SourceCallOutcome
{
    /// <summary>The call worked. Resets escalation to zero — see <see cref="SourceBackoffPolicy"/>.</summary>
    Success = 0,

    /// <summary>
    /// The call failed in a way waiting might fix: a timeout, a connection refused, a 5xx. Escalates
    /// one level.
    /// </summary>
    TransientFailure = 1,

    /// <summary>
    /// The source rejected our credentials. Disables the source permanently rather than escalating;
    /// see <see cref="Entities.SourceBackoffState.IsPermanentlyDisabled"/> for why the distinction
    /// from <see cref="TransientFailure"/> is load-bearing rather than cosmetic.
    /// </summary>
    AuthenticationFailure = 2,
}

/// <summary>
/// The single named place holding the backoff figures (arb-x7w8.10) — the escalation period table
/// and the startup grace window. Stated here rather than scattered as literals for the same reason
/// <see cref="Events.EventRetentionPolicy"/> centralises retention: a figure copied into two places
/// drifts, and these are figures a test asserts PER LEVEL.
/// </summary>
public static class SourceBackoffPolicy
{
    /// <summary>
    /// How long a source is held off at each <see cref="Entities.SourceBackoffState.DisabledLevel"/>,
    /// in minutes. Index 0 is "not escalated" and is deliberately zero: a source that has not failed
    /// is not held off at all, so level and period share one origin and no caller has to remember an
    /// offset.
    ///
    /// <para>The table is bounded rather than unbounded-exponential: escalation SATURATES at the last
    /// entry (3 hours) instead of doubling forever. A source that has been broken all day should be
    /// retried every three hours, not once a fortnight — the operator needs to learn that it came
    /// back reasonably soon after it does.</para>
    /// </summary>
    public static readonly IReadOnlyList<int> Periods = new[] { 0, 5, 15, 30, 60, 180 };

    /// <summary>
    /// How long after process start escalation is SUPPRESSED (Prowlarr's
    /// <c>MinimumTimeSinceStartup</c>).
    ///
    /// <para><b>This exists because a restart makes every source fail at once</b> — upstreams are not
    /// reachable yet, or the host is still warming — and that burst says nothing about the indexers.
    /// Without the window, that single moment disables the entire set simultaneously, exactly when
    /// the operator is watching a dashboard after a deploy and is most likely to conclude the deploy
    /// caused it. The cost is a short delay before a genuinely broken source starts backing off,
    /// which is cheap against disabling every healthy one.</para>
    ///
    /// <para>It suppresses ESCALATION ONLY. An authentication failure inside the window still
    /// disables permanently: a rejected key is not a symptom of a cold start, and hiding it here
    /// would make the fault invisible for fifteen minutes for no gain.</para>
    /// </summary>
    public static readonly TimeSpan StartupGraceWindow = TimeSpan.FromMinutes(15);

    /// <summary>The highest level <see cref="Periods"/> defines; escalation saturates here.</summary>
    public static int MaxLevel => Periods.Count - 1;

    /// <summary>How long a source sitting at <paramref name="level"/> is held off.</summary>
    public static TimeSpan PeriodFor(int level) =>
        TimeSpan.FromMinutes(Periods[Math.Clamp(level, 0, MaxLevel)]);
}
