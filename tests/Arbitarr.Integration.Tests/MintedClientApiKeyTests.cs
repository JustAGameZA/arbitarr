using System.Net;
using Arbitarr.Core.Security;
using Arbitarr.Core.Sources;
using Arbitarr.Data.Logging;
using Arbitarr.Data.Security;
using Arbitarr.Integration.Tests.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #97: the search routes must accept a key minted in Settings &gt; API keys, not only an
/// environment key. This is deliberately host-level rather than a unit test of
/// <c>DbClientApiKeyResolver</c>: the bug being fixed was not that the resolver was wrong, it was
/// that the ROUTES resolved through the config-backed implementation, and only driving the real
/// routes through the real container can establish that they no longer do.
///
/// <para><b>IAsyncLifetime, never <c>System.IAsyncDisposable</c>.</b> xunit v2 awaits
/// <see cref="IAsyncLifetime"/> and calls <see cref="IDisposable"/>; it does not know about
/// <c>System.IAsyncDisposable</c> and silently ignores a class that declares only that. This class
/// declared it, so its teardown had NEVER ONCE RUN — 8 fully INTACT config directories per run,
/// which is why replacing the body of <c>DisposeAsync</c> alone changed nothing (arb-gphi). With the
/// interface corrected the teardown runs and the residue drops to 5 partial ones, whose remaining
/// cause is arb-gz3o — see the note on <see cref="DisposeAsync"/>.
/// Verified against xunit 2.9.2 in a throwaway project outside the repository: a class implementing
/// only <c>System.IAsyncDisposable</c> had its <c>DisposeAsync</c> skipped while a sibling's
/// <c>IDisposable.Dispose</c> ran. A dead teardown reads exactly like a working one at the call
/// site, so the INTERFACE is the load-bearing part of this declaration.</para>
/// </summary>
public sealed class MintedClientApiKeyTests : IAsyncLifetime
{
    // "secret-api-key" is the prefix the pre-commit secret guard allowlists; the suffix keeps it
    // distinctive when searching log rows.
    private const string EnvironmentKey = "secret-api-key-upgrade-client";

    private readonly string _configDirectory = Path.Combine(
        Path.GetTempPath(), "arbitarr-minted-client-key-tests", Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public MintedClientApiKeyTests()
    {
        Directory.CreateDirectory(_configDirectory);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);
            builder.UseSetting("Arbitarr:ApiKey", EnvironmentKey);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IUpstreamSource>();
                services.RemoveAll<ISourceRegistry>();
                services.AddSingleton<ISourceRegistry>(StaticSourceRegistry.Empty);
            });
        });
    }

    /// <summary>
    /// The issue's headline symptom, on both protocol routes: a minted key was answered with error
    /// 100 while the environment key worked. Both must now be accepted, and the minted key is
    /// deliberately <see cref="ApiKeyScope.ReadOnly"/> — proving an Admin scope is NOT required,
    /// which is what the API keys section promises an operator.
    /// </summary>
    [Theory]
    [InlineData("/torznab/api")]
    [InlineData("/newznab/api")]
    public async Task A_minted_read_only_key_and_the_environment_key_both_open_the_protocol_routes(string route)
    {
        using var client = _factory.CreateClient();
        var (_, mintedKey) = await MintReadOnlyKeyAsync();

        foreach (var key in new[] { mintedKey, EnvironmentKey })
        {
            using var response = await client.GetAsync($"{route}?t=caps&apikey={Uri.EscapeDataString(key)}");
            await AssertAcceptedAsync(response);
        }
    }

    /// <summary>
    /// Asserts a protocol route ACCEPTED the key, by the body rather than the status.
    ///
    /// <para>Status alone is worthless here and asserting it was a real bug in an earlier draft of
    /// this file: a rejected key is answered with HTTP <b>200</b> carrying an error-100 document,
    /// deliberately, so an *arr client renders "incorrect credentials" instead of treating the
    /// refusal as a network failure (see <c>ErrorXmlGoldenTests</c>). An
    /// <c>Assert.Equal(OK, response.StatusCode)</c> therefore passes just as happily when the key
    /// was refused — which is exactly the bug these tests exist to catch. The caps document and the
    /// error document are what actually differ, so that is what is asserted.</para>
    /// </summary>
    private static async Task AssertAcceptedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("error code", body, StringComparison.Ordinal);
        Assert.Contains("<caps", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// arb-xwl3: the same acceptance check as <see cref="AssertAcceptedAsync(HttpResponseMessage)"/>,
    /// but able to explain a non-200 rather than merely report one.
    ///
    /// <para><b>WHY THE BODY CANNOT CARRY THE ANSWER.</b> This test host runs with the default
    /// <c>WebApplicationFactory</c> environment (nothing here calls <c>UseEnvironment</c>, so it is
    /// "Production", never "Development"), and <c>Program.cs</c> registers no
    /// <c>UseDeveloperExceptionPage</c> and no <c>UseExceptionHandler</c> anywhere in its pipeline.
    /// An unhandled exception thrown from a route delegate therefore reaches no code of ours at all:
    /// ASP.NET Core's own default behaviour answers a bare, empty-body 500. Reading
    /// <c>response.Content</c> on that path is not merely unhelpful, it is provably empty — the
    /// exception has to be found somewhere else, or not at all.</para>
    ///
    /// <para><b>WHERE IT ACTUALLY GOES.</b> ASP.NET Core's own hosting diagnostics logs an unhandled
    /// request exception at <c>Error</c> level under the category
    /// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c>, through the ordinary <c>ILogger</c> pipeline,
    /// before it answers the empty 500. <c>LoggingSetup.AddArbitarrSqliteLogging</c> filters
    /// <c>Microsoft</c>-prefixed categories down to a <c>Warning</c> FLOOR in the SQLite store — not a
    /// ceiling — so an <c>Error</c> row from that category is not suppressed; it is written through
    /// with its full <see cref="LogEntry.Exception"/> text intact. That is the one place this
    /// diagnostic can still exist once the response is empty, so this reads it from there.
    /// <see cref="An_error_row_logged_under_the_ASP_NET_hosting_diagnostics_category_reaches_the_store_and_the_diagnostic_helper"/>
    /// is the control that proves this empirically rather than leaving it inferred.</para>
    ///
    /// <para><b>WHY PRINTING IT IS SAFE.</b> These rows come back through <see cref="LogStore.ReadAsync"/>
    /// — the store's own read path, the same one <see cref="A_minted_key_driven_through_the_search_pipeline_appears_in_no_log_row"/>
    /// already reads to assert the minted key is ABSENT from every field including
    /// <see cref="LogEntry.Exception"/> — so any credential-shaped text has already passed through
    /// whatever the sink itself applies before this method ever sees it. Nothing here reads a raw
    /// exception object or forms a new sink; it re-reads the same store. And the key involved is a
    /// same-test fixture minted moments earlier by <c>MintReadOnlyKeyAsync</c>, not a real operator's
    /// credential, so even an uncleansed leak here would name nothing an operator holds.</para>
    ///
    /// <para><b>PAGES THROUGH THE WHOLE MATCHING SET, NOT ONE PAGE.</b> <see cref="LogStore.MaxPageSize"/>
    /// caps a single page at 200 rows; a maintenance pass or a chatty run could still push the
    /// Error/Critical rows past that in one page, and stopping at page 1 would silently drop whichever
    /// row is the one actually worth printing.</para>
    ///
    /// <para><b>THE PASS/FAIL CONDITION DOES NOT CHANGE.</b> On success this asserts exactly what
    /// <see cref="AssertAcceptedAsync(HttpResponseMessage)"/> already asserts, in the same order; the
    /// diagnostic text is built ONLY when a non-200 is about to fail the test, and is appended to that
    /// same assertion's message rather than replacing it.</para>
    /// </summary>
    private static async Task AssertAcceptedAsync(HttpResponseMessage response, IServiceProvider services)
    {
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var diagnostic = await DescribeServerErrorsAsync(services);
            Assert.Fail(
                $"Expected {HttpStatusCode.OK} but the response was {(int)response.StatusCode} " +
                $"{response.StatusCode}. {diagnostic}");
        }

        Assert.DoesNotContain("error code", body, StringComparison.Ordinal);
        Assert.Contains("<caps", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Flushes the log sink and pages through every Error-or-above row the store holds, rendering
    /// each one's category, message and exception text. See
    /// <see cref="AssertAcceptedAsync(HttpResponseMessage, IServiceProvider)"/> for why this is the
    /// only place a 500 from this host can still be explained.
    /// </summary>
    private static async Task<string> DescribeServerErrorsAsync(IServiceProvider services)
    {
        await services.FlushLogSinkAsync();

        var store = services.GetRequiredService<LogStore>();
        var rows = new List<LogEntry>();
        var page = 1;
        while (true)
        {
            var result = await store.ReadAsync(level: "Error", logger: null, page: page, pageSize: LogStore.MaxPageSize);
            rows.AddRange(result.Entries);
            if (rows.Count >= result.Total || result.Entries.Count == 0)
            {
                break;
            }

            page++;
        }

        if (rows.Count == 0)
        {
            return "No Error-or-above row was found in the log store to explain it.";
        }

        var rendered = rows.Select(entry =>
            $"[{entry.Level}] {entry.Logger}: {entry.Message}" +
            (string.IsNullOrEmpty(entry.Exception) ? string.Empty : $"{Environment.NewLine}{entry.Exception}"));

        return $"Error-or-above log rows ({rows.Count}):{Environment.NewLine}{string.Join(Environment.NewLine + "---" + Environment.NewLine, rendered)}";
    }

    /// <summary>
    /// <c>/download/{proxyGuid}</c> gets its OWN by-name test on purpose. It is a templated route,
    /// and the route-gating sweep in <c>AdminApiKeyRouteEnumerationTests</c> skips every route
    /// containing "{" because a template needs a real value to resolve — so that sweep passing is
    /// not evidence for this route, and only this test is. See CLAUDE.md §2.
    ///
    /// <para>A non-existent guid answers 404, which is the point: 404 means the key was ACCEPTED
    /// and the lookup then found nothing, whereas a rejected key never reaches the lookup and
    /// answers a bare 401. The two are distinguishable, so this asserts the gate, not the guid.</para>
    /// </summary>
    [Fact]
    public async Task A_minted_read_only_key_and_the_environment_key_both_open_the_templated_download_route()
    {
        using var client = _factory.CreateClient();
        var (_, mintedKey) = await MintReadOnlyKeyAsync();

        foreach (var key in new[] { mintedKey, EnvironmentKey })
        {
            using var response = await client.GetAsync(
                $"/download/not-a-real-guid?apikey={Uri.EscapeDataString(key)}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// An Admin-scope key reaches the search routes too — not because an operator should hand one
    /// to Sonarr, but because a scope that reaches every mutating route must not somehow fail to
    /// reach a read one. If this ever fails, the resolver has grown a scope comparison it must not
    /// have.
    /// </summary>
    [Fact]
    public async Task An_admin_scope_minted_key_also_opens_the_search_routes()
    {
        using var client = _factory.CreateClient();
        var (_, adminKey) = await MintKeyAsync("scripted-admin", ApiKeyScope.Admin);

        using var response = await client.GetAsync($"/torznab/api?t=caps&apikey={Uri.EscapeDataString(adminKey)}");
        await AssertAcceptedAsync(response);
    }

    /// <summary>
    /// Revocation takes effect on the next request, and — the part worth asserting — a revoked key
    /// is refused with a body and status identical to an unknown key's. A caller must not be able
    /// to tell "this key was once valid" from "this key never existed"; asserting only that the
    /// revoked key fails would let a distinguishable error message through unnoticed.
    /// </summary>
    [Fact]
    public async Task A_revoked_minted_key_is_refused_exactly_as_an_unknown_key_is()
    {
        using var client = _factory.CreateClient();
        var (id, mintedKey) = await MintReadOnlyKeyAsync();

        // Establish the key worked BEFORE revocation, so the refusal below is attributable to the
        // revocation rather than to the key never having been accepted at all.
        using (var beforeRevocation = await client.GetAsync(
            $"/torznab/api?t=caps&apikey={Uri.EscapeDataString(mintedKey)}"))
        {
            await AssertAcceptedAsync(beforeRevocation);
        }

        Assert.True(await RevokeKeyAsync(id));

        foreach (var route in new[] { "/torznab/api", "/newznab/api" })
        {
            using var unknown = await client.GetAsync($"{route}?t=caps&apikey=not-the-key");
            using var revoked = await client.GetAsync(
                $"{route}?t=caps&apikey={Uri.EscapeDataString(mintedKey)}");

            var unknownBody = await unknown.Content.ReadAsStringAsync();
            var revokedBody = await revoked.Content.ReadAsStringAsync();

            Assert.Equal(unknown.StatusCode, revoked.StatusCode);
            Assert.Equal(unknownBody, revokedBody);
            Assert.Contains("code=\"100\"", revokedBody, StringComparison.Ordinal);
        }

        using var unknownDownload = await client.GetAsync("/download/not-a-real-guid?apikey=not-the-key");
        using var revokedDownload = await client.GetAsync(
            $"/download/not-a-real-guid?apikey={Uri.EscapeDataString(mintedKey)}");
        Assert.Equal(HttpStatusCode.Unauthorized, unknownDownload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedDownload.StatusCode);
        Assert.Equal(0, unknownDownload.Content.Headers.ContentLength ?? 0);
        Assert.Equal(0, revokedDownload.Content.Headers.ContentLength ?? 0);
    }

    /// <summary>
    /// AC3: attribution is identical in SHAPE for both sources and correct in CONTENT for each. The
    /// minted key resolves to its own label and row id; the environment key resolves to its
    /// configured name with a null id, because it has no row to attribute to. Asserting the whole
    /// record rather than just the name is what catches an id silently going missing.
    /// </summary>
    [Fact]
    public async Task Both_key_sources_resolve_to_the_same_context_shape_carrying_the_keys_own_name()
    {
        var (id, mintedKey) = await MintKeyAsync("sonarr", ApiKeyScope.ReadOnly);
        var resolver = _factory.Services.GetRequiredService<IClientApiKeyResolver>();

        Assert.Equal(
            new ClientKeyContext("sonarr", id),
            await resolver.ResolveAsync(mintedKey, CancellationToken.None));
        Assert.Equal(
            new ClientKeyContext("default"),
            await resolver.ResolveAsync(EnvironmentKey, CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync("not-the-key", CancellationToken.None));
    }

    /// <summary>
    /// A minted key is stamped as used, and — the part that matters — never reaches the persistent
    /// log store. This mirrors <c>LogSecretInjectionTests</c> deliberately, because the primary
    /// control is STRUCTURAL (nothing on the request path logs the inbound query string), with
    /// <see cref="LogMessageCleanser"/> only as defence in depth. A minted key needs its own copy
    /// of that guard rather than inheriting the environment key's: it travels the same inbound
    /// query string, but it also passes through a database lookup and a last-used write the
    /// environment key never touches, and those are new places a value could be logged verbatim.
    ///
    /// <para>"No row contains the key" passes just as happily against an empty table, so the
    /// <c>Assert.NotEmpty</c> below is load-bearing — and the positive control proving a leak of
    /// THIS key would actually be caught is the test immediately after this one.</para>
    /// </summary>
    [Fact]
    public async Task A_minted_key_driven_through_the_search_pipeline_appears_in_no_log_row()
    {
        using var client = _factory.CreateClient();
        var (id, mintedKey) = await MintReadOnlyKeyAsync();

        // Error paths too: a request URL is likeliest to be logged verbatim when something throws,
        // so a happy-path-only probe would miss the realistic leak.
        using (var caps = await client.GetAsync($"/torznab/api?t=caps&apikey={Uri.EscapeDataString(mintedKey)}"))
        {
            await AssertAcceptedAsync(caps, _factory.Services);
        }
        await client.GetAsync($"/newznab/api?t=search&q=probe&apikey={Uri.EscapeDataString(mintedKey)}");
        await client.GetAsync($"/torznab/api?t=notarealmode&apikey={Uri.EscapeDataString(mintedKey)}");
        await client.GetAsync($"/download/not-a-real-guid?apikey={Uri.EscapeDataString(mintedKey)}");

        // The last-used stamp is written off the request path by the throttled recorder, so it
        // lands after the response rather than during it. Poll instead of guessing a delay.
        DateTimeOffset? lastUsed = null;
        for (var attempt = 0; attempt < 50 && lastUsed is null; attempt++)
        {
            await Task.Delay(100);
            lastUsed = await GetLastUsedAsync(id);
        }
        Assert.NotNull(lastUsed);

        await _factory.Services.FlushLogSinkAsync();
        var page = await _factory.Services.GetRequiredService<LogStore>()
            .ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);

        // Without this the loop below is a no-op the day the sink stops recording anything.
        Assert.NotEmpty(page.Entries);

        // Every field, not just Message: Exception is the one most likely to carry a request URI.
        foreach (var entry in page.Entries)
        {
            Assert.DoesNotContain(mintedKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(mintedKey, entry.Exception ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(mintedKey, entry.Logger, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// THE POSITIVE CONTROL for the test above. It plants this very key into a log line through the
    /// real logger and asserts the stored row carries <see cref="LogMessageCleanser.Replacement"/>,
    /// proving the value reached the sink and was scrubbed there rather than never arriving.
    ///
    /// <para>Asserting merely that the key was created would prove the secret EXISTS; it would not
    /// prove it is DETECTABLE if it leaked, and only the second makes the absence assertion above
    /// bite. See CLAUDE.md §4 and <c>LogSecretInjectionTests</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_planted_minted_key_is_detectable_and_is_redacted_before_storage()
    {
        using var client = _factory.CreateClient();
        var (_, mintedKey) = await MintReadOnlyKeyAsync();
        _ = await client.GetAsync("/api/health");

        var logger = _factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Arbitarr.Test.MintedKeyLeakProbe");
        logger.LogWarning(
            "client request failed: http://192.0.2.10:8080/torznab/api?t=caps&apikey={ApiKey}",
            mintedKey);

        await _factory.Services.FlushLogSinkAsync();
        var page = await _factory.Services.GetRequiredService<LogStore>()
            .ReadAsync(level: null, logger: null, page: 1, pageSize: LogStore.MaxPageSize);
        var probed = page.Entries
            .Where(entry => entry.Logger.Contains("MintedKeyLeakProbe", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(probed);
        foreach (var entry in probed)
        {
            Assert.DoesNotContain(mintedKey, entry.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(LogMessageCleanser.Replacement, entry.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// arb-xwl3: THE CONTROL for <see cref="DescribeServerErrorsAsync"/>. It plants an Error-level
    /// row under the exact category ASP.NET Core's own hosting diagnostics use for an unhandled
    /// request exception (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c>) and asserts the helper's
    /// rendered diagnostic text contains the planted exception's message.
    ///
    /// <para>This is the empirical half of <see cref="AssertAcceptedAsync(HttpResponseMessage, IServiceProvider)"/>'s
    /// doc comment: "an Error row from a Microsoft-prefixed category is not suppressed by the
    /// Warning floor" was, until this test, read off <c>LoggingSetup.AddArbitarrSqliteLogging</c>'s
    /// filter call rather than observed. Logging directly under the framework's own category name
    /// (rather than a fabricated stand-in) is what makes this a genuine proof that the filter lets
    /// THIS category through at THIS level, not merely that some Microsoft-prefixed category does.
    /// If this test ever fails because the row does not reach the store, that is a real finding
    /// about the filter, not a defect in the test — the self-reporting design in this file would
    /// need to change with it.</para>
    /// </summary>
    [Fact]
    public async Task An_error_row_logged_under_the_ASP_NET_hosting_diagnostics_category_reaches_the_store_and_the_diagnostic_helper()
    {
        const string exceptionMessage = "arb-xwl3 control: planted unhandled-exception-shaped failure";

        var logger = _factory.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
        logger.LogError(new InvalidOperationException(exceptionMessage), "An unhandled exception has occurred while executing the request.");

        var diagnostic = await DescribeServerErrorsAsync(_factory.Services);

        Assert.Contains(exceptionMessage, diagnostic, StringComparison.Ordinal);
    }

    private Task<(long Id, string Plaintext)> MintReadOnlyKeyAsync() =>
        MintKeyAsync("sonarr", ApiKeyScope.ReadOnly);

    private async Task<(long Id, string Plaintext)> MintKeyAsync(string label, ApiKeyScope scope)
    {
        using var scopeHandle = _factory.Services.CreateScope();
        var repository = scopeHandle.ServiceProvider.GetRequiredService<ApiKeyRepository>();
        var created = await repository.CreateAsync(label, scope, CancellationToken.None);
        return (created.Entry.Id, created.PlaintextKey);
    }

    private async Task<bool> RevokeKeyAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ApiKeyRepository>()
            .RevokeAsync(id, CancellationToken.None);
    }

    private async Task<DateTimeOffset?> GetLastUsedAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<ApiKeyRepository>()
            .GetAllAsync(CancellationToken.None);
        return keys.Single(key => key.Id == id).LastUsedAt;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The delete used to run bare, with no pool clear, inside an empty <c>catch (IOException)</c>
    /// — and on a <c>DisposeAsync</c> xunit never called at all (see the interface note on the class).
    /// <see cref="ConfigDirectoryTeardown"/> clears both databases' pools first (arb-gphi).
    ///
    /// <para><b>TryDelete rather than Delete, and this is the one class where that is not the
    /// factories' reason.</b> With the teardown finally running, this class's directory still
    /// survives it — and MEASURED, the file that wins is <c>arbitarr-logs.db</c>, not
    /// <c>arbitarr.db</c>: "the process cannot access the file 'arbitarr-logs.db' because it is
    /// being used by another process", on all five tests. The log store's connection string is built
    /// with Mode, Cache AND Pooling set and <c>SqlitePools.ClearLogStorePool</c> reproduces that
    /// shape exactly, so the pool clear is naming the right pool — which means the handle was never
    /// RETURNED to it. That is a connection outliving disposal, which is <b>arb-gz3o</b>'s open
    /// question, not this change's: arb-gphi is about teardown that was missing or half-written,
    /// and this teardown is now complete and still loses.</para>
    ///
    /// <para>Throwing here would therefore fail five tests for a defect they do not contain and
    /// which is already tracked, so the residue is left visible to arb-gz3o rather than converted
    /// into a red suite. This is NOT the empty <c>catch (IOException)</c> this change removes
    /// everywhere else: that swallowed an unknown failure silently, whereas this names the file that
    /// wins, the evidence that the pool clear is correct, and the bead that owns it.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();

        ConfigDirectoryTeardown.TryDelete(_configDirectory);
    }
}
