using Arbitarr.Core.Caching;
using Arbitarr.Data.CircuitBreaker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Arbitarr.Api.Admin;

/// <summary>
/// arb-mhd2: the error DETAIL for one source, the half of the old <c>SourceStatus.LastError</c>
/// that could not stay on an un-gated route.
/// </summary>
/// <param name="SourceName">The configured source this detail belongs to. Matches <c>SourceStatus.SourceName</c> on <c>/api/status</c>, which is how a client joins the two.</param>
/// <param name="LastError">
/// The sanitised description of the most recent failure, or null if the source has not failed.
/// Still passed through <c>SanitizedErrorDescription</c> — see this endpoint's remarks for why
/// being admin-gated does not make raw text acceptable here.
/// </param>
/// <param name="UpstreamStatusCode">
/// The HTTP status upstream answered with, when the failure carried one, else null. Held as a
/// separate int rather than left for a reader to parse back out of <paramref name="LastError"/>:
/// a client that wants to branch on 401 should not have to pattern-match prose.
/// </param>
public sealed record SourceDiagnostics(string SourceName, string? LastError, int? UpstreamStatusCode);

/// <summary>
/// arb-mhd2: the refresh worker's error detail, the counterpart to <see cref="SourceDiagnostics"/>.
/// </summary>
/// <param name="LastError">The most recent cycle-level failure's sanitised message, or null if the last cycle did not fault.</param>
public sealed record WorkerDiagnostics(string? LastError);

/// <summary>
/// arb-mhd2: <c>GET /api/admin/status/diagnostics</c> — the admin-gated companion to
/// <c>GET /api/status</c>, carrying the error TEXT that route no longer publishes.
///
/// <para><b>Why the text moved rather than being deleted.</b> The detail is genuinely useful: it is
/// what tells an operator a rejected Ollama option from an unknown model (arb-1rr), and deleting it
/// would have re-broken the diagnosis that bead fixed. What was wrong was its AUDIENCE, not its
/// existence — <c>/api/status</c> is <c>RouteClassification.PublicRead</c> and un-gated. Behind the
/// admin key the same string costs nothing to a caller who is not already authenticated, which is
/// the same split <c>docs/standards/data.md</c> records between the two scrubbers.</para>
///
/// <para><b>The text is STILL sanitised, and that is not belt-and-braces.</b> Every value here comes
/// from a snapshot written through <c>SanitizedErrorDescription.Describe</c> at the writer, so
/// nothing raw reaches this route in the first place. This endpoint deliberately does NOT re-widen
/// it by reaching for an unsanitised source: the admin key is an authorisation boundary, not a
/// reason to start publishing LAN topology into a response that a browser tab, a screenshot or a
/// support bundle can carry onward. An operator who needs the unredacted text has the log.</para>
///
/// <para><b>NON-TEMPLATED, and deliberately so.</b> It returns every source plus the worker in one
/// body rather than taking a <c>{sourceName}</c>. Two reasons: the dashboard needs them all at once
/// and would otherwise issue one request per source, and — the load-bearing one —
/// <c>AdminApiKeyRouteEnumerationTests</c> SKIPS every route containing a <c>{</c> (see the guard at
/// its ~line 203), so a templated route's gating is not covered by that sweep and needs its own
/// by-name test. A route shaped so the sweep can reach it is covered by construction. There is a
/// by-name gating test regardless, in <c>AdminStatusDiagnosticsEndpointTests</c>.</para>
///
/// <para><b>Under <c>/api/admin/</c> because classification goes by PATH PREFIX, never by verb</b>
/// (CLAUDE.md section 2). <c>RequireAdminApiKey</c> attaches
/// <c>RouteClassification.AdminMutating</c>, the required scope and the filter together — a GET that
/// only reads is still admin-classified here, exactly as <c>AdminEndpointConventions</c>'s own
/// remarks require: "it only reads" describes the handler, not how sensitive what it reads is.</para>
///
/// <para><b>It takes NO BODY.</b> A GET with no parameters cannot trip the
/// <c>EmptyBodyBehavior</c> trap that CLAUDE.md section 2 describes, where a required body is
/// rejected BEFORE <c>AdminApiKeyFilter</c> runs and leaks 400-vs-503 to an unauthenticated
/// caller.</para>
/// </summary>
/// <param name="Sources">One entry per source that has a health snapshot, ordered by name. Empty when none has been observed.</param>
/// <param name="Worker">The refresh worker's error detail.</param>
public sealed record StatusDiagnosticsResponse(
    IReadOnlyList<SourceDiagnostics> Sources,
    WorkerDiagnostics Worker);

/// <summary>Maps <c>GET /api/admin/status/diagnostics</c>. See <see cref="StatusDiagnosticsResponse"/> for the design.</summary>
public static class AdminStatusDiagnosticsEndpoint
{
    public static IEndpointConventionBuilder Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/status/diagnostics", HandleAsync)
            .RequireAdminApiKey();

    private static async Task<StatusDiagnosticsResponse> HandleAsync(
        SourceHealthRepository healthRepository,
        IRefreshWorkerHealth workerHealth,
        CancellationToken cancellationToken)
    {
        var snapshots = await healthRepository.LoadAllAsync(cancellationToken);

        var sources = snapshots
            .Select(kvp => new SourceDiagnostics(
                SourceName: kvp.Key,
                LastError: kvp.Value.LastError,
                // Read off the snapshot, where the writer put it. Deliberately NOT parsed back out
                // of LastError: that would be the classification-from-prose this whole change
                // removes, and it would break silently the first time the wording changed.
                UpstreamStatusCode: kvp.Value.LastUpstreamStatusCode))
            .OrderBy(s => s.SourceName, StringComparer.Ordinal)
            .ToArray();

        var worker = new WorkerDiagnostics(LastError: workerHealth.Snapshot.LastError);

        return new StatusDiagnosticsResponse(Sources: sources, Worker: worker);
    }
}
