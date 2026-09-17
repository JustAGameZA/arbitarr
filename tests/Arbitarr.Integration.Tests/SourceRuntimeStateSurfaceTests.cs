using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// arb-x7w8.11: the two surfaces that report per-indexer runtime state, end to end against the real
/// Host — and the SPLIT between them, which is the security-relevant half of this bead.
///
/// <para><b>The split, restated because a later edit is likely to want to undo it.</b>
/// <c>/api/status</c> is <c>RouteClassification.PublicRead</c> and UN-GATED, so it carries exactly
/// ONE thing from this feature: the blocking health item a permanently disabled indexer raises, with
/// a summary built from the configured source name and fixed wording alone. Every per-source detail
/// — the backoff state, the level, the hold-off instant, the last outcome, and the hits spent
/// against the limit — is admin-gated on <c>GET /api/admin/sources</c>. The tests below assert BOTH
/// halves: that the item appears publicly, and that none of the detail does.</para>
///
/// <para>This class owns its own factory rather than sharing one, for the reason
/// <see cref="StatusHealthItemsTests"/> gives: the "a transient backoff raises NO health item"
/// assertion is an ABSENCE assertion, and on a shared host another test's permanently disabled
/// source would make it pass for the wrong reason.</para>
/// </summary>
public sealed class SourceRuntimeStateSurfaceTests : IDisposable
{
    private const string AdminKey = "the-real-admin-key";
    private const string SourcesRoute = "/api/admin/sources";

    /// <summary>Loopback discard port: refuses immediately, so the inline caps refresh fails fast.</summary>
    private const string RefusedBaseUrl = "http://127.0.0.1:1/";

    private readonly ArbitarrWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// THE CENTRAL ASSERTION, at N=3 IN ONE RESPONSE: one healthy source, one backing off, one
    /// permanently disabled — and exactly ONE health item, naming the third.
    ///
    /// <para><b>Three sources rather than one, because "a health item exists" passes vacuously with
    /// one.</b> A projection that raised an item for every source with a backoff row, or for every
    /// source at all, satisfies a single-source test and fails here. Each source is asserted BY NAME
    /// on both surfaces, per CLAUDE.md §4's per-row rule.</para>
    ///
    /// <para><b>The transient backoff raising NO item is the load-bearing negative.</b> It clears
    /// itself within minutes, so an item for it would be noise — and a design that raises one is
    /// exactly what the bead's "a transient backoff is NOT [a health item]" rules out. The positive
    /// control for that absence is the permanently disabled source in the SAME response: the payload
    /// demonstrably does carry health items, so the backing-off source's absence from it is a real
    /// negative rather than an empty set proving nothing.</para>
    /// </summary>
    [Fact]
    public async Task Only_a_permanently_disabled_source_raises_a_blocking_health_item()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var healthy = await CreateSourceAsync(admin, "Healthy hydra");
        var backingOff = await CreateSourceAsync(admin, "Backing hydra");
        var rejected = await CreateSourceAsync(admin, "Rejected hydra");

        await SeedBackoffAsync(backingOff.DisplayName, permanentlyDisabled: false, disabledUntil: DateTimeOffset.UtcNow.AddHours(1), level: 2);
        await SeedBackoffAsync(rejected.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        using var pub = _factory.CreateClient();
        var status = await pub.GetFromJsonAsync<StatusResponse>("/api/status");

        var item = Assert.Single(status!.Health, h => h.Key == StatusEndpoint.SourcePermanentlyDisabledKey);
        Assert.Equal("blocking", item.Severity);
        Assert.Equal(rejected.DisplayName, item.SourceName);

        // PER SOURCE. The other two must NOT appear — asserted by name, because "one item exists"
        // would also hold for an implementation that raised one item for the wrong source.
        Assert.DoesNotContain(status.Health, h => h.SourceName == backingOff.DisplayName);
        Assert.DoesNotContain(status.Health, h => h.SourceName == healthy.DisplayName);
    }

    /// <summary>
    /// <b>THE ITEM DOES NOT CLEAR ON ELAPSED TIME.</b> Health items are cleared only by the specific
    /// event that proves the condition is over — never by elapsed time, a worker cycle, or a
    /// successful search (CONTEXT.md "Health item", ADR 0016).
    ///
    /// <para>The row is aged by REWRITING <c>UpdatedAt</c> far into the past and re-reading, which
    /// makes the passage of time real to the endpoint without a fake clock: any implementation that
    /// expired the item after an interval, or that filtered by recency the way an operational event
    /// sweep does, fails here. The one thing that DOES clear it — a genuine success recorded against
    /// that source — is asserted in the next test, so this is not merely "nothing ever clears it".
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_health_item_survives_an_arbitrary_amount_of_elapsed_time()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var rejected = await CreateSourceAsync(admin, "Ancient rejection");
        await SeedBackoffAsync(rejected.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        using var pub = _factory.CreateClient();
        var before = await pub.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.Contains(before!.Health, h => h.SourceName == rejected.DisplayName);

        // A year, which is past every retention and coalescing window in this codebase.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-365);
        await _factory.SeedAsync(async db =>
        {
            var row = db.SourceBackoffStates.Single(s => s.SourceName == rejected.DisplayName);
            row.UpdatedAt = longAgo;
            await Task.CompletedTask;
        });

        var after = await pub.GetFromJsonAsync<StatusResponse>("/api/status");

        var item = Assert.Single(after!.Health, h => h.SourceName == rejected.DisplayName);
        Assert.Equal("blocking", item.Severity);

        // And the instant it reports is the one the row carries, not "now" — an item that restamped
        // itself on every read would tell an operator the condition had just begun, every time.
        Assert.Equal(longAgo.ToUnixTimeSeconds(), item.ObservedSinceUtc.ToUnixTimeSeconds());
    }

    /// <summary>
    /// The PROVING EVENT clears it: a genuine <see cref="SourceCallOutcome.Success"/> recorded
    /// against that source, which is the only thing that resets <c>IsPermanentlyDisabled</c>.
    ///
    /// <para>Driven through <see cref="SourceBackoffStore.RecordOutcomeAsync"/> — the real writer —
    /// rather than by blanking the column, so this exercises the same path a recovered indexer takes
    /// and would fail if that path stopped clearing the flag.</para>
    /// </summary>
    [Fact]
    public async Task A_successful_call_retires_the_health_item()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var rejected = await CreateSourceAsync(admin, "Recovering hydra");
        await SeedBackoffAsync(rejected.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        using var pub = _factory.CreateClient();
        var before = await pub.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.Contains(before!.Health, h => h.SourceName == rejected.DisplayName);

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<SourceBackoffStore>()
                .RecordOutcomeAsync(rejected.DisplayName, SourceCallOutcome.Success);
        }

        var after = await pub.GetFromJsonAsync<StatusResponse>("/api/status");
        Assert.DoesNotContain(after!.Health, h => h.SourceName == rejected.DisplayName);
    }

    /// <summary>
    /// <b>THE PUBLIC SUMMARY CARRIES NO UPSTREAM TEXT, WITH A POSITIVE CONTROL</b> (CLAUDE.md §4).
    ///
    /// <para>An <c>Assert.DoesNotContain</c> over a payload passes just as happily when the value was
    /// never in play, so this first plants a distinctive marker where an implementation that echoed
    /// row-derived free text WOULD carry it — in <c>LastOutcome</c>, the only string column on the
    /// backoff row — and proves that marker is genuinely reachable by finding it on the ADMIN
    /// response. Only then does it assert the PUBLIC response carries none of it. Without the
    /// admin-side hit this assertion would hold against an endpoint that emitted no health block at
    /// all.</para>
    /// </summary>
    [Fact]
    public async Task The_public_summary_is_built_from_the_source_name_and_never_from_row_text()
    {
        const string PlantedUpstreamText = "planted-upstream-text-that-must-not-be-public";

        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var rejected = await CreateSourceAsync(admin, "Leaky hydra");
        await SeedBackoffAsync(
            rejected.DisplayName,
            permanentlyDisabled: true,
            disabledUntil: null,
            level: 0,
            lastOutcome: PlantedUpstreamText);

        // POSITIVE CONTROL. The marker really is stored and really does reach a response body, so
        // the absence assertion below is about the public route's projection and not about a value
        // that was never there.
        using var adminList = await admin.GetAsync(SourcesRoute);
        var adminBody = await adminList.Content.ReadAsStringAsync();
        Assert.Contains(PlantedUpstreamText, adminBody, StringComparison.Ordinal);

        using var pub = _factory.CreateClient();
        using var statusResponse = await pub.GetAsync("/api/status");
        var publicBody = await statusResponse.Content.ReadAsStringAsync();

        // The raw text, not a deserialized field: a leak through an unexpected property name is
        // caught just as well as one through Summary.
        Assert.DoesNotContain(PlantedUpstreamText, publicBody, StringComparison.Ordinal);

        // And the item IS present, so the absence above is not satisfied by an empty health block.
        var status = await statusResponse.Content.ReadFromJsonAsync<StatusResponse>();
        var item = Assert.Single(status!.Health, h => h.SourceName == rejected.DisplayName);
        Assert.Contains(rejected.DisplayName, item.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// The un-gated route carries NO per-source budget or backoff detail at all — only the one health
    /// item. Publishing "indexer X has spent 47 of its 50 daily queries" on a public route is
    /// operational intelligence about a private deployment that nobody has ratified.
    ///
    /// <para>Each field is hunted by its wire NAME, so adding one to <c>StatusResponse</c> later
    /// fails this rather than passing silently.</para>
    /// </summary>
    [Fact]
    public async Task The_public_status_route_carries_no_per_source_budget_or_backoff_detail()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var source = await CreateSourceAsync(admin, "Detail-free hydra");
        await SeedBackoffAsync(source.DisplayName, permanentlyDisabled: true, disabledUntil: DateTimeOffset.UtcNow.AddHours(1), level: 4);

        using var pub = _factory.CreateClient();
        using var statusResponse = await pub.GetAsync("/api/status");
        var body = await statusResponse.Content.ReadAsStringAsync();

        foreach (var field in new[]
                 {
                     "queriesUsed", "grabsUsed", "queryLimit", "grabLimit",
                     "disabledUntil", "disabledLevel", "runtimeState",
                 })
        {
            Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
        }

        // arb-mhd2: `lastOutcome` can no longer be hunted by NAME here, because the name now has two
        // unrelated owners. The backoff row's LastOutcome (a SourceCallOutcome: the escalation
        // decision, and still admin-only) is NOT the same field as SourceStatus.LastOutcome (a
        // SourceStatusOutcome: the closed diagnostic value this route publishes by design).
        //
        // So the property is asserted on the VALUE SPACE instead, which is the stronger form anyway:
        // every published lastOutcome must be one of the closed status names, which means no
        // SourceCallOutcome member can have reached this body through the collided name. The two
        // vocabularies are disjoint, so this fails if the backoff value is ever projected here.
        using var parsed = JsonDocument.Parse(body);
        var published = parsed.RootElement.GetProperty("sources").EnumerateArray().ToList();
        Assert.NotEmpty(published);
        foreach (var row in published)
        {
            Assert.Contains(row.GetProperty("lastOutcome").GetString(), ClosedStatusOutcomeNames);
        }

        // The backoff vocabulary's own members, by name, are absent regardless.
        foreach (var backoffOutcome in Enum.GetNames<SourceCallOutcome>())
        {
            Assert.DoesNotContain(backoffOutcome, body, StringComparison.OrdinalIgnoreCase);
        }

        // Positive control for the sweep: the SAME source's detail IS on the admin route, so the
        // absences above are the public projection's doing rather than a source that had no detail.
        using var adminList = await admin.GetAsync(SourcesRoute);
        var adminBody = await adminList.Content.ReadAsStringAsync();
        Assert.Contains("disabledLevel", adminBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queriesUsed", adminBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// arb-mhd2: the closed vocabulary <c>/api/status</c> may publish for a source's last failure.
    /// Spelled out as literals rather than derived from <c>SourceStatusOutcome</c>: deriving them
    /// would make this agree with the implementation automatically, including about a member added
    /// later that was never meant to be public.
    /// </summary>
    private static readonly string[] ClosedStatusOutcomeNames =
    [
        "none", "upstream-error", "unreachable", "timeout", "auth-rejected", "internal-error",
        "unknown",
    ];

    /// <summary>
    /// <b>THE ADMIN DETAIL, ASSERTED PER SOURCE AT N=3.</b> Three sources in one list response must
    /// report three DIFFERENT states. "Some row is backing off" still passes when an implementation
    /// writes one value to every row (CLAUDE.md §4), which is precisely the projection defect a
    /// per-source surface would then render.
    /// </summary>
    [Fact]
    public async Task The_admin_list_reports_each_source_its_own_runtime_state()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var healthy = await CreateSourceAsync(admin, "State healthy");
        var backingOff = await CreateSourceAsync(admin, "State backing");
        var rejected = await CreateSourceAsync(admin, "State rejected");

        await SeedBackoffAsync(backingOff.DisplayName, permanentlyDisabled: false, disabledUntil: DateTimeOffset.UtcNow.AddHours(1), level: 3);
        await SeedBackoffAsync(rejected.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);

        Assert.Equal(
            nameof(SourceRuntimeState.Healthy),
            Single(listed, healthy.DisplayName).RuntimeState);

        var backing = Single(listed, backingOff.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.BackingOff), backing.RuntimeState);
        Assert.Equal(3, backing.DisabledLevel);
        Assert.NotNull(backing.DisabledUntil);

        var disabled = Single(listed, rejected.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.PermanentlyDisabled), disabled.RuntimeState);
    }

    /// <summary>
    /// A hold-off whose instant has PASSED reports healthy, not backing off. The row does not clear
    /// <c>DisabledUntil</c> eagerly, so a projection keyed off the field being non-null would report
    /// a recovered source as broken indefinitely — and the instant is still projected, because the
    /// level it was reached at remains live information.
    /// </summary>
    [Fact]
    public async Task An_elapsed_hold_off_reports_healthy_while_still_projecting_the_instant()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var recovered = await CreateSourceAsync(admin, "Elapsed hydra");
        await SeedBackoffAsync(recovered.DisplayName, permanentlyDisabled: false, disabledUntil: DateTimeOffset.UtcNow.AddHours(-1), level: 2);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);
        var source = Single(listed, recovered.DisplayName);

        Assert.Equal(nameof(SourceRuntimeState.Healthy), source.RuntimeState);
        Assert.NotNull(source.DisabledUntil);
        Assert.Equal(2, source.DisabledLevel);
    }

    /// <summary>
    /// Precedence, end to end: permanently disabled AND an expired hold-off reports PERMANENTLY
    /// DISABLED, per <c>IsCallableAsync</c>'s documented ordering. Consulting the instant first would
    /// let a source with a rejected key read as recovered the moment an unrelated hold-off ran out.
    /// </summary>
    [Fact]
    public async Task A_permanent_disable_outranks_an_expired_hold_off_on_the_wire()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var source = await CreateSourceAsync(admin, "Precedence hydra");
        await SeedBackoffAsync(source.DisplayName, permanentlyDisabled: true, disabledUntil: DateTimeOffset.UtcNow.AddHours(-1), level: 5);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);

        Assert.Equal(
            nameof(SourceRuntimeState.PermanentlyDisabled),
            Single(listed, source.DisplayName).RuntimeState);
    }

    /// <summary>
    /// <b>NULL LIMIT IS UNLIMITED AND IS NOT ZERO, on the wire.</b> Both directions in one response:
    /// the unlimited source reports a null cap and is NOT budgeted however the tally reads, while the
    /// capped one keeps its number. Collapsing either way is silent — see <c>Source.QueryLimit</c>.
    /// </summary>
    [Fact]
    public async Task A_null_query_limit_stays_null_on_the_wire_and_is_never_budgeted()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var unlimited = await CreateSourceAsync(admin, "Unlimited hydra");
        var capped = await CreateSourceAsync(admin, "Capped hydra", queryLimit: 50);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);

        var withoutCap = Single(listed, unlimited.DisplayName);
        Assert.Null(withoutCap.QueryLimit);
        Assert.Equal(nameof(SourceRuntimeState.Healthy), withoutCap.RuntimeState);

        var withCap = Single(listed, capped.DisplayName);
        Assert.Equal(50, withCap.QueryLimit);
    }

    /// <summary>
    /// A source at its configured cap reports BUDGETED, and one under it does not — both in one
    /// response, so a constant-returning projection cannot pass either half.
    ///
    /// <para>The hits are planted as real <c>EventEntry</c> rows with a <c>RepeatCount</c> ABOVE ONE,
    /// because <c>SourceApiHitCounter</c> sums RepeatCount rather than counting rows and a row count
    /// undercounts SILENTLY — the number is merely low, never wrong-looking. A single-repeat fixture
    /// would pass against that bug.</para>
    /// </summary>
    [Fact]
    public async Task A_source_at_its_cap_is_budgeted_and_one_under_it_is_not()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var spent = await CreateSourceAsync(admin, "Spent hydra", queryLimit: 5);
        var remaining = await CreateSourceAsync(admin, "Remaining hydra", queryLimit: 5);

        // 5 hits in ONE row: at the cap, so the next call would exceed it.
        await SeedQueryHitsAsync(spent.DisplayName, repeatCount: 5);
        // 2 hits, under the same cap.
        await SeedQueryHitsAsync(remaining.DisplayName, repeatCount: 2);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);

        var atCap = Single(listed, spent.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.Budgeted), atCap.RuntimeState);
        // The RepeatCount really was summed: a row count would report 1 here and still call it
        // budgeted only by accident of a cap of 1.
        Assert.Equal(5, atCap.QueriesUsed);

        var underCap = Single(listed, remaining.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.Healthy), underCap.RuntimeState);
        Assert.Equal(2, underCap.QueriesUsed);
    }

    /// <summary>
    /// <b>A SPENT GRAB ALLOWANCE IS BUDGETED ON THE WIRE</b>, per source, at the boundary.
    ///
    /// <para>arch-511's defect end to end: the derivation read the query pair only, so a source whose
    /// GRABS were spent rendered Healthy while <c>BudgetedUpstreamSource</c> refused every download
    /// from it. The grab-spent source here has its query allowance deliberately UNDER its cap, so
    /// nothing but the grab arm can produce the Budgeted it reports.</para>
    ///
    /// <para><b>Both directions in one response, per CLAUDE.md §4's per-row rule.</b> The second
    /// source is at the grab boundary but one UNDER it, which is the positive control the first
    /// assertion needs: an implementation that called every source with a grab limit budgeted would
    /// satisfy the first assertion and fail the second, and one that ignored grabs entirely fails the
    /// first. The tallies are asserted alongside the state so a Budgeted reached with the wrong
    /// numbers does not pass.</para>
    /// </summary>
    [Fact]
    public async Task A_source_whose_grabs_are_spent_is_budgeted_though_its_queries_are_not()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var spentGrabs = await CreateSourceAsync(admin, "Grabbed hydra", queryLimit: 50, grabLimit: 5);
        var underGrabs = await CreateSourceAsync(admin, "Grabbing hydra", queryLimit: 50, grabLimit: 5);

        // Queries well under the cap on BOTH, so the query arm cannot account for either verdict.
        await SeedQueryHitsAsync(spentGrabs.DisplayName, repeatCount: 3);
        await SeedQueryHitsAsync(underGrabs.DisplayName, repeatCount: 3);

        // AT the grab cap: the allowance is spent and the next download would exceed it.
        await SeedGrabHitsAsync(spentGrabs.DisplayName, repeatCount: 5);
        // One UNDER the same cap.
        await SeedGrabHitsAsync(underGrabs.DisplayName, repeatCount: 4);

        var listed = await admin.GetFromJsonAsync<List<SourceResponse>>(SourcesRoute);

        var budgeted = Single(listed, spentGrabs.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.Budgeted), budgeted.RuntimeState);
        Assert.Equal(5, budgeted.GrabsUsed);
        // The query allowance really was untouched, so Budgeted above came from the grab arm.
        Assert.Equal(3, budgeted.QueriesUsed);

        var healthy = Single(listed, underGrabs.DisplayName);
        Assert.Equal(nameof(SourceRuntimeState.Healthy), healthy.RuntimeState);
        Assert.Equal(4, healthy.GrabsUsed);
        Assert.Equal(3, healthy.QueriesUsed);
    }

    /// <summary>
    /// <b>THE PUBLIC ITEM MAY NAME ONLY A SOURCE THIS RESPONSE ALREADY NAMES</b> (sec-511).
    ///
    /// <para><b>The reused source really is CONFIGURED, which is what makes this bite.</b> The
    /// pre-existing join already drops a row whose name matches no configured source, so a fixture
    /// that only planted an orphaned row would pass without the new filter and prove nothing. This
    /// one reproduces the actual sequence: the row is planted under a name, a NEW source is then
    /// created taking that name, and its health row is DELETED so it stands for a source that has
    /// never been called. It passes the configured-source join and is stopped only by the published
    /// -name filter. Projecting it would publish that name on this un-gated route for the first time
    /// and blame it for a credential failure that was never its own.</para>
    ///
    /// <para><b>The positive control is in the same response and is what makes the absence bite.</b> A
    /// second source that HAS both rows raises its item normally, so the payload demonstrably does
    /// carry items of this kind: the first source's absence is the filter's doing rather than an
    /// endpoint that produced nothing at all. Asserted per source, by name.</para>
    /// </summary>
    [Fact]
    public async Task A_configured_source_this_response_never_names_raises_no_public_item()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        // The freed name, taken by a source that has never been called: its backoff row belongs to
        // the source that was renamed AWAY from this name, not to this one.
        var reused = await CreateSourceAsync(admin, "Reused name hydra");
        await SeedBackoffAsync(reused.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);
        await DeleteSourceHealthAsync(reused.DisplayName);

        var published = await CreateSourceAsync(admin, "Published hydra");
        await SeedBackoffAsync(published.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        using var pub = _factory.CreateClient();
        using var response = await pub.GetAsync("/api/status");
        var body = await response.Content.ReadAsStringAsync();
        var status = await pub.GetFromJsonAsync<StatusResponse>("/api/status");

        // The premise: this response really does not name the reused source, so an item naming it
        // would be publishing that name for the first time.
        Assert.DoesNotContain(status!.Sources, s => s.SourceName == reused.DisplayName);

        // The positive control: this response really does carry items of this key, so the absence
        // asserted below is a filtered item and not an empty health list.
        var item = Assert.Single(status.Health, h => h.Key == StatusEndpoint.SourcePermanentlyDisabledKey);
        Assert.Equal(published.DisplayName, item.SourceName);

        // PER SOURCE, and then over the whole body: the name reaches no field of the response.
        Assert.DoesNotContain(status.Health, h => h.SourceName == reused.DisplayName);
        Assert.DoesNotContain(reused.DisplayName, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The runtime detail is ADMIN-GATED, asserted BY NAME.
    ///
    /// <para>The list route is concrete rather than templated, so
    /// <see cref="AdminApiKeyRouteEnumerationTests"/> does cover its gating — but that sweep proves
    /// the ROUTE is gated, not that the new DETAIL is behind the gate rather than on some other
    /// surface. This asserts the pairing directly: no key yields no detail, and the same request with
    /// the key yields it.</para>
    /// </summary>
    [Fact]
    public async Task The_runtime_detail_is_only_readable_with_the_admin_key()
    {
        await SeedAdminKeyAsync();
        using var admin = CreateAdminClient();

        var source = await CreateSourceAsync(admin, "Gated hydra");
        await SeedBackoffAsync(source.DisplayName, permanentlyDisabled: true, disabledUntil: null, level: 0);

        using var anonymous = _factory.CreateClient();
        using var denied = await anonymous.GetAsync(SourcesRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var deniedBody = await denied.Content.ReadAsStringAsync();
        Assert.DoesNotContain("runtimeState", deniedBody, StringComparison.OrdinalIgnoreCase);

        // Positive control: the very same path DOES carry it once the key is attached, so the
        // absence above is the gate's doing rather than a field that is never projected at all.
        using var allowed = await admin.GetAsync(SourcesRoute);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("runtimeState", await allowed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The sources list stays <c>AdminRead</c>-classified and <c>/api/status</c> stays
    /// <c>PublicRead</c>. Pinned because the split this bead rests on is a property of those two
    /// classifications, and a route that quietly changed one would move the detail across the gate
    /// without failing anything else here.
    /// </summary>
    [Fact]
    public void The_two_surfaces_keep_the_classifications_the_split_depends_on()
    {
        var endpoints = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToList();

        var status = Assert.Single(endpoints, e => e.RoutePattern.RawText == "/api/status");
        Assert.Equal(
            RouteClassification.PublicRead,
            status.Metadata.GetMetadata<RouteClassificationMetadata>()!.Classification);

        var sources = Assert.Single(
            endpoints,
            e => e.RoutePattern.RawText == SourcesRoute
                 && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        Assert.NotEqual(
            RouteClassification.PublicRead,
            sources.Metadata.GetMetadata<RouteClassificationMetadata>()!.Classification);
    }

    private static SourceResponse Single(List<SourceResponse>? listed, string displayName) =>
        Assert.Single(listed!, s => s.DisplayName == displayName);

    private HttpClient CreateAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, AdminKey);
        return client;
    }

    private static async Task<SourceResponse> CreateSourceAsync(
        HttpClient client,
        string displayName,
        int? queryLimit = null,
        int? grabLimit = null)
    {
        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = "NzbHydra",
            displayName,
            baseUrl = RefusedBaseUrl,
            enabled = true,
            queryLimit,
            grabLimit,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceResponse>())!;
    }

    /// <summary>
    /// Plants a backoff row directly. The ROW is what both surfaces read, and driving
    /// <c>RecordOutcomeAsync</c> instead would make each fixture depend on the escalation policy's
    /// period table — so a change to those figures would break tests that are about projection.
    /// The one test whose subject IS the writer drives it explicitly.
    /// </summary>
    private Task SeedBackoffAsync(
        string sourceName,
        bool permanentlyDisabled,
        DateTimeOffset? disabledUntil,
        int level,
        string? lastOutcome = null) =>
        _factory.SeedAsync(async db =>
        {
            db.SourceBackoffStates.Add(new SourceBackoffState
            {
                SourceName = sourceName,
                IsPermanentlyDisabled = permanentlyDisabled,
                DisabledUntil = disabledUntil,
                DisabledLevel = level,
                LastOutcome = lastOutcome,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });

    /// <summary>
    /// Removes a source's circuit-breaker row, which is what <c>/api/status</c> builds its
    /// <c>SourceStatus</c> list from. Creating a source through the admin route records a failed
    /// inline caps refresh against the refused base URL, so a row exists by the time the fixture
    /// returns; deleting it is how a source that has genuinely never been called is expressed.
    /// </summary>
    private Task DeleteSourceHealthAsync(string sourceName) =>
        _factory.SeedAsync(async db =>
        {
            var record = await db.SourceHealthRecords
                .SingleOrDefaultAsync(r => r.SourceName == sourceName);

            if (record is not null)
            {
                db.SourceHealthRecords.Remove(record);
            }
        });

    /// <summary>
    /// Plants query hits as ONE event row carrying <paramref name="repeatCount"/>, which is how a
    /// busy indexer's burst actually lands: <c>EventRepository</c> folds a repeat onto the previous
    /// row rather than appending. A fixture of N separate rows would pass against a counter that
    /// counted rows instead of summing RepeatCount — the silent undercount that type exists to
    /// prevent.
    /// </summary>
    private Task SeedQueryHitsAsync(string sourceName, int repeatCount) =>
        SeedHitsAsync(sourceName, EventKind.SourceQueryHit, repeatCount);

    /// <summary>
    /// The GRAB counterpart, under the same one-row-carrying-RepeatCount rule and for the same
    /// reason. A separate KIND rather than a separate table is the whole shape of the budget: the
    /// counter asks the same question of each kind, which is why the derivation has to ask both.
    /// </summary>
    private Task SeedGrabHitsAsync(string sourceName, int repeatCount) =>
        SeedHitsAsync(sourceName, EventKind.SourceGrabHit, repeatCount);

    private Task SeedHitsAsync(string sourceName, EventKind kind, int repeatCount) =>
        _factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Events.Add(new EventEntry
            {
                Kind = kind,
                SourceDisplayName = sourceName,
                Summary = "Api hit",
                OccurredAt = now,
                LastRepeatedAt = now,
                RepeatCount = repeatCount,
            });
            await Task.CompletedTask;
        });

    private async Task SeedAdminKeyAsync()
    {
        await _factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = AdminKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = AdminKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });
    }
}
