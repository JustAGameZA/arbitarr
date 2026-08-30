using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Settings;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// AC25: proves the staleness envelope's two derived durations —
/// <c>worst_case_unjudged_age = search_result_cache_band_bound + classifier_queue_latency</c> and
/// <c>refresh_lead + worker_cycle_interval</c> — compute exactly as specified, and that the
/// envelope carries <c>fresh_until</c>/<c>serve_until</c> through unchanged as the other two bounds.
/// </summary>
public sealed class StalenessEnvelopeTests
{
    [Fact]
    public void WorstCaseUnjudgedAge_is_cache_band_bound_plus_classifier_queue_latency()
    {
        var envelope = new StalenessEnvelope(
            SearchResultCacheBandBound: TimeSpan.FromMinutes(15),
            ClassifierQueueLatency: TimeSpan.FromSeconds(90),
            FreshUntil: TimeSpan.FromMinutes(15),
            RefreshLead: TimeSpan.FromMinutes(7.5),
            WorkerCycleInterval: TimeSpan.FromMinutes(1),
            ServeUntil: TimeSpan.FromDays(7));

        Assert.Equal(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(90), envelope.WorstCaseUnjudgedAge);
    }

    [Fact]
    public void RefreshLeadPlusWorkerCycle_adds_the_two_worker_bounds()
    {
        var envelope = new StalenessEnvelope(
            SearchResultCacheBandBound: TimeSpan.FromMinutes(15),
            ClassifierQueueLatency: TimeSpan.Zero,
            FreshUntil: TimeSpan.FromMinutes(15),
            RefreshLead: TimeSpan.FromMinutes(7.5),
            WorkerCycleInterval: TimeSpan.FromMinutes(1),
            ServeUntil: TimeSpan.FromDays(7));

        Assert.Equal(TimeSpan.FromMinutes(8.5), envelope.RefreshLeadPlusWorkerCycle);
    }

    [Fact]
    public void FromSettings_projects_FreshUntil_and_ServeUntil_as_the_cache_band_bound_and_outer_bound()
    {
        var settings = SettingsSnapshot.Defaults(TimeSpan.FromMinutes(15));

        var envelope = StalenessEnvelope.FromSettings(settings, classifierQueueLatency: TimeSpan.FromSeconds(30));

        Assert.Equal(settings.FreshUntil, envelope.SearchResultCacheBandBound);
        Assert.Equal(settings.FreshUntil, envelope.FreshUntil);
        Assert.Equal(settings.ServeUntil, envelope.ServeUntil);
        Assert.Equal(settings.RefreshLead, envelope.RefreshLead);
        Assert.Equal(settings.WorkerCycleInterval, envelope.WorkerCycleInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), envelope.ClassifierQueueLatency);
        Assert.Equal(settings.FreshUntil + TimeSpan.FromSeconds(30), envelope.WorstCaseUnjudgedAge);
    }
}
