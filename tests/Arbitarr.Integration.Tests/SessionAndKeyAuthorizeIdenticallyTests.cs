using System.Net;
using System.Net.Http.Json;
using Arbitarr.Api.Admin;
using Arbitarr.Api.Security;
using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #44 AC5, asserted rather than claimed: a gated route makes ONE scope check, over whichever
/// credential answered. A session and an equivalently-scoped #58 API key are interchangeable at the
/// gate, and a session is refused exactly where an equivalently-scoped key would be.
///
/// <para><b>WHY THIS FILE EXISTS SEPARATELY FROM <see cref="AuthSessionEndpointsTests"/>.</b> Those tests
/// ask "does the session surface work?"; these ask "is there one authorization model or two?" The
/// owner ruling on #44 names a second model as "the exact failure this pair exists to prevent", and
/// the property is not visible from either credential's own tests — only from driving the SAME
/// routes with BOTH and comparing. <c>AdminApiKeyFilter</c> and <c>ISessionAuthenticator</c> both
/// name this class in their type docs as the thing that holds them to it.</para>
///
/// <para><b>EVERY TEST HERE SEEDS AN ADMIN KEY FIRST</b>, via <see cref="CreateClaimedFactoryAsync"/>.
/// With no key configured the resolver answers <c>NotConfigured</c> and #43's bootstrap bypass
/// admits any local caller — which is every caller here — so the gate would answer 200 without ever
/// consulting a credential and every comparison below would be between two bypasses rather than
/// between two authorizations. See <c>AuthEndpointTestSupport.ConfigureAdminKeyAsync</c> for the same
/// trap in its other form.</para>
/// </summary>
public sealed class SessionAndKeyAuthorizeIdenticallyTests
{
    private const string LegacyKey = "the-real-admin-key";
    private const string Username = "operator";
    private const string Password = "example-operator-passphrase";

    /// <summary>An admin-scope route (<c>RequireAdminApiKey()</c> with no argument).</summary>
    private const string AdminScopeRoute = "/api/admin/ping";

    /// <summary>A route that requires only <see cref="ApiKeyScope.ReadOnly"/>.</summary>
    private const string ReadOnlyScopeRoute = "/api/admin/observability";

    /// <summary>
    /// A loopback-stamped host with an admin key already configured — an instance that has been
    /// claimed, which is the only state in which the gate actually adjudicates a credential.
    /// </summary>
    private static async Task<RemoteAddressWebApplicationFactory> CreateClaimedFactoryAsync()
    {
        var factory = new RemoteAddressWebApplicationFactory(IPAddress.Loopback);

        await factory.SeedAsync(async db =>
        {
            var existing = await db.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
            if (existing is null)
            {
                db.Settings.Add(new SettingEntry
                {
                    Name = SettingKey.AdminApiKey.ToString(),
                    Value = LegacyKey,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });
            }
            else
            {
                existing.Value = LegacyKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        });

        return factory;
    }

    /// <summary>Creates the first account and returns a client presenting the resulting session.</summary>
    private static async Task<HttpClient> CreateSessionClientAsync(RemoteAddressWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");

        using var response = await client.PostAsJsonAsync(
            AuthEndpoints.SetupRoute,
            new { username = Username, password = Password });

        if (response.StatusCode != HttpStatusCode.Created)
        {
            Assert.Fail(await DescribeSetupFailureAsync(factory, response));
        }

        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(ISessionAuthenticator.CookieName + "=", StringComparison.Ordinal));

        var token = setCookie.Split(';', 2)[0].Split('=', 2)[1];
        client.DefaultRequestHeaders.Add("Cookie", $"{ISessionAuthenticator.CookieName}={token}");

        return client;
    }

    /// <summary>
    /// arb-j4g9: builds the assertion message for a setup POST that did not return 201, with the
    /// response body and the Error-level log rows the server wrote while failing it.
    ///
    /// <para>This is a local twin of <c>ActivityEmissionTests.DescribeFailureWithErrorLogsAsync</c>,
    /// not an extraction into shared TestSupport: that method is an instance method reading its own
    /// class's <c>_factory</c> field, while <c>CreateSessionClientAsync</c> here is <c>static</c> and
    /// takes its factory as a parameter. Sharing it would mean changing
    /// <c>ActivityEmissionTests</c>' method to accept a factory argument too, which the brief for
    /// this change rules out (its passing-path behaviour must stay byte-for-byte identical). The
    /// poll shape — bounded wall-clock, inside the existing failure path so a throwing read cannot
    /// mask the original failure — is copied from that method on purpose; the reasoning for it is
    /// recorded there.</para>
    /// </summary>
    private static async Task<string> DescribeSetupFailureAsync(
        RemoteAddressWebApplicationFactory factory,
        HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        const int maxBodyLength = 2000;
        if (body.Length > maxBodyLength)
        {
            body = body[..maxBodyLength] + $"... (truncated, {body.Length} chars total)";
        }

        var lines = new List<string>
        {
            $"Account setup returned {(int)response.StatusCode} ({response.StatusCode}) rather than {(int)HttpStatusCode.Created} (Created).",
            $"Response body: {body}",
        };

        // The store read is itself best-effort: a failure to fetch the diagnostic must report the
        // ORIGINAL failure plus why the diagnostic is missing, never replace one with the other.
        try
        {
            var store = factory.Services.GetRequiredService<LogStore>();

            // Bounded poll instead of a fixed sleep: stop as soon as a row shows up, and give up
            // after ~3s (well beyond the log sink's flush interval) if it genuinely wrote nothing.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            var page = await store.ReadAsync(level: "Error", logger: null, page: 1, pageSize: 50);
            while (page.Entries.Count == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(75);
                page = await store.ReadAsync(level: "Error", logger: null, page: 1, pageSize: 50);
            }

            if (page.Entries.Count == 0)
            {
                lines.Add("No Error-level log rows were present in the store.");
            }
            else
            {
                lines.Add($"{page.Entries.Count} Error-level log row(s) of {page.Total} total:");
                lines.AddRange(page.Entries.Select(e =>
                    $"  [{e.Time:O}] {e.Logger}: {e.Message}"
                    + (e.ExceptionType is null ? string.Empty : $" | {e.ExceptionType}")
                    + (e.Exception is null ? string.Empty : $" | {e.Exception}")));
            }
        }
        catch (Exception ex)
        {
            lines.Add($"The Error-level log rows could not be read for this failure: {ex}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A client presenting an API key of <paramref name="scope"/> and no session.</summary>
    private static async Task<HttpClient> CreateKeyClientAsync(
        RemoteAddressWebApplicationFactory factory,
        ApiKeyScope scope)
    {
        string plaintext;
        using (var serviceScope = factory.Services.CreateScope())
        {
            var keys = serviceScope.ServiceProvider.GetRequiredService<ApiKeyRepository>();
            var created = await keys.CreateAsync($"{scope}-key", scope, CancellationToken.None);
            plaintext = created.PlaintextKey;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.HeaderName, plaintext);
        return client;
    }

    private static Task<HttpResponseMessage> CallAsync(HttpClient client, string route) =>
        route == AdminScopeRoute
            ? client.PostAsync(route, content: null)
            : client.GetAsync(route);

    [Fact]
    public async Task A_session_and_an_admin_key_are_both_admitted_by_an_admin_scope_route()
    {
        // The single-model claim's positive half: the gate does not care WHICH credential answered,
        // only what scope it carried.
        await using var factory = await CreateClaimedFactoryAsync();

        using var sessionClient = await CreateSessionClientAsync(factory);
        using var keyClient = await CreateKeyClientAsync(factory, ApiKeyScope.Admin);

        using var viaSession = await CallAsync(sessionClient, AdminScopeRoute);
        using var viaKey = await CallAsync(keyClient, AdminScopeRoute);

        Assert.Equal(HttpStatusCode.OK, viaKey.StatusCode);
        Assert.Equal(viaKey.StatusCode, viaSession.StatusCode);
    }

    [Fact]
    public async Task A_session_and_an_admin_key_are_both_admitted_by_a_read_only_scope_route()
    {
        // Admin outranks ReadOnly through the SAME comparison for both credentials — the scope
        // decision is written once and neither path has its own.
        await using var factory = await CreateClaimedFactoryAsync();

        using var sessionClient = await CreateSessionClientAsync(factory);
        using var keyClient = await CreateKeyClientAsync(factory, ApiKeyScope.Admin);

        using var viaSession = await CallAsync(sessionClient, ReadOnlyScopeRoute);
        using var viaKey = await CallAsync(keyClient, ReadOnlyScopeRoute);

        Assert.Equal(HttpStatusCode.OK, viaKey.StatusCode);
        Assert.Equal(viaKey.StatusCode, viaSession.StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_is_refused_where_a_session_is_admitted()
    {
        // THE NON-VACUITY CONTROL FOR THE TWO TESTS ABOVE. Without it they would pass just as
        // happily against a gate that admitted everything — "both got 200" proves a single model
        // only once something is shown to be REFUSED by the same machinery. A ReadOnly key on an
        // admin-scope route is the refusal, and the session's 200 alongside it proves the gate is
        // discriminating on scope rather than waving all callers through.
        await using var factory = await CreateClaimedFactoryAsync();

        using var sessionClient = await CreateSessionClientAsync(factory);
        using var readOnlyClient = await CreateKeyClientAsync(factory, ApiKeyScope.ReadOnly);

        using var viaReadOnlyKey = await CallAsync(readOnlyClient, AdminScopeRoute);
        using var viaSession = await CallAsync(sessionClient, AdminScopeRoute);

        Assert.Equal(HttpStatusCode.Forbidden, viaReadOnlyKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaSession.StatusCode);
    }

    [Fact]
    public async Task No_credential_is_refused_on_a_claimed_instance()
    {
        // The other half of the control: with a key configured, an unauthenticated caller is
        // refused even from loopback. This is also the assertion that #43's bypass is genuinely
        // closed here — if it were open, every comparison in this file would be vacuous.
        await using var factory = await CreateClaimedFactoryAsync();

        using var anonymous = factory.CreateClient();

        using var response = await CallAsync(anonymous, AdminScopeRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_can_never_reopen_the_unconfigured_key_bypass()
    {
        // Constraint 1 of the owner ruling, at the one place it could actually break: the LAN
        // bypass skips the API KEY, never the login. A session must never resolve to
        // NotConfigured — if a junk cookie could produce it, presenting any cookie on a claimed
        // instance would re-enter #43's bypass and admit an unauthenticated caller.
        await using var factory = await CreateClaimedFactoryAsync();

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(AdminApiKeyFilter.SessionRequestHeaderName, "1");
        client.DefaultRequestHeaders.Add(
            "Cookie",
            $"{ISessionAuthenticator.CookieName}=not-a-real-session-token");

        using var response = await CallAsync(client, AdminScopeRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
