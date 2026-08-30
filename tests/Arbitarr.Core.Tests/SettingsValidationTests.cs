using Arbitarr.Core.Settings;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Proves each setting's floor AND ceiling actually rejects out-of-bounds input (mirrors Step
/// 1's AC6a non-vacuousness bar — a negative test that never actually exercises the rejection
/// path is not acceptable). Every [[Rejects*]] test asserts a thrown
/// <see cref="SettingsValidationException"/> for a value just past the bound; every
/// [[Accepts*]] test asserts the boundary value itself (inclusive) does NOT throw, so the tests
/// cannot be satisfied by an overly strict validator either.
///
/// Placed in Arbitarr.Core.Tests (not Arbitarr.Data.Tests) because the validator lives in
/// Arbitarr.Core.Settings, kept reference-free of EF Core/Data per AC6's spirit — see
/// SettingsValidator's own XML doc for the rationale.
/// </summary>
public class SettingsValidationTests
{
    private static readonly TimeSpan ArrSyncInterval = TimeSpan.FromMinutes(15); // AC0c measured value

    // ---------- fresh_until ----------

    [Fact]
    public void FreshUntil_RejectsNegativeValue_BelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateFreshUntil(TimeSpan.FromSeconds(-1), ArrSyncInterval));
        Assert.Equal(SettingKey.FreshUntil, ex.Key);
    }

    [Fact]
    public void FreshUntil_AcceptsZero_AtFloor()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateFreshUntil(TimeSpan.Zero, ArrSyncInterval));
        Assert.Null(ex);
    }

    [Fact]
    public void FreshUntil_RejectsValueAboveFlatCeiling()
    {
        // arrSyncInterval (15m) > flat 30m ceiling would never apply here; use a large sync
        // interval so the flat 30m ceiling is the binding one, then exceed it.
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateFreshUntil(TimeSpan.FromMinutes(31), TimeSpan.FromHours(2)));
        Assert.Equal(SettingKey.FreshUntil, ex.Key);
    }

    [Fact]
    public void FreshUntil_AcceptsFlatCeiling_WhenSyncIntervalIsLarger()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateFreshUntil(TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)));
        Assert.Null(ex);
    }

    [Fact]
    public void FreshUntil_RejectsValueAboveMeasuredSyncInterval_WhenSyncIntervalIsBindingCeiling()
    {
        // At the real 15m AC0c measurement, the sync interval (not the flat 30m) is the binding
        // ceiling. A value above 15m but below 30m must still be rejected.
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateFreshUntil(TimeSpan.FromMinutes(16), ArrSyncInterval));
        Assert.Equal(SettingKey.FreshUntil, ex.Key);
    }

    [Fact]
    public void FreshUntil_AcceptsExactMeasuredSyncInterval_WhenItIsBindingCeiling()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateFreshUntil(ArrSyncInterval, ArrSyncInterval));
        Assert.Null(ex);
    }

    // ---------- serve_until ----------

    [Fact]
    public void ServeUntil_RejectsValueBelowCurrentFreshUntil()
    {
        var currentFreshUntil = TimeSpan.FromMinutes(15);
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateServeUntil(TimeSpan.FromMinutes(14), currentFreshUntil));
        Assert.Equal(SettingKey.ServeUntil, ex.Key);
    }

    [Fact]
    public void ServeUntil_AcceptsValueEqualToCurrentFreshUntil_AtFloor()
    {
        var currentFreshUntil = TimeSpan.FromMinutes(15);
        var ex = Record.Exception(
            () => SettingsValidator.ValidateServeUntil(currentFreshUntil, currentFreshUntil));
        Assert.Null(ex);
    }

    [Fact]
    public void ServeUntil_RejectsValueAboveCeiling()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateServeUntil(TimeSpan.FromDays(14) + TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15)));
        Assert.Equal(SettingKey.ServeUntil, ex.Key);
    }

    [Fact]
    public void ServeUntil_AcceptsCeilingValue()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateServeUntil(TimeSpan.FromDays(14), TimeSpan.FromMinutes(15)));
        Assert.Null(ex);
    }

    // ---------- active_window ----------

    [Fact]
    public void ActiveWindow_RejectsValueBelowSyncIntervalFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateActiveWindow(
                ArrSyncInterval - TimeSpan.FromSeconds(1), ArrSyncInterval, TimeSpan.FromDays(7)));
        Assert.Equal(SettingKey.ActiveWindow, ex.Key);
    }

    [Fact]
    public void ActiveWindow_AcceptsExactSyncIntervalFloor()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateActiveWindow(ArrSyncInterval, ArrSyncInterval, TimeSpan.FromDays(7)));
        Assert.Null(ex);
    }

    [Fact]
    public void ActiveWindow_RejectsValueAboveFlatCeiling_WhenServeUntilIsLarger()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateActiveWindow(
                TimeSpan.FromDays(7) + TimeSpan.FromMinutes(1), ArrSyncInterval, TimeSpan.FromDays(14)));
        Assert.Equal(SettingKey.ActiveWindow, ex.Key);
    }

    [Fact]
    public void ActiveWindow_RejectsValueAboveCurrentServeUntil_WhenServeUntilIsBindingCeiling()
    {
        var currentServeUntil = TimeSpan.FromDays(2);
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateActiveWindow(
                currentServeUntil + TimeSpan.FromMinutes(1), ArrSyncInterval, currentServeUntil));
        Assert.Equal(SettingKey.ActiveWindow, ex.Key);
    }

    [Fact]
    public void ActiveWindow_AcceptsValueEqualToCurrentServeUntil_WhenServeUntilIsBindingCeiling()
    {
        var currentServeUntil = TimeSpan.FromDays(2);
        var ex = Record.Exception(
            () => SettingsValidator.ValidateActiveWindow(currentServeUntil, ArrSyncInterval, currentServeUntil));
        Assert.Null(ex);
    }

    // ---------- refresh_lead ----------

    [Fact]
    public void RefreshLead_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateRefreshLead(TimeSpan.FromSeconds(59), TimeSpan.FromMinutes(15)));
        Assert.Equal(SettingKey.RefreshLead, ex.Key);
    }

    [Fact]
    public void RefreshLead_AcceptsFloorValue()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateRefreshLead(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15)));
        Assert.Null(ex);
    }

    [Fact]
    public void RefreshLead_RejectsValueAboveCurrentFreshUntil()
    {
        var currentFreshUntil = TimeSpan.FromMinutes(15);
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateRefreshLead(currentFreshUntil + TimeSpan.FromSeconds(1), currentFreshUntil));
        Assert.Equal(SettingKey.RefreshLead, ex.Key);
    }

    [Fact]
    public void RefreshLead_AcceptsValueEqualToCurrentFreshUntil_AtCeiling()
    {
        var currentFreshUntil = TimeSpan.FromMinutes(15);
        var ex = Record.Exception(
            () => SettingsValidator.ValidateRefreshLead(currentFreshUntil, currentFreshUntil));
        Assert.Null(ex);
    }

    // ---------- worker_cycle_interval ----------

    [Fact]
    public void WorkerCycleInterval_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateWorkerCycleInterval(TimeSpan.FromSeconds(14), TimeSpan.FromMinutes(7.5)));
        Assert.Equal(SettingKey.WorkerCycleInterval, ex.Key);
    }

    [Fact]
    public void WorkerCycleInterval_AcceptsFloorValue()
    {
        var ex = Record.Exception(
            () => SettingsValidator.ValidateWorkerCycleInterval(TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(7.5)));
        Assert.Null(ex);
    }

    [Fact]
    public void WorkerCycleInterval_RejectsValueAboveCurrentRefreshLead()
    {
        var currentRefreshLead = TimeSpan.FromMinutes(7.5);
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateWorkerCycleInterval(currentRefreshLead + TimeSpan.FromSeconds(1), currentRefreshLead));
        Assert.Equal(SettingKey.WorkerCycleInterval, ex.Key);
    }

    [Fact]
    public void WorkerCycleInterval_AcceptsValueEqualToCurrentRefreshLead_AtCeiling()
    {
        var currentRefreshLead = TimeSpan.FromMinutes(7.5);
        var ex = Record.Exception(
            () => SettingsValidator.ValidateWorkerCycleInterval(currentRefreshLead, currentRefreshLead));
        Assert.Null(ex);
    }

    // ---------- AI verdict cache TTL (floor only; "none needed" ceiling) ----------

    [Fact]
    public void AiVerdictCacheTtl_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateAiVerdictCacheTtl(TimeSpan.FromHours(23)));
        Assert.Equal(SettingKey.AiVerdictCacheTtl, ex.Key);
    }

    [Fact]
    public void AiVerdictCacheTtl_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateAiVerdictCacheTtl(TimeSpan.FromHours(24)));
        Assert.Null(ex);
    }

    [Fact]
    public void AiVerdictCacheTtl_AcceptsArbitrarilyLargeValue_NoCeiling()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateAiVerdictCacheTtl(TimeSpan.FromDays(3650)));
        Assert.Null(ex);
    }

    // ---------- AI verdict cache row ceiling (floor only; "none needed" ceiling) ----------

    [Fact]
    public void AiVerdictCacheRowCeiling_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateAiVerdictCacheRowCeiling(9_999));
        Assert.Equal(SettingKey.AiVerdictCacheRowCeiling, ex.Key);
    }

    [Fact]
    public void AiVerdictCacheRowCeiling_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateAiVerdictCacheRowCeiling(10_000));
        Assert.Null(ex);
    }

    [Fact]
    public void AiVerdictCacheRowCeiling_AcceptsArbitrarilyLargeValue_NoCeiling()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateAiVerdictCacheRowCeiling(int.MaxValue));
        Assert.Null(ex);
    }

    // ---------- metadata refresh cadence (positive entries) ----------

    [Fact]
    public void MetadataRefreshCadence_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMetadataRefreshCadence(TimeSpan.FromHours(23)));
        Assert.Equal(SettingKey.MetadataRefreshCadence, ex.Key);
    }

    [Fact]
    public void MetadataRefreshCadence_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMetadataRefreshCadence(TimeSpan.FromHours(24)));
        Assert.Null(ex);
    }

    [Fact]
    public void MetadataRefreshCadence_RejectsValueAboveCeiling()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMetadataRefreshCadence(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1)));
        Assert.Equal(SettingKey.MetadataRefreshCadence, ex.Key);
    }

    [Fact]
    public void MetadataRefreshCadence_AcceptsCeilingValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMetadataRefreshCadence(TimeSpan.FromDays(30)));
        Assert.Null(ex);
    }

    // ---------- metadata negative TTL ----------

    [Fact]
    public void MetadataNegativeTtl_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMetadataNegativeTtl(TimeSpan.FromHours(23)));
        Assert.Equal(SettingKey.MetadataNegativeTtl, ex.Key);
    }

    [Fact]
    public void MetadataNegativeTtl_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMetadataNegativeTtl(TimeSpan.FromHours(24)));
        Assert.Null(ex);
    }

    [Fact]
    public void MetadataNegativeTtl_RejectsValueAboveCeiling()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMetadataNegativeTtl(TimeSpan.FromDays(30) + TimeSpan.FromSeconds(1)));
        Assert.Equal(SettingKey.MetadataNegativeTtl, ex.Key);
    }

    [Fact]
    public void MetadataNegativeTtl_AcceptsCeilingValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMetadataNegativeTtl(TimeSpan.FromDays(30)));
        Assert.Null(ex);
    }

    // ---------- suppression audit retention (floor only; "none needed" ceiling) ----------

    [Fact]
    public void SuppressionAuditRetention_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateSuppressionAuditRetention(TimeSpan.FromDays(6)));
        Assert.Equal(SettingKey.SuppressionAuditRetention, ex.Key);
    }

    [Fact]
    public void SuppressionAuditRetention_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateSuppressionAuditRetention(TimeSpan.FromDays(7)));
        Assert.Null(ex);
    }

    [Fact]
    public void SuppressionAuditRetention_AcceptsArbitrarilyLargeValue_NoCeiling()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateSuppressionAuditRetention(TimeSpan.FromDays(3650)));
        Assert.Null(ex);
    }

    // ---------- query snapshot TTL ----------

    [Fact]
    public void QuerySnapshotTtl_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateQuerySnapshotTtl(TimeSpan.FromSeconds(59)));
        Assert.Equal(SettingKey.QuerySnapshotTtl, ex.Key);
    }

    [Fact]
    public void QuerySnapshotTtl_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateQuerySnapshotTtl(TimeSpan.FromSeconds(60)));
        Assert.Null(ex);
    }

    [Fact]
    public void QuerySnapshotTtl_RejectsValueAboveCeiling()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateQuerySnapshotTtl(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1)));
        Assert.Equal(SettingKey.QuerySnapshotTtl, ex.Key);
    }

    [Fact]
    public void QuerySnapshotTtl_AcceptsCeilingValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateQuerySnapshotTtl(TimeSpan.FromHours(1)));
        Assert.Null(ex);
    }

    // ---------- maintenance job interval ----------

    [Fact]
    public void MaintenanceJobInterval_RejectsValueBelowFloor()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMaintenanceJobInterval(TimeSpan.FromMinutes(4)));
        Assert.Equal(SettingKey.MaintenanceJobInterval, ex.Key);
    }

    [Fact]
    public void MaintenanceJobInterval_AcceptsFloorValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMaintenanceJobInterval(TimeSpan.FromMinutes(5)));
        Assert.Null(ex);
    }

    [Fact]
    public void MaintenanceJobInterval_RejectsValueAboveCeiling()
    {
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateMaintenanceJobInterval(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1)));
        Assert.Equal(SettingKey.MaintenanceJobInterval, ex.Key);
    }

    [Fact]
    public void MaintenanceJobInterval_AcceptsCeilingValue()
    {
        var ex = Record.Exception(() => SettingsValidator.ValidateMaintenanceJobInterval(TimeSpan.FromHours(24)));
        Assert.Null(ex);
    }

    // ---------- ValidateChange: reverse-direction cross-field re-validation (M3-6) ----------
    //
    // These prove the gap the per-field Validate* methods above cannot see: each one only checks a
    // proposed value against whichever *other* current value it depends on (forward direction). None
    // of them can also catch the *reverse* case, where changing a field that other fields' bounds
    // depend on would strand one of those other fields below its own floor or above its own ceiling.
    // Every [[Rejects*]] test below builds a snapshot where the dependent field is already pinned
    // exactly at its bound relative to the anchor's current value, then proposes moving the anchor in
    // the direction that would push the dependent out of bounds -- and asserts the whole change is
    // rejected (keyed to the *anchor* being changed, not the dependent). Every [[Accepts*]] test
    // proposes a change that keeps every dependent within bounds, proving ValidateChange does not
    // over-reject on top of a merely-tightened-but-still-valid state.

    private static SettingsSnapshot BaselineSnapshot() => SettingsSnapshot.Defaults(ArrSyncInterval);

    [Fact]
    public void ValidateChange_RejectsLoweringFreshUntil_BelowCurrentServeUntilFloor()
    {
        // ServeUntil pinned exactly at the current FreshUntil (its floor). Lowering FreshUntil
        // leaves ServeUntil (unchanged) above its own floor, which is FINE -- floor violations need
        // ServeUntil to be below the *new* floor, which can't happen since the floor only drops.
        // The real hazard is the ceiling side: RefreshLead pinned exactly at current FreshUntil
        // (its ceiling) becomes invalid once FreshUntil drops below it.
        var baseline = BaselineSnapshot();
        var current = baseline with { RefreshLead = baseline.FreshUntil };
        var lowered = current.FreshUntil - TimeSpan.FromMinutes(1);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.FreshUntil, lowered, ArrSyncInterval));
        Assert.Equal(SettingKey.RefreshLead, ex.Key);
    }

    [Fact]
    public void ValidateChange_AcceptsLoweringFreshUntil_WhenDependentsStayInBounds()
    {
        // RefreshLead and ServeUntil both stay comfortably within bounds of the lowered FreshUntil.
        var current = BaselineSnapshot() with { RefreshLead = TimeSpan.FromMinutes(1) };
        var lowered = TimeSpan.FromMinutes(2);
        Assert.True(lowered < current.FreshUntil);
        Assert.True(lowered >= current.RefreshLead); // RefreshLead's ceiling (lowered) is not breached

        var ex = Record.Exception(
            () => SettingsValidator.ValidateChange(current, SettingKey.FreshUntil, lowered, ArrSyncInterval));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateChange_RejectsRaisingFreshUntil_AboveArrSyncCeiling_EvenIfDependentsWouldBeFine()
    {
        // The forward-direction check on the field itself must still apply: FreshUntil's own ceiling
        // is min(30m, arrSyncInterval). Proposing above that must reject keyed to FreshUntil itself,
        // never mind the dependents.
        var current = BaselineSnapshot();
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(
                current, SettingKey.FreshUntil, ArrSyncInterval + TimeSpan.FromMinutes(1), ArrSyncInterval));
        Assert.Equal(SettingKey.FreshUntil, ex.Key);
    }

    [Fact]
    public void ValidateChange_RejectsLoweringServeUntil_BelowCurrentFreshUntilFloor()
    {
        // Forward-direction: ServeUntil's own floor is the current FreshUntil. Proposing a ServeUntil
        // below it must reject keyed to ServeUntil.
        var current = BaselineSnapshot();
        var lowered = current.FreshUntil - TimeSpan.FromMinutes(1);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.ServeUntil, lowered, ArrSyncInterval));
        Assert.Equal(SettingKey.ServeUntil, ex.Key);
    }

    [Fact]
    public void ValidateChange_RejectsLoweringServeUntil_BelowCurrentActiveWindowCeiling()
    {
        // ActiveWindow pinned exactly at the current ServeUntil (its ceiling, since ServeUntil here
        // is below the flat 7d cap). Lowering ServeUntil below that stranded ActiveWindow value.
        var current = BaselineSnapshot() with
        {
            ServeUntil = TimeSpan.FromDays(3),
            ActiveWindow = TimeSpan.FromDays(3),
        };
        var lowered = TimeSpan.FromDays(2);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.ServeUntil, lowered, ArrSyncInterval));
        Assert.Equal(SettingKey.ActiveWindow, ex.Key);
    }

    [Fact]
    public void ValidateChange_AcceptsLoweringServeUntil_WhenDependentsStayInBounds()
    {
        var current = BaselineSnapshot() with
        {
            ServeUntil = TimeSpan.FromDays(3),
            ActiveWindow = TimeSpan.FromDays(1),
        };
        var lowered = TimeSpan.FromDays(2);

        var ex = Record.Exception(
            () => SettingsValidator.ValidateChange(current, SettingKey.ServeUntil, lowered, ArrSyncInterval));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateChange_RejectsLoweringRefreshLead_BelowCurrentWorkerCycleIntervalCeiling()
    {
        // WorkerCycleInterval pinned exactly at the current RefreshLead (its ceiling). Lowering
        // RefreshLead below that strands WorkerCycleInterval above its own new ceiling.
        var current = BaselineSnapshot() with
        {
            RefreshLead = TimeSpan.FromMinutes(5),
            WorkerCycleInterval = TimeSpan.FromMinutes(5),
        };
        var lowered = TimeSpan.FromMinutes(4);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.RefreshLead, lowered, ArrSyncInterval));
        Assert.Equal(SettingKey.WorkerCycleInterval, ex.Key);
    }

    [Fact]
    public void ValidateChange_AcceptsLoweringRefreshLead_WhenWorkerCycleIntervalStaysInBounds()
    {
        var current = BaselineSnapshot() with
        {
            RefreshLead = TimeSpan.FromMinutes(5),
            WorkerCycleInterval = TimeSpan.FromMinutes(1),
        };
        var lowered = TimeSpan.FromMinutes(2);

        var ex = Record.Exception(
            () => SettingsValidator.ValidateChange(current, SettingKey.RefreshLead, lowered, ArrSyncInterval));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateChange_RejectsRaisingWorkerCycleInterval_AboveCurrentRefreshLeadCeiling()
    {
        // Forward direction on WorkerCycleInterval itself: its own ceiling is the current RefreshLead.
        var current = BaselineSnapshot() with { RefreshLead = TimeSpan.FromMinutes(5) };
        var raised = current.RefreshLead + TimeSpan.FromMinutes(1);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.WorkerCycleInterval, raised, ArrSyncInterval));
        Assert.Equal(SettingKey.WorkerCycleInterval, ex.Key);
    }

    [Fact]
    public void ValidateChange_RejectsLoweringActiveWindow_BelowArrSyncFloor()
    {
        // Forward-direction ActiveWindow floor check still applies via ValidateChange.
        var current = BaselineSnapshot();
        var lowered = ArrSyncInterval - TimeSpan.FromMinutes(1);

        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.ActiveWindow, lowered, ArrSyncInterval));
        Assert.Equal(SettingKey.ActiveWindow, ex.Key);
    }

    [Fact]
    public void ValidateChange_AcceptsWorkerEnabledToggle_Unbounded()
    {
        var current = BaselineSnapshot();
        var ex = Record.Exception(
            () => SettingsValidator.ValidateChange(current, SettingKey.WorkerEnabled, !current.WorkerEnabled, ArrSyncInterval));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateChange_RejectsAiVerdictCacheRowCeiling_BelowFloor_ViaSingleFieldPath()
    {
        // No cross-field dependents for this key; ValidateChange must still apply its own bound.
        var current = BaselineSnapshot();
        var ex = Assert.Throws<SettingsValidationException>(
            () => SettingsValidator.ValidateChange(current, SettingKey.AiVerdictCacheRowCeiling, 1, ArrSyncInterval));
        Assert.Equal(SettingKey.AiVerdictCacheRowCeiling, ex.Key);
    }
}
