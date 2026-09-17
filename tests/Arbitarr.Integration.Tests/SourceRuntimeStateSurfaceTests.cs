using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Dashboard;
using Arbitarr.Api.Routing;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Microsoft.AspNetCore.Routing;
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
                     "disabledUntil", "disabledLevel", "lastOutcome", "runtimeState",
                 })
        {
            Assert.DoesNotContain(field, body, StringComparison.OrdinalIgnoreCase);
        }

        // Positive control for the sweep: the SAME source's detail IS on the admin route, so the
        // absences above are the public projection's doing rather than a source that had no detail.
        using var adminList = await admin.GetAsync(SourcesRoute);
        var adminBody = await adminList.Content.ReadAsStringAsync();
        Assert.Contains("disabledLevel", adminBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queriesUsed", adminBody, StringComparison.OrdinalIgnoreCase);
    }

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
        int? queryLimit = null)
    {
        using var response = await client.PostAsJsonAsync(SourcesRoute, new
        {
            kind = "NzbHydra",
            displayName,
            baseUrl = RefusedBaseUrl,
            enabled = true,
            queryLimit,
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
    /// Plants query hits as ONE event row carrying <paramref name="repeatCount"/>, which is how a
    /// busy indexer's burst actually lands: <c>EventRepository</c> folds a repeat onto the previous
    /// row rather than appending. A fixture of N separate rows would pass against a counter that
    /// counted rows instead of summing RepeatCount — the silent undercount that type exists to
    /// prevent.
    /// </summary>
    private Task SeedQueryHitsAsync(string sourceName, int repeatCount) =>
        _factory.SeedAsync(async db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.Events.Add(new EventEntry
            {
                Kind = EventKind.SourceQueryHit,
                SourceDisplayName = sourceName,
                Summary = "Query hit",
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
