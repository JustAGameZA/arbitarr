using Arbitarr.Core.Notifications;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Notifications;

/// <summary>
/// Persistence for #57: the notifier's configuration (including the write-only webhook URL), its
/// durable policy state, and its last delivery result.
///
/// <para><b>NO NEW TABLE, DELIBERATELY.</b> Everything here is a colon-namespaced row in the
/// existing <see cref="SettingEntry"/> table — the convention <c>SourceRepository</c> established
/// for source API keys (<c>source:{id}:api_key</c>). That is not merely reuse: a new table would
/// accumulate rows and would therefore need its own retention wired into <c>MaintenanceJob</c>
/// alongside the five already there, and a retention policy with no scheduler is a defect that a
/// diff cannot show. These rows are FIXED IN NUMBER — one per setting, one for the cursor, one for
/// the last result — so they do not accumulate and there is nothing to prune. A future change that
/// makes any notification data grow per-event MUST add its prune to <c>MaintenanceJob</c> in the
/// same commit.</para>
///
/// <para><b>THE WEBHOOK URL IS A SECRET, HANDLED EXACTLY LIKE A SOURCE API KEY.</b> Its row name is
/// colon-namespaced, so no <c>SettingKey</c> enum value can produce it and it can never surface
/// through <c>GET /api/admin/settings</c> (which projects from <c>SettingsCatalog.Entries</c>,
/// never from this table). Presence is read as a BOOL by <see cref="HasWebhookUrlAsync"/>, which is
/// the only read any path that projects to the wire may use.
/// <see cref="ReadWebhookUrlForDeliveryAsync"/> is the single reader of the value, and — precisely
/// as <c>SourceRepository.ReadApiKeyForUpstreamRequestAsync</c> states for the key it mirrors — the
/// value it returns goes into one outbound POST and nowhere else. It is never returned to a caller,
/// never logged, and never written to an event row: <c>EventEntry</c> is served un-gated over
/// <c>GET /api/activity</c>, and providers embed their token in the URL path, so the URL on an
/// event row would be a credential disclosure to any LAN client. Any future caller of that method
/// which is not "post it to the webhook" is a bug.</para>
/// </summary>
public sealed class NotificationRepository
{
    /// <summary>
    /// The write-only Settings row holding the webhook URL. Colon-namespaced so it is unreachable
    /// from the settings catalog projection — see the type doc. Internal naming detail, not a
    /// contract.
    /// </summary>
    public const string WebhookUrlSettingName = "notification:webhook_url";

    private const string EnabledSettingName = "notification:enabled";
    private const string FailureThresholdSettingName = "notification:consecutive_failure_threshold";
    private const string SuppressionRateThresholdSettingName = "notification:suppression_rate_threshold";
    private const string SuppressionRateWindowSettingName = "notification:suppression_rate_window";
    private const string DisabledTriggersSettingName = "notification:disabled_triggers";
    private const string CursorSettingName = "notification:cursor";
    private const string FailingSourcesSettingName = "notification:failing_sources";
    private const string SuppressionRateHighSettingName = "notification:suppression_rate_high";
    private const string LastDeliverySettingName = "notification:last_delivery_outcome";
    private const string LastDeliveryAtSettingName = "notification:last_delivery_at";

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public NotificationRepository(ArbitarrDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Loads the notifier's settings, falling back per-field to
    /// <see cref="NotificationSettings.Default"/> for anything unset or unparseable. A corrupt row
    /// yields the default rather than throwing: the notifier is best-effort infrastructure and must
    /// never be the reason the host fails to start or a maintenance cycle dies.
    /// </summary>
    public async Task<NotificationSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(cancellationToken);

        var defaults = NotificationSettings.Default;

        return new NotificationSettings(
            Enabled: ParseBool(rows, EnabledSettingName, defaults.Enabled),
            ConsecutiveFailureThreshold: ParseInt(rows, FailureThresholdSettingName, defaults.ConsecutiveFailureThreshold),
            SuppressionRateThreshold: ParseDouble(rows, SuppressionRateThresholdSettingName, defaults.SuppressionRateThreshold),
            SuppressionRateWindow: ParseTimeSpan(rows, SuppressionRateWindowSettingName, defaults.SuppressionRateWindow),
            EnabledTriggers: ParseEnabledTriggers(rows));
    }

    /// <summary>
    /// Writes the notifier's settings. Validated by <see cref="NotificationSettingsValidator"/>
    /// before anything is staged, so an out-of-range threshold rejects the whole write rather than
    /// leaving a half-applied configuration (AC24's reject-never-clamp posture, as
    /// <c>SettingsRepository</c> and <c>SourceRepository</c> both take it).
    ///
    /// <paramref name="webhookUrl"/> follows the source-API-key contract exactly: null LEAVES THE
    /// STORED VALUE ALONE, a non-empty value REPLACES it. There is deliberately no way to express
    /// "give me back what is stored", because the client never had it — which is also why omitting
    /// the field from an edit must not clear it.
    /// </summary>
    public async Task SetSettingsAsync(
        NotificationSettings settings,
        string? webhookUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        NotificationSettingsValidator.Validate(settings);
        if (webhookUrl is not null)
        {
            NotificationSettingsValidator.ValidateWebhookUrl(webhookUrl);
        }

        await UpsertAsync(EnabledSettingName, settings.Enabled.ToString(), cancellationToken);
        await UpsertAsync(FailureThresholdSettingName, settings.ConsecutiveFailureThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await UpsertAsync(SuppressionRateThresholdSettingName, settings.SuppressionRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
        await UpsertAsync(SuppressionRateWindowSettingName, settings.SuppressionRateWindow.ToString(), cancellationToken);

        // Stored as the DISABLED set rather than the enabled one, so a trigger added to the enum in
        // future defaults to enabled for an operator who configured notifications before it
        // existed — the alternative silently mutes new triggers on every upgrade.
        var disabled = Enum.GetValues<NotificationTrigger>()
            .Where(t => !settings.EnabledTriggers.Contains(t))
            .Select(t => t.ToString());
        await UpsertAsync(DisabledTriggersSettingName, string.Join(',', disabled), cancellationToken);

        if (!string.IsNullOrEmpty(webhookUrl))
        {
            await UpsertAsync(WebhookUrlSettingName, webhookUrl, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a webhook URL is stored, without exposing it — the read every projection to the wire
    /// uses. The config endpoint renders its "set / not set" indicator from this and nothing else.
    /// </summary>
    public Task<bool> HasWebhookUrlAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking().AnyAsync(e => e.Name == WebhookUrlSettingName, cancellationToken);

    /// <summary>
    /// Reads the stored webhook URL so it can be POSTED TO, and for no other purpose.
    ///
    /// <para>The direct analogue of <c>SourceRepository.ReadApiKeyForUpstreamRequestAsync</c>, and
    /// the same rule applies with more force, because here the URL <i>is</i> the credential rather
    /// than merely carrying one. The value returned goes into
    /// <c>WebhookNotificationTransport.DeliverAsync</c> and nowhere else: never to a caller, never
    /// to a log, never into an error message or a delivery outcome (both of which are closed enums
    /// precisely so they cannot carry it), and NEVER onto an event row — <c>/api/activity</c> is
    /// un-gated, so a URL there would be readable by any LAN client.</para>
    /// </summary>
    public Task<string?> ReadWebhookUrlForDeliveryAsync(CancellationToken cancellationToken) =>
        _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name == WebhookUrlSettingName)
            .Select(e => e.Value)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Clears the stored webhook URL. The one way to un-configure a target, kept explicit rather
    /// than folded into <see cref="SetSettingsAsync"/>, where a null URL means "leave it alone" —
    /// conflating the two would make an ordinary settings edit that omits the field silently
    /// destroy the operator's configuration.
    /// </summary>
    public async Task ClearWebhookUrlAsync(CancellationToken cancellationToken)
    {
        var row = await _dbContext.Settings.FindAsync([WebhookUrlSettingName], cancellationToken);
        if (row is null)
        {
            return;
        }

        _dbContext.Settings.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the notifier's durable policy state (AC7's restart behaviour). An absent or corrupt
    /// state reads as <see cref="NotificationState.Empty"/>, which resumes from the beginning of
    /// the store rather than from "now" — see <c>NotificationPolicy</c>'s restart note for why
    /// skipping ahead would be the worse failure.
    /// </summary>
    public async Task<NotificationState> GetStateAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(cancellationToken);

        long? cursor = rows.TryGetValue(CursorSettingName, out var rawCursor)
            && long.TryParse(rawCursor, out var parsedCursor)
                ? parsedCursor
                : null;

        var failing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (rows.TryGetValue(FailingSourcesSettingName, out var rawFailing) && !string.IsNullOrEmpty(rawFailing))
        {
            foreach (var pair in rawFailing.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // "count\tsource" — count first so a display name containing a tab cannot shift the
                // parse, and the name is taken as the whole remainder rather than a split field.
                var separator = pair.IndexOf('\t');
                if (separator > 0 && int.TryParse(pair[..separator], out var count))
                {
                    failing[pair[(separator + 1)..]] = count;
                }
            }
        }

        return new NotificationState(
            cursor,
            failing,
            ParseBool(rows, SuppressionRateHighSettingName, false));
    }

    /// <summary>Persists the notifier's policy state after an evaluation cycle.</summary>
    public async Task SetStateAsync(NotificationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        await UpsertAsync(
            CursorSettingName,
            state.Cursor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            cancellationToken);

        var failing = string.Join('\n', state.FailingSources.Select(kvp => $"{kvp.Value}\t{kvp.Key}"));
        await UpsertAsync(FailingSourcesSettingName, failing, cancellationToken);
        await UpsertAsync(SuppressionRateHighSettingName, state.SuppressionRateHigh.ToString(), cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Records the outcome of the most recent delivery attempt (§3.4/AC6: "a notifier that is
    /// silently broken is worse than none"). Stores the closed enum's NAME and a timestamp — never
    /// an exception string, a status code, or the target, none of which the outcome carries.
    /// </summary>
    public async Task RecordDeliveryAsync(NotificationDeliveryOutcome outcome, CancellationToken cancellationToken)
    {
        await UpsertAsync(LastDeliverySettingName, outcome.ToString(), cancellationToken);
        await UpsertAsync(
            LastDeliveryAtSettingName,
            _timeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The last delivery attempt's outcome and when it happened, or null if nothing has been
    /// attempted. What the config surface renders so a broken notifier is visible.
    /// </summary>
    public async Task<(NotificationDeliveryOutcome Outcome, DateTimeOffset At)?> GetLastDeliveryAsync(
        CancellationToken cancellationToken)
    {
        var rows = await ReadRowsAsync(cancellationToken);

        if (!rows.TryGetValue(LastDeliverySettingName, out var rawOutcome)
            || !Enum.TryParse<NotificationDeliveryOutcome>(rawOutcome, out var outcome))
        {
            return null;
        }

        var at = rows.TryGetValue(LastDeliveryAtSettingName, out var rawAt)
            && DateTimeOffset.TryParse(
                rawAt,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var parsedAt)
            ? parsedAt
            : default;

        return (outcome, at);
    }

    /// <summary>
    /// Every <c>notification:*</c> row in one query. The rows are few and fixed in number, so one
    /// read beats a query per field.
    ///
    /// The webhook URL row is EXCLUDED here rather than merely ignored by the callers: every reader
    /// of this dictionary is a projection path, and a secret that is never loaded cannot be
    /// accidentally projected. <see cref="ReadWebhookUrlForDeliveryAsync"/> is the only path that
    /// reads it, and it reads it alone.
    /// </summary>
    private async Task<Dictionary<string, string>> ReadRowsAsync(CancellationToken cancellationToken) =>
        await _dbContext.Settings.AsNoTracking()
            .Where(e => e.Name.StartsWith("notification:") && e.Name != WebhookUrlSettingName)
            .ToDictionaryAsync(e => e.Name, e => e.Value, cancellationToken);

    /// <summary>
    /// Stages an insert-or-update for one row. Deliberately does NOT save: callers batch a group of
    /// related rows into one <c>SaveChangesAsync</c>, so a settings write applies as a unit rather
    /// than leaving a partial configuration if one row fails.
    /// </summary>
    private async Task UpsertAsync(string name, string value, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Settings.FindAsync([name], cancellationToken);
        if (existing is null)
        {
            _dbContext.Settings.Add(new SettingEntry
            {
                Name = name,
                Value = value,
                UpdatedAt = _timeProvider.GetUtcNow(),
            });
            return;
        }

        existing.Value = value;
        existing.UpdatedAt = _timeProvider.GetUtcNow();
    }

    private static bool ParseBool(IReadOnlyDictionary<string, string> rows, string name, bool fallback) =>
        rows.TryGetValue(name, out var raw) && bool.TryParse(raw, out var value) ? value : fallback;

    private static int ParseInt(IReadOnlyDictionary<string, string> rows, string name, int fallback) =>
        rows.TryGetValue(name, out var raw)
        && int.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ParseDouble(IReadOnlyDictionary<string, string> rows, string name, double fallback) =>
        rows.TryGetValue(name, out var raw)
        && double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static TimeSpan ParseTimeSpan(IReadOnlyDictionary<string, string> rows, string name, TimeSpan fallback) =>
        rows.TryGetValue(name, out var raw) && TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>
    /// Reads the stored DISABLED set and returns its complement. See
    /// <see cref="SetSettingsAsync"/> for why the disabled set is what is persisted.
    /// </summary>
    private static IReadOnlySet<NotificationTrigger> ParseEnabledTriggers(IReadOnlyDictionary<string, string> rows)
    {
        if (!rows.TryGetValue(DisabledTriggersSettingName, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return NotificationSettings.AllTriggers;
        }

        var disabled = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Enum.TryParse<NotificationTrigger>(name, out var trigger) ? trigger : (NotificationTrigger?)null)
            .Where(trigger => trigger.HasValue)
            .Select(trigger => trigger!.Value)
            .ToHashSet();

        return Enum.GetValues<NotificationTrigger>().Where(t => !disabled.Contains(t)).ToHashSet();
    }
}
