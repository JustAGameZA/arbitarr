using System.Net;
using Arbitarr.Api.Admin;
using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// #56 / security review: RESTORE IS DELIBERATELY EXCLUDED FROM #43's BOOTSTRAP BYPASS.
///
/// <para><b>Why this lives here and not in the integration suite.</b> The dangerous case is a
/// request from a TRUSTED (RFC1918 or loopback) peer while no admin key is configured — that is
/// exactly when <c>AdminApiKeyFilter</c> lets the request through to the handler, so the handler's
/// own refusal is the only thing standing in front of it. <c>WebApplicationFactory</c> cannot
/// reproduce that: its in-memory TestServer leaves <c>RemoteIpAddress</c> null,
/// <c>IsTrustedNetwork</c> treats null as untrusted, and the filter answers 503 before the handler
/// is reached. An integration test therefore passes whether or not this guard exists — verified by
/// mutation — which is precisely the vacuous shape CLAUDE.md §4 warns about. Calling the handler
/// directly is what makes the assertion bite.</para>
///
/// <para><b>The attack.</b> <c>multipart/form-data</c> is a CORS-simple content type, so a page on
/// any site a LAN user visits can auto-submit this route cross-origin — no preflight, no key — and
/// replace the configuration database and the release-GUID secret with an archive of the
/// attacker's choosing. Every other bootstrap-reachable route sets one value; this one replaces
/// every credential the instance holds, which no fresh install needs to do before its key is set.
/// </para>
/// </summary>
public sealed class RestoreBootstrapRefusalTests
{
    [Fact]
    public async Task Restore_is_refused_while_no_admin_key_is_configured()
    {
        var context = new DefaultHttpContext
        {
            // Results.Problem writes through the DI-resolved problem-details service, so the
            // context needs a provider before the result can be executed.
            RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Post;
        // A multipart body, as the cross-origin form post would send.
        context.Request.ContentType = "multipart/form-data; boundary=----test";

        var result = await AdminBackupEndpoints.RestoreAsync(
            context,
            new StubResolver(AdminKeyResolutionOutcome.NotConfigured),
            restoreService: null!,
            coordinator: null!,
            state: null!,
            dbContext: null!,
            timeProvider: TimeProvider.System,
            cancellationToken: CancellationToken.None);

        // The nulls above are load-bearing: reaching ANY of those dependencies would throw. Passing
        // means the refusal happened before the form was read and before a restore was attempted,
        // which is the ordering the fix is about.
        await result.ExecuteAsync(context);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, context.Response.StatusCode);
    }

    /// <summary>
    /// POSITIVE CONTROL: an AUTHORIZED resolution does not take the refusal path. It fails later —
    /// on the null dependencies — which is the proof that the bootstrap check let it past rather
    /// than the handler refusing everything.
    /// </summary>
    [Fact]
    public async Task An_authorized_caller_is_not_refused_by_the_bootstrap_check()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "multipart/form-data; boundary=----test";

        await Assert.ThrowsAnyAsync<Exception>(() => AdminBackupEndpoints.RestoreAsync(
            context,
            new StubResolver(AdminKeyResolutionOutcome.Authorized),
            restoreService: null!,
            coordinator: null!,
            state: null!,
            dbContext: null!,
            timeProvider: TimeProvider.System,
            cancellationToken: CancellationToken.None));
    }

    private sealed class StubResolver(AdminKeyResolutionOutcome outcome) : IAdminKeyResolver
    {
        public Task<AdminKeyResolution> ResolveAsync(
            string? presentedKey,
            ApiKeyScope requiredScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AdminKeyResolution(outcome, null, null, null));
    }
}
