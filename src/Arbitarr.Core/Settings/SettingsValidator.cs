namespace Arbitarr.Core.Settings;

/// <summary>
/// Validates proposed setting values against the plan's floor/ceiling policy (plan lines
/// ~1043-1056). Pure and reference-free of persistence: callers (the Data-layer settings
/// repository) call this before persisting a change, and the maintenance job's own interval is
/// the sole "restart to take effect" exception (documented at
/// <see cref="ValidateMaintenanceJobInterval"/>) — validation itself has no restart concept.
///
/// Every bound rejects with <see cref="SettingsValidationException"/>; none clamp silently, per
/// the plan's explicit warning that a too-high value on some of these settings (search-result
/// cache, maintenance interval, metadata refresh cadence) produces a fully green but silently
/// useless system (D3) if allowed through.
/// </summary>
public static class SettingsValidator
{
    /// <summary>
    /// Ceiling for <see cref="SettingKey.FreshUntil"/>: the lesser of the plan's flat 30-minute
    /// cap and the AC0c-measured *arr RSS sync interval (docs/step0-measurements.md §3 — 15m at
    /// the user's current Sonarr/Radarr config). Passed by the caller rather than hardcoded here
    /// because it is measured environment data, not a compile-time constant.
    /// </summary>
    public static TimeSpan FreshUntilCeiling(TimeSpan arrSyncInterval)
    {
        var flatCeiling = TimeSpan.FromMinutes(30);
        return arrSyncInterval < flatCeiling ? arrSyncInterval : flatCeiling;
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.FreshUntil"/> value.
    /// Floor: 0s (0 = healthy-path cache disabled, a deliberate legitimate choice, not rejected).
    /// Ceiling: min(30m, AC0c-measured *arr RSS sync interval) — never above whichever *arr app
    /// polls most frequently, or most syncs would return stale cached data (D3).
    /// </summary>
    public static void ValidateFreshUntil(TimeSpan proposed, TimeSpan arrSyncInterval)
    {
        if (proposed < TimeSpan.Zero)
        {
            throw new SettingsValidationException(SettingKey.FreshUntil,
                $"fresh_until must be >= 0s (0 = cache disabled), got {proposed}.");
        }

        var ceiling = FreshUntilCeiling(arrSyncInterval);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.FreshUntil,
                $"fresh_until must be <= {ceiling} (min of 30m ceiling and the measured *arr RSS " +
                $"sync interval of {arrSyncInterval}), got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.ServeUntil"/> value.
    /// Floor: must be &gt;= the *current* fresh_until (cross-field). Ceiling: 14d.
    /// </summary>
    public static void ValidateServeUntil(TimeSpan proposed, TimeSpan currentFreshUntil)
    {
        if (proposed < currentFreshUntil)
        {
            throw new SettingsValidationException(SettingKey.ServeUntil,
                $"serve_until must be >= the current fresh_until ({currentFreshUntil}), got {proposed}.");
        }

        var ceiling = TimeSpan.FromDays(14);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.ServeUntil,
                $"serve_until must be <= {ceiling}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.ActiveWindow"/> value.
    /// Floor: 1x the AC0c-measured *arr RSS sync interval (below this the window expires between
    /// consecutive polls of the same query and the worker never fires - D3).
    /// Ceiling: min(7d, current serve_until) - scheduling refreshes for entries no longer servable
    /// is meaningless.
    /// </summary>
    public static void ValidateActiveWindow(TimeSpan proposed, TimeSpan arrSyncInterval, TimeSpan currentServeUntil)
    {
        if (proposed < arrSyncInterval)
        {
            throw new SettingsValidationException(SettingKey.ActiveWindow,
                $"active_window must be >= the measured *arr RSS sync interval ({arrSyncInterval}), got {proposed}.");
        }

        var flatCeiling = TimeSpan.FromDays(7);
        var ceiling = currentServeUntil < flatCeiling ? currentServeUntil : flatCeiling;
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.ActiveWindow,
                $"active_window must be <= {ceiling} (min of 7d ceiling and the current serve_until " +
                $"of {currentServeUntil}), got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.RefreshLead"/> value.
    /// Floor: 1m (prevents refresh storms). Ceiling: current fresh_until (cross-field; prevents
    /// refreshing entries that were never stale).
    /// </summary>
    public static void ValidateRefreshLead(TimeSpan proposed, TimeSpan currentFreshUntil)
    {
        var floor = TimeSpan.FromMinutes(1);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.RefreshLead,
                $"refresh_lead must be >= {floor}, got {proposed}.");
        }

        if (proposed > currentFreshUntil)
        {
            throw new SettingsValidationException(SettingKey.RefreshLead,
                $"refresh_lead must be <= the current fresh_until ({currentFreshUntil}), got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.WorkerCycleInterval"/> value.
    /// Floor: 15s (prevents a hot scan loop against SQLite). Ceiling: current refresh_lead
    /// (cross-field; a cycle longer than refresh_lead makes the lead unachievable - D3).
    /// </summary>
    public static void ValidateWorkerCycleInterval(TimeSpan proposed, TimeSpan currentRefreshLead)
    {
        var floor = TimeSpan.FromSeconds(15);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.WorkerCycleInterval,
                $"worker_cycle_interval must be >= {floor}, got {proposed}.");
        }

        if (proposed > currentRefreshLead)
        {
            throw new SettingsValidationException(SettingKey.WorkerCycleInterval,
                $"worker_cycle_interval must be <= the current refresh_lead ({currentRefreshLead}), got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.AiVerdictCacheTtl"/> value.
    /// Floor: 24h. No ceiling needed - a long verdict TTL cannot cause silent wrongness
    /// (verdicts are model-version-keyed; the row ceiling bounds size independently).
    /// </summary>
    public static void ValidateAiVerdictCacheTtl(TimeSpan proposed)
    {
        var floor = TimeSpan.FromHours(24);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.AiVerdictCacheTtl,
                $"AI verdict cache TTL must be >= {floor}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.AiVerdictCacheRowCeiling"/> value.
    /// Floor: 10,000 rows. No ceiling needed - an over-large ceiling is a disk-space choice,
    /// visible in the health panel, not a correctness hazard.
    /// </summary>
    public static void ValidateAiVerdictCacheRowCeiling(int proposed)
    {
        const int floor = 10_000;
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.AiVerdictCacheRowCeiling,
                $"AI verdict cache row ceiling must be >= {floor}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.MetadataRefreshCadence"/> value (positive
    /// entries). Floor: 24h (protects XEM/AniDB from over-fetching). Ceiling: 30d (above this the
    /// instance pins to an indefinitely stale snapshot, contradicting AC-M8).
    /// </summary>
    public static void ValidateMetadataRefreshCadence(TimeSpan proposed)
    {
        var floor = TimeSpan.FromHours(24);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.MetadataRefreshCadence,
                $"metadata refresh cadence must be >= {floor}, got {proposed}.");
        }

        var ceiling = TimeSpan.FromDays(30);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.MetadataRefreshCadence,
                $"metadata refresh cadence must be <= {ceiling}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.MetadataNegativeTtl"/> value (negative /
    /// "no coverage" entries). Same floor/ceiling rationale as the positive cadence: 24h floor,
    /// 30d ceiling.
    /// </summary>
    public static void ValidateMetadataNegativeTtl(TimeSpan proposed)
    {
        var floor = TimeSpan.FromHours(24);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.MetadataNegativeTtl,
                $"metadata negative-entry TTL must be >= {floor}, got {proposed}.");
        }

        var ceiling = TimeSpan.FromDays(30);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.MetadataNegativeTtl,
                $"metadata negative-entry TTL must be <= {ceiling}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.SuppressionAuditRetention"/> value.
    /// Floor: 7d (keeps the shadow-mode 48h review window always possible). No ceiling needed -
    /// a long retention window only costs disk (bounded by AC22 / the health panel).
    /// </summary>
    public static void ValidateSuppressionAuditRetention(TimeSpan proposed)
    {
        var floor = TimeSpan.FromDays(7);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.SuppressionAuditRetention,
                $"suppression audit log retention must be >= {floor}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.QuerySnapshotTtl"/> value.
    /// Floor: 60s. Ceiling: 1h (prevents a snapshot outliving the paging session it exists to
    /// stabilise).
    /// </summary>
    public static void ValidateQuerySnapshotTtl(TimeSpan proposed)
    {
        var floor = TimeSpan.FromSeconds(60);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.QuerySnapshotTtl,
                $"query snapshot TTL must be >= {floor}, got {proposed}.");
        }

        var ceiling = TimeSpan.FromHours(1);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.QuerySnapshotTtl,
                $"query snapshot TTL must be <= {ceiling}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.MaintenanceJobInterval"/> value.
    /// Floor: 5m. Ceiling: 24h (above this pruning cannot keep pace with accumulation and the
    /// bounded steady-state guarantee is defeated - D3).
    ///
    /// RESTART EXCEPTION: this is the one setting explicitly permitted to require a process
    /// restart to take effect (plan lines ~1082-1085, AC24) — every other setting here takes
    /// effect on its next evaluation without restart. The maintenance job's own scheduling loop
    /// reads its interval once at startup; changing it while the job is running does not
    /// re-schedule the already-running timer. Callers (the Data-layer maintenance job) must
    /// document this to operators (e.g. in the settings UI / API response) rather than silently
    /// implying a live reschedule.
    /// </summary>
    public static void ValidateMaintenanceJobInterval(TimeSpan proposed)
    {
        var floor = TimeSpan.FromMinutes(5);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.MaintenanceJobInterval,
                $"maintenance job interval must be >= {floor}, got {proposed}.");
        }

        var ceiling = TimeSpan.FromHours(24);
        if (proposed > ceiling)
        {
            throw new SettingsValidationException(SettingKey.MaintenanceJobInterval,
                $"maintenance job interval must be <= {ceiling}, got {proposed}.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.AdminApiKey"/> value (AC24-style: rejects
    /// rather than silently accepting a weak value, since a short/blank key defeats the D2
    /// mutation gate entirely). Floor: 16 characters (minimum length to resist casual guessing on
    /// a LAN-exposed API). No ceiling — an arbitrarily long key is never a correctness hazard.
    /// </summary>
    public static void ValidateAdminApiKey(string proposed)
    {
        const int floor = 16;
        if (string.IsNullOrWhiteSpace(proposed) || proposed.Length < floor)
        {
            throw new SettingsValidationException(SettingKey.AdminApiKey,
                $"admin API key must be a non-blank string of at least {floor} characters.");
        }
    }

    /// <summary>
    /// Validates a proposed <see cref="SettingKey.AiConfidenceThreshold"/> value (D3: default
    /// 0.9). Floor: 0.0 exclusive (a 0 threshold would suppress everything unconditionally,
    /// silently defeating the "AI slot" of the precedence chain). Ceiling: 1.0 inclusive (a
    /// confidence score is a probability and cannot exceed certainty).
    /// </summary>
    public static void ValidateAiConfidenceThreshold(double proposed)
    {
        if (proposed <= 0.0)
        {
            throw new SettingsValidationException(SettingKey.AiConfidenceThreshold,
                $"AI confidence threshold must be > 0.0, got {proposed}.");
        }

        if (proposed > 1.0)
        {
            throw new SettingsValidationException(SettingKey.AiConfidenceThreshold,
                $"AI confidence threshold must be <= 1.0, got {proposed}.");
        }
    }
}
