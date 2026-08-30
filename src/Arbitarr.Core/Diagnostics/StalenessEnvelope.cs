namespace Arbitarr.Core.Diagnostics;

/// <summary>
/// AC25: the worst-case age a client could ever be served an unjudged (not-yet-AI-classified)
/// search result, plus the three named bounds that make that number legible to an operator rather
/// than a bare, unexplained duration.
///
/// <c>worst_case_unjudged_age = search_result_cache_band_bound + classifier_queue_latency</c> —
/// the cache can serve a result up to <see cref="SearchResultCacheBandBound"/> old (the "Fresh"
/// band; see <see cref="Arbitarr.Data.Entities.SearchResultCacheEntry"/> and
/// <c>SettingKey.FreshUntil</c>), and once served, a result may sit for up to
/// <see cref="ClassifierQueueLatency"/> before the AI layer gets to judging it. Both are additive
/// because they are sequential, not overlapping, delays on the same result.
/// </summary>
/// <param name="SearchResultCacheBandBound">
/// Upper bound on how old a cached search result can be while still served from the healthy-path
/// ("Fresh") cache band — currently <c>SettingKey.FreshUntil</c>.
/// </param>
/// <param name="ClassifierQueueLatency">
/// Upper bound on how long a result can wait in the AI classifier's queue before being judged.
/// Zero on this branch: the AI classifier queue (M5/M6) has not merged yet, so there is nothing
/// to wait on — this field exists now so the envelope's shape is stable ahead of that merge, per
/// the same M2-dashboard-shape-stability rationale as <c>StatusResponse.WorkerStatus</c>.
/// </param>
/// <param name="FreshUntil">
/// Bound 1: age at which a search-result cache entry leaves the "Fresh" (served-directly) band.
/// </param>
/// <param name="RefreshLead">
/// Bound 2 component: how far ahead of <see cref="FreshUntil"/> the proactive-refresh worker
/// targets a re-fetch, so a still-active query's cache entry rarely lapses out of "Fresh" at all.
/// </param>
/// <param name="WorkerCycleInterval">
/// Bound 2 component: how often the proactive-refresh worker scans for entries needing a refresh —
/// added to <see cref="RefreshLead"/> because a worker cycle boundary can delay the refresh by up
/// to one full cycle after the lead window opens.
/// </param>
/// <param name="ServeUntil">
/// Bound 3: outer age at which a cache entry is no longer served at all (pruned instead) — the
/// hard ceiling on <see cref="WorstCaseUnjudgedAge"/> regardless of the other components.
/// </param>
public sealed record StalenessEnvelope(
    TimeSpan SearchResultCacheBandBound,
    TimeSpan ClassifierQueueLatency,
    TimeSpan FreshUntil,
    TimeSpan RefreshLead,
    TimeSpan WorkerCycleInterval,
    TimeSpan ServeUntil)
{
    /// <summary>AC25's headline number: <see cref="SearchResultCacheBandBound"/> + <see cref="ClassifierQueueLatency"/>.</summary>
    public TimeSpan WorstCaseUnjudgedAge => SearchResultCacheBandBound + ClassifierQueueLatency;

    /// <summary>Bound 2: <see cref="RefreshLead"/> + <see cref="WorkerCycleInterval"/> (see that property's doc comment for why they add).</summary>
    public TimeSpan RefreshLeadPlusWorkerCycle => RefreshLead + WorkerCycleInterval;

    /// <summary>
    /// Builds the envelope from the current effective settings. <paramref name="classifierQueueLatency"/>
    /// is passed in rather than hardcoded so a future M5/M6 merge can supply a real measured value
    /// without changing this type's shape.
    /// </summary>
    public static StalenessEnvelope FromSettings(Settings.SettingsSnapshot settings, TimeSpan classifierQueueLatency) =>
        new(
            SearchResultCacheBandBound: settings.FreshUntil,
            ClassifierQueueLatency: classifierQueueLatency,
            FreshUntil: settings.FreshUntil,
            RefreshLead: settings.RefreshLead,
            WorkerCycleInterval: settings.WorkerCycleInterval,
            ServeUntil: settings.ServeUntil);
}
