using Arbitarr.Core.Notifications;
using Arbitarr.Data.Notifications;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #57's persistence: the notifier's settings, its durable policy state, and — the property this
/// file exists for — the <b>write-only</b> storage of the webhook URL.
///
/// <para>The URL is handled exactly as a source API key is (see
/// <see cref="SourceRepositoryTests"/>), and for a stronger reason: Discord, Telegram, Gotify and
/// Notifiarr all carry the token in the URL path, so for a notification webhook the URL <i>is</i>
/// the credential rather than merely carrying one. These tests assert the storage half of that
/// contract — the row is colon-namespaced (so no <c>SettingKey</c> value can name it and
/// <c>GET /api/admin/settings</c> cannot project it), presence reads as a bool, and the value is
/// readable only through the single delivery-path accessor.</para>
///
/// Every URL here is an obviously-fake <c>example.com</c> form, per this repo's fixture convention.
/// </summary>
public sealed class NotificationRepositoryTests : IDisposable
{
    /// <summary>
    /// Shaped the way real providers shape one — token in the PATH — so the leak assertions are
    /// testing the thing that actually leaks. Deliberately fake and on example.com.
    /// </summary>
    private const string WebhookUrl = "https://example.com/hooks/placeholder-webhook-token-value";

    private readonly SqliteTestDatabase _database = new("arr-searcher-notifications-test");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static NotificationSettings Enabled(int threshold = 3) => new(
        Enabled: true,
        ConsecutiveFailureThreshold: threshold,
        SuppressionRateThreshold: 0.5,
        SuppressionRateWindow: TimeSpan.FromHours(1),
        EnabledTriggers: NotificationSettings.AllTriggers);

    [Fact]
    public async Task An_unconfigured_notifier_reads_the_documented_defaults_and_is_off()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        var settings = await repository.GetSettingsAsync(CancellationToken.None);

        // A fresh install must be OFF: notifications need an endpoint only the operator can supply.
        Assert.False(settings.Enabled);
        Assert.Equal(NotificationSettings.DefaultConsecutiveFailureThreshold, settings.ConsecutiveFailureThreshold);
        Assert.Equal(NotificationSettings.DefaultSuppressionRateThreshold, settings.SuppressionRateThreshold);
        Assert.False(await repository.HasWebhookUrlAsync(CancellationToken.None));
        Assert.Null(await repository.GetLastDeliveryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Settings_round_trip_through_the_store()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        var proposed = new NotificationSettings(
            Enabled: true,
            ConsecutiveFailureThreshold: 7,
            SuppressionRateThreshold: 0.25,
            SuppressionRateWindow: TimeSpan.FromMinutes(30),
            EnabledTriggers: new HashSet<NotificationTrigger>
            {
                NotificationTrigger.SourceFailing,
                NotificationTrigger.SourceRecovered,
            });

        await repository.SetSettingsAsync(proposed, WebhookUrl, CancellationToken.None);

        var reloaded = await repository.GetSettingsAsync(CancellationToken.None);

        Assert.True(reloaded.Enabled);
        Assert.Equal(7, reloaded.ConsecutiveFailureThreshold);
        Assert.Equal(0.25, reloaded.SuppressionRateThreshold);
        Assert.Equal(TimeSpan.FromMinutes(30), reloaded.SuppressionRateWindow);
        Assert.Equal(
            new[] { NotificationTrigger.SourceFailing, NotificationTrigger.SourceRecovered }.Order().ToList(),
            reloaded.EnabledTriggers.Order().ToList());
    }

    [Fact]
    public async Task A_trigger_added_to_the_enum_later_defaults_to_enabled_for_an_existing_operator()
    {
        // The DISABLED set is what persists, precisely so this holds: an operator who configured
        // notifications before a trigger existed gets it enabled rather than silently muted on
        // upgrade. Simulated by storing a disabled set naming only one trigger and confirming the
        // complement — every other member, including any added later — comes back enabled.
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        var allButOne = NotificationSettings.AllTriggers
            .Where(t => t != NotificationTrigger.SuppressionRateHigh)
            .ToHashSet();

        await repository.SetSettingsAsync(Enabled() with { EnabledTriggers = allButOne }, null, CancellationToken.None);

        var reloaded = await repository.GetSettingsAsync(CancellationToken.None);

        Assert.DoesNotContain(NotificationTrigger.SuppressionRateHigh, reloaded.EnabledTriggers);
        Assert.Contains(NotificationTrigger.SourceFailing, reloaded.EnabledTriggers);
        Assert.Contains(NotificationTrigger.SourceRecovered, reloaded.EnabledTriggers);
        Assert.Contains(NotificationTrigger.SuppressionRateNormal, reloaded.EnabledTriggers);
    }

    [Fact]
    public async Task Out_of_range_settings_are_rejected_and_nothing_is_persisted()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        // AC24: reject, never clamp — and reject the WHOLE write rather than half-applying it.
        await Assert.ThrowsAsync<NotificationSettingsValidationException>(() =>
            repository.SetSettingsAsync(Enabled(threshold: 1), WebhookUrl, CancellationToken.None));

        Assert.False(await repository.HasWebhookUrlAsync(CancellationToken.None));
        Assert.False((await repository.GetSettingsAsync(CancellationToken.None)).Enabled);
    }

    [Fact]
    public async Task A_malformed_webhook_url_is_rejected_without_the_message_quoting_it()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        const string malformed = "not-a-url-placeholder-value";

        var ex = await Assert.ThrowsAsync<NotificationSettingsValidationException>(() =>
            repository.SetSettingsAsync(Enabled(), malformed, CancellationToken.None));

        // The rejection states the RULE, never the input: a validation error is a response body,
        // and echoing the rejected URL back would defeat the write-only storage it was headed for.
        Assert.DoesNotContain(malformed, ex.Message, StringComparison.Ordinal);
        Assert.False(await repository.HasWebhookUrlAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_webhook_url_is_stored_write_only_under_a_name_no_SettingKey_can_produce()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), WebhookUrl, CancellationToken.None);

        // Non-vacuous: the value really is in the store...
        var row = await context.Settings.AsNoTracking()
            .SingleAsync(e => e.Name == NotificationRepository.WebhookUrlSettingName);
        Assert.Equal(WebhookUrl, row.Value);

        // ...under a COLON-NAMESPACED name, which is what makes it unreachable from the settings
        // catalog projection (GET /api/admin/settings projects from SettingsCatalog.Entries, and no
        // SettingKey enum member can ever produce a name containing a colon).
        Assert.Contains(':', NotificationRepository.WebhookUrlSettingName);
        Assert.False(
            Enum.GetNames<Arbitarr.Core.Settings.SettingKey>()
                .Contains(NotificationRepository.WebhookUrlSettingName, StringComparer.OrdinalIgnoreCase),
            "The webhook URL row must not be nameable by any SettingKey, or it could surface on GET /api/admin/settings.");

        // Presence is readable as a bool, which is the only read a projection path may use.
        Assert.True(await repository.HasWebhookUrlAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_webhook_url_is_excluded_from_the_bulk_row_read_that_every_projection_uses()
    {
        // GetSettingsAsync and GetStateAsync share one bulk read of the notification:* rows, and
        // that read deliberately EXCLUDES the URL row rather than merely ignoring it: a secret that
        // is never loaded cannot be accidentally projected by a future field added to either.
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), WebhookUrl, CancellationToken.None);

        var settings = await repository.GetSettingsAsync(CancellationToken.None);
        var state = await repository.GetStateAsync(CancellationToken.None);

        // Non-vacuous: assert the URL IS stored before asserting it is absent from what these return.
        Assert.True(await repository.HasWebhookUrlAsync(CancellationToken.None));
        Assert.DoesNotContain(WebhookUrl, settings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookUrl, state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_delivery_path_accessor_returns_the_url()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), WebhookUrl, CancellationToken.None);

        Assert.Equal(WebhookUrl, await repository.ReadWebhookUrlForDeliveryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_null_url_leaves_the_stored_one_alone_so_an_ordinary_edit_cannot_destroy_it()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), WebhookUrl, CancellationToken.None);

        // An ordinary threshold edit that omits the URL (the client never had it, so it cannot
        // read-and-reapply one) must not clear the operator's target.
        await repository.SetSettingsAsync(Enabled(threshold: 5), null, CancellationToken.None);

        Assert.True(await repository.HasWebhookUrlAsync(CancellationToken.None));
        Assert.Equal(WebhookUrl, await repository.ReadWebhookUrlForDeliveryAsync(CancellationToken.None));
        Assert.Equal(5, (await repository.GetSettingsAsync(CancellationToken.None)).ConsecutiveFailureThreshold);
    }

    [Fact]
    public async Task Clearing_the_webhook_removes_the_row_entirely()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), WebhookUrl, CancellationToken.None);
        Assert.True(await repository.HasWebhookUrlAsync(CancellationToken.None));

        await repository.ClearWebhookUrlAsync(CancellationToken.None);

        Assert.False(await repository.HasWebhookUrlAsync(CancellationToken.None));
        Assert.Null(await repository.ReadWebhookUrlForDeliveryAsync(CancellationToken.None));
        Assert.False(await context.Settings.AsNoTracking()
            .AnyAsync(e => e.Name == NotificationRepository.WebhookUrlSettingName));
    }

    [Fact]
    public async Task Clearing_a_webhook_that_was_never_set_is_a_no_op()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.ClearWebhookUrlAsync(CancellationToken.None);

        Assert.False(await repository.HasWebhookUrlAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Policy_state_round_trips_so_a_restart_does_not_re_notify()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        var state = new NotificationState(
            Cursor: 4242,
            FailingSources: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["placeholder-source-one"] = 3,
                ["placeholder-source-two"] = 11,
            },
            SuppressionRateHigh: true,
            // arb-u8e: the position is a PAIR since repeats began folding onto existing rows, and
            // both halves have to survive a restart. A sub-second component is deliberate — the
            // watermark is compared with strict > against LastRepeatedAt, so a format that
            // truncated it would re-present every repeat in the same second forever.
            RepeatsSeenAt: new DateTimeOffset(2026, 9, 7, 12, 34, 56, 789, TimeSpan.Zero));

        await repository.SetStateAsync(state, CancellationToken.None);

        var reloaded = await repository.GetStateAsync(CancellationToken.None);

        Assert.Equal(4242, reloaded.Cursor);
        Assert.True(reloaded.SuppressionRateHigh);
        Assert.Equal(3, reloaded.FailingSources["placeholder-source-one"]);
        Assert.Equal(11, reloaded.FailingSources["placeholder-source-two"]);
        Assert.Equal(state.RepeatsSeenAt, reloaded.RepeatsSeenAt);
    }

    /// <summary>
    /// arb-u8e: a notifier that has never seen a repeat reads null, and that must round-trip as
    /// null rather than as an epoch or a parse artefact. Null means "re-read every repeat still in
    /// the store", which notifies late; any non-null default would mean "everything before now is
    /// already seen", which skips repeats silently — the failure this bead exists to remove.
    /// </summary>
    [Fact]
    public async Task A_notifier_that_has_seen_no_repeats_round_trips_a_null_watermark()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        // Non-vacuous: written as a real state with the OTHER fields populated, so a null read back
        // is the watermark's own value and not simply an absent row or an unwritten state.
        await repository.SetStateAsync(
            new NotificationState(
                Cursor: 7,
                FailingSources: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                SuppressionRateHigh: false,
                RepeatsSeenAt: null),
            CancellationToken.None);

        var reloaded = await repository.GetStateAsync(CancellationToken.None);

        Assert.Equal(7, reloaded.Cursor);
        Assert.Null(reloaded.RepeatsSeenAt);
    }

    [Fact]
    public async Task A_source_display_name_containing_a_tab_survives_the_state_round_trip()
    {
        // The encoding is "count\tsource" — count first, name taken as the whole remainder —
        // precisely so a display name containing the separator cannot shift the parse.
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        const string awkward = "placeholder\tsource\twith\ttabs";

        await repository.SetStateAsync(
            new NotificationState(1, new Dictionary<string, int> { [awkward] = 9 }, false),
            CancellationToken.None);

        var reloaded = await repository.GetStateAsync(CancellationToken.None);

        Assert.Equal(9, reloaded.FailingSources[awkward]);
    }

    [Fact]
    public async Task The_last_delivery_result_is_recorded_so_a_broken_notifier_is_visible()
    {
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.RecordDeliveryAsync(NotificationDeliveryOutcome.TlsFailure, CancellationToken.None);

        var last = await repository.GetLastDeliveryAsync(CancellationToken.None);

        Assert.NotNull(last);
        Assert.Equal(NotificationDeliveryOutcome.TlsFailure, last!.Value.Outcome);
        Assert.NotEqual(default, last.Value.At);
    }

    [Fact]
    public async Task A_corrupt_row_falls_back_to_the_default_rather_than_throwing()
    {
        // The notifier is best-effort infrastructure: a corrupt row must never be the reason the
        // host fails to start or a maintenance cycle dies.
        using var context = CreateContext();
        var repository = new NotificationRepository(context);

        await repository.SetSettingsAsync(Enabled(), null, CancellationToken.None);

        var row = await context.Settings.SingleAsync(e => e.Name == "notification:consecutive_failure_threshold");
        row.Value = "not-a-number";
        await context.SaveChangesAsync();

        var settings = await repository.GetSettingsAsync(CancellationToken.None);

        Assert.Equal(NotificationSettings.DefaultConsecutiveFailureThreshold, settings.ConsecutiveFailureThreshold);
    }
}
