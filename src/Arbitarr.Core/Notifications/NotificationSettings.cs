namespace Arbitarr.Core.Notifications;

/// <summary>
/// The notifier's tunables, as a value (plan §4 item 1). Bounds and defaults are stated here rather
/// than scattered as literals at the call sites, matching <c>EventRetentionPolicy</c>'s posture for
/// the same reason: a future reader looking for "how many failures before it tells me" finds one
/// place holding the number and the argument for it.
///
/// <para><b>These are NOT <see cref="Settings.SettingKey"/> entries, and that is load-bearing.</b>
/// They persist as colon-namespaced <c>notification:*</c> rows, which no <c>SettingKey</c> value
/// can name, so they cannot surface through <c>GET /api/admin/settings</c> (which projects from
/// <c>SettingsCatalog.Entries</c>, never from the table). That property exists for the webhook URL
/// stored alongside them — see <c>NotificationSettingNames</c> — and the policy values ride the
/// same namespace so the whole group is read and written through one gated surface rather than
/// split across two with different exposure rules.</para>
/// </summary>
/// <param name="Enabled">Master switch. Off means the notifier evaluates nothing at all.</param>
/// <param name="ConsecutiveFailureThreshold">
/// How many consecutive recorded failures a source must accumulate before the first notification
/// (§3.1: "a consecutive-failure threshold before the first notification avoids alerting on a
/// single blip"). One would notify on every transient blip, which is the noise that makes operators
/// mute the feature.
/// </param>
/// <param name="SuppressionRateThreshold">
/// The fraction of decisions in the window that must be enforced suppressions before the rate is
/// called high, in [0, 1]. §3.1 notifies on the PATTERN, never the instance.
/// </param>
/// <param name="SuppressionRateWindow">
/// How far back the rate is measured. Also the minimum sample age: a window with too few decisions
/// in it cannot produce a meaningful rate — see <see cref="MinimumSuppressionSample"/>.
/// </param>
/// <param name="EnabledTriggers">
/// Per-trigger enable/disable (an issue AC). A trigger absent from this set is evaluated for state
/// but never delivered, so muting "suppression rate" does not desynchronise the source-failure
/// state machine.
/// </param>
public sealed record NotificationSettings(
    bool Enabled,
    int ConsecutiveFailureThreshold,
    double SuppressionRateThreshold,
    TimeSpan SuppressionRateWindow,
    IReadOnlySet<NotificationTrigger> EnabledTriggers)
{
    /// <summary>
    /// Fewest decisions the window must contain before a suppression rate is computed at all.
    ///
    /// Without a floor the rate is a division by a tiny denominator: one suppression out of one
    /// decision is a 100% suppression rate, and a quiet homelab would page its operator every time
    /// a single release was filtered overnight. This is the documented defence against the issue's
    /// named failure mode ("the failure mode that makes people turn notifications off entirely").
    /// </summary>
    public const int MinimumSuppressionSample = 20;

    /// <summary>Default consecutive failures before notifying (three polls of a genuinely down source).</summary>
    public const int DefaultConsecutiveFailureThreshold = 3;

    /// <summary>Default suppression-rate threshold: half of everything decided in the window.</summary>
    public const double DefaultSuppressionRateThreshold = 0.5;

    /// <summary>Default rate window.</summary>
    public static readonly TimeSpan DefaultSuppressionRateWindow = TimeSpan.FromHours(1);

    /// <summary>Every trigger, which is the default: a configured notifier notifies on all four.</summary>
    public static readonly IReadOnlySet<NotificationTrigger> AllTriggers =
        new HashSet<NotificationTrigger>(Enum.GetValues<NotificationTrigger>());

    /// <summary>
    /// The settings a fresh install has: OFF, with every other value at its default so that turning
    /// it on is a single change rather than a configuration exercise. Off by default because
    /// notifications need an endpoint that only the operator can supply.
    /// </summary>
    public static NotificationSettings Default { get; } = new(
        Enabled: false,
        ConsecutiveFailureThreshold: DefaultConsecutiveFailureThreshold,
        SuppressionRateThreshold: DefaultSuppressionRateThreshold,
        SuppressionRateWindow: DefaultSuppressionRateWindow,
        EnabledTriggers: AllTriggers);

    /// <summary>Whether <paramref name="trigger"/> is enabled for delivery.</summary>
    public bool IsEnabled(NotificationTrigger trigger) => Enabled && EnabledTriggers.Contains(trigger);
}
