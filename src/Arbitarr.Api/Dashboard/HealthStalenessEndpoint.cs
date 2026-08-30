using Arbitarr.Api.Routing;
using Arbitarr.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Dashboard;

/// <summary>
/// AC25 staleness envelope, as reported over the wire. Field names are the snake_case AC25 spec
/// names verbatim (not this codebase's usual PascalCase-serializes-as-camelCase), since AC25 quotes
/// them directly (<c>worst_case_unjudged_age</c>, <c>fresh_until</c>, <c>serve_until</c>) as the
/// contract operators and monitoring tooling are expected to read.
/// </summary>
public sealed record StalenessEnvelopeResponse(
    string worst_case_unjudged_age,
    string search_result_cache_band_bound,
    string classifier_queue_latency,
    string fresh_until,
    string refresh_lead_plus_worker_cycle_interval,
    string serve_until);

/// <summary>Maps the read-only <c>GET /api/health/staleness</c> endpoint (M7-8, AC25).</summary>
public static class HealthStalenessEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/health/staleness", HandleAsync)
            .WithClassification(RouteClassification.PublicRead);

    private static async Task<StalenessEnvelopeResponse> HandleAsync(
        EffectiveSettingsReader settingsReader,
        CancellationToken cancellationToken)
    {
        var settings = await settingsReader.LoadAsync(cancellationToken);

        // Zero until the M5/M6 AI classifier queue merges (see StalenessEnvelope.ClassifierQueueLatency).
        var envelope = StalenessEnvelope.FromSettings(settings, classifierQueueLatency: TimeSpan.Zero);

        return new StalenessEnvelopeResponse(
            worst_case_unjudged_age: envelope.WorstCaseUnjudgedAge.ToString(),
            search_result_cache_band_bound: envelope.SearchResultCacheBandBound.ToString(),
            classifier_queue_latency: envelope.ClassifierQueueLatency.ToString(),
            fresh_until: envelope.FreshUntil.ToString(),
            refresh_lead_plus_worker_cycle_interval: envelope.RefreshLeadPlusWorkerCycle.ToString(),
            serve_until: envelope.ServeUntil.ToString());
    }
}
