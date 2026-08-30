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
    /// Validates a proposed <see cref="SettingKey.ClassifierPollInterval"/> value.
    /// Floor: 15s (prevents a hot polling loop against the in-memory release lookup/classifier).
    /// No ceiling needed - a long poll interval only delays classification, it cannot cause
    /// silent wrongness (verify-m5).
    /// </summary>
    public static void ValidateClassifierPollInterval(TimeSpan proposed)
    {
        var floor = TimeSpan.FromSeconds(15);
        if (proposed < floor)
        {
            throw new SettingsValidationException(SettingKey.ClassifierPollInterval,
                $"classifier_poll_interval must be >= {floor}, got {proposed}.");
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
    /// Validates a proposed single-field change against the rest of a live <see cref="SettingsSnapshot"/>,
    /// in both directions of every cross-field bound (M3-6).
    ///
    /// <para>
    /// The per-field <c>Validate*</c> methods above only check the proposed value against whichever
    /// <em>other</em> current values it directly depends on (e.g. <see cref="ValidateServeUntil"/> checks
    /// the proposal against the current <see cref="SettingsSnapshot.FreshUntil"/>). That is the forward
    /// direction and is necessary, but not sufficient: several bounds run the other way too —
    /// <see cref="SettingsSnapshot.FreshUntil"/> is the floor <see cref="SettingsSnapshot.ServeUntil"/>
    /// depends on, so lowering <c>FreshUntil</c> without touching <c>ServeUntil</c> can silently strand
    /// the already-persisted <c>ServeUntil</c> below its own floor. Same shape for
    /// <c>FreshUntil</c> → <c>RefreshLead</c>'s ceiling, <c>ServeUntil</c> → <c>ActiveWindow</c>'s ceiling,
    /// and <c>RefreshLead</c> → <c>WorkerCycleInterval</c>'s ceiling. A single-field validator call can
    /// never see this because it is never handed the field that depends on it.
    /// </para>
    ///
    /// <para>
    /// This method closes that gap: it first validates <paramref name="proposed"/> against
    /// <paramref name="current"/> using the normal forward-direction per-field validator, then — for
    /// every other setting whose bound depends on <paramref name="key"/> — re-runs that dependent
    /// setting's own validator with its own current value against the proposed change, rejecting the
    /// whole change (not clamping either side) if the dependent would end up violating its own bound.
    /// A caller wanting to move both settings needs two calls: first move the dependent field to a
    /// value compatible with both its old and new anchor, then move the anchor field itself, so no
    /// intermediate snapshot is ever out of bounds.
    /// </para>
    /// </summary>
    /// <param name="current">The live snapshot before the change.</param>
    /// <param name="key">Which setting <paramref name="proposed"/> is a new value for.</param>
    /// <param name="proposed">
    /// The proposed new value, boxed: a <see cref="TimeSpan"/> for every key except
    /// <see cref="SettingKey.AiVerdictCacheRowCeiling"/> (<see cref="int"/>) and
    /// <see cref="SettingKey.WorkerEnabled"/> (<see cref="bool"/>, never bounded — accepted as-is).
    /// </param>
    /// <param name="arrSyncInterval">
    /// The AC0c-measured *arr RSS sync interval, same as every other caller of
    /// <see cref="ValidateFreshUntil"/>/<see cref="ValidateActiveWindow"/> already supplies explicitly.
    /// This is measured environment data, not a <see cref="SettingsSnapshot"/> field, and is never
    /// derived from <see cref="SettingsSnapshot.ActiveWindow"/> — an operator is free to raise
    /// <c>ActiveWindow</c> above its floor for their own reasons, so back-solving the interval from it
    /// would silently drift once that happens.
    /// </param>
    public static void ValidateChange(SettingsSnapshot current, SettingKey key, object proposed, TimeSpan arrSyncInterval)
    {
        switch (key)
        {
            case SettingKey.FreshUntil:
            {
                var value = (TimeSpan)proposed;
                ValidateFreshUntil(value, arrSyncInterval);
                // Dependents: ServeUntil's floor and RefreshLead's ceiling are both "current FreshUntil".
                ValidateServeUntil(current.ServeUntil, value);
                ValidateRefreshLead(current.RefreshLead, value);
                break;
            }
            case SettingKey.ServeUntil:
            {
                var value = (TimeSpan)proposed;
                ValidateServeUntil(value, current.FreshUntil);
                // Dependent: ActiveWindow's ceiling is min(7d, current ServeUntil).
                ValidateActiveWindow(current.ActiveWindow, arrSyncInterval, value);
                break;
            }
            case SettingKey.ActiveWindow:
                ValidateActiveWindow((TimeSpan)proposed, arrSyncInterval, current.ServeUntil);
                break;
            case SettingKey.RefreshLead:
            {
                var value = (TimeSpan)proposed;
                ValidateRefreshLead(value, current.FreshUntil);
                // Dependent: WorkerCycleInterval's ceiling is current RefreshLead.
                ValidateWorkerCycleInterval(current.WorkerCycleInterval, value);
                break;
            }
            case SettingKey.WorkerCycleInterval:
                ValidateWorkerCycleInterval((TimeSpan)proposed, current.RefreshLead);
                break;
            case SettingKey.WorkerEnabled:
                _ = (bool)proposed; // unbounded; presence of the cast documents the expected shape.
                break;
            case SettingKey.AiVerdictCacheTtl:
                ValidateAiVerdictCacheTtl((TimeSpan)proposed);
                break;
            case SettingKey.AiVerdictCacheRowCeiling:
                ValidateAiVerdictCacheRowCeiling((int)proposed);
                break;
            case SettingKey.MetadataRefreshCadence:
                ValidateMetadataRefreshCadence((TimeSpan)proposed);
                break;
            case SettingKey.MetadataNegativeTtl:
                ValidateMetadataNegativeTtl((TimeSpan)proposed);
                break;
            case SettingKey.SuppressionAuditRetention:
                ValidateSuppressionAuditRetention((TimeSpan)proposed);
                break;
            case SettingKey.QuerySnapshotTtl:
                ValidateQuerySnapshotTtl((TimeSpan)proposed);
                break;
            case SettingKey.MaintenanceJobInterval:
                ValidateMaintenanceJobInterval((TimeSpan)proposed);
                break;
            case SettingKey.ClassifierPollInterval:
                ValidateClassifierPollInterval((TimeSpan)proposed);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown setting key.");
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

    /// <summary>Max length for a filter rule's regex pattern text (M4 review finding, MEDIUM: unbounded
    /// regex pattern length). Matches the <c>FilterRuleEntry.Pattern</c> column's
    /// <c>HasMaxLength(1024)</c> constraint in <c>ArbitarrDbContext</c> — kept here so every entry
    /// point that accepts a pattern (import, and any future direct-API rule creation) rejects with
    /// the same bound before it ever reaches persistence.</summary>
    public const int FilterRulePatternMaxLength = 1024;

    /// <summary>
    /// Validates a proposed filter-rule pattern's length (M4 review finding, MEDIUM). Not tied to a
    /// <see cref="SettingKey"/> — a filter rule pattern is per-rule data, not a global setting — so
    /// this rejects with a plain <see cref="ArgumentException"/> rather than
    /// <see cref="SettingsValidationException"/>, matching how <see cref="Filtering.RuleImporter"/>
    /// rejects other malformed rule data. Rejects rather than truncates (this codebase's established
    /// idiom): silently clamping a pattern could turn a deliberate rule into a different, unintended
    /// one.
    /// </summary>
    public static void ValidateFilterRulePattern(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Length > FilterRulePatternMaxLength)
        {
            throw new ArgumentException(
                $"Filter rule pattern must be <= {FilterRulePatternMaxLength} characters, got {pattern.Length}.",
                nameof(pattern));
        }
    }

    /// <summary>Max rules a single profile may contain (M4 security review, MEDIUM: unbounded
    /// aggregate rule-evaluation time). This is the write-time half of the fix — the runtime half
    /// is <c>FilterProfile.TotalEvaluationBudget</c>, which bounds evaluation time regardless of
    /// rule count and fails open on a cutoff. This bound instead stops an over-large profile from
    /// being written in the first place, at the same import boundary as
    /// <see cref="ValidateFilterRulePattern"/>. Deliberately NOT enforced at load time: an
    /// already-persisted profile that exceeds this count (e.g. grandfathered before this bound
    /// existed) must keep loading and evaluating normally — the time budget covers that case — so
    /// this can never fail-closed on existing data.</summary>
    public const int MaxRulesPerProfile = 500;

    /// <summary>
    /// Validates a proposed rule count for a single profile (M4 security review, MEDIUM). Not tied
    /// to a <see cref="SettingKey"/> — like <see cref="ValidateFilterRulePattern"/>, this rejects
    /// with a plain <see cref="ArgumentException"/> rather than <see cref="SettingsValidationException"/>.
    /// Callers apply this at the write/import boundary (see <see cref="Filtering.RuleImporter.Import"/>),
    /// never when loading an existing persisted profile.
    /// </summary>
    public static void ValidateRuleCount(int ruleCount)
    {
        if (ruleCount > MaxRulesPerProfile)
        {
            throw new ArgumentException(
                $"A profile must contain <= {MaxRulesPerProfile} rules, got {ruleCount}.",
                nameof(ruleCount));
        }
    }
}
