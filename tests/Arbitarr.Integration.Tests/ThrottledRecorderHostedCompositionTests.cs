using Arbitarr.Core.Security;
using Arbitarr.Host.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// The shutdown drain (arb-acy9) only works if the hosted service and the recorder the request path
/// calls are the SAME object — the in-flight set lives on the instance, so a second instance drains
/// a set nothing ever wrote to. <c>Program.cs</c> gets that by registering the resolved singleton
/// (<c>AddHostedService(sp =&gt; (T)sp.GetRequiredService&lt;I&gt;())</c>), and the mutant is the
/// obvious tidy-up to <c>AddHostedService&lt;T&gt;()</c>, which reads identically and mints a second
/// instance whose <c>StopAsync</c> waits on nothing while the real one's writes are abandoned.
///
/// <para><b><c>Assert.Same</c> is the load-bearing assertion here.</b> The <c>Assert.Single</c>
/// before it is only how the one hosted instance is obtained: under the mutant there is still exactly one of each recorder in
/// the hosted collection, so <c>Single</c> alone passes against the bug. Confirmed by mutation —
/// flipping the key recorder's registration to <c>AddHostedService&lt;ThrottledApiKeyLastUsedRecorder&gt;()</c>
/// leaves <c>Single</c> green and fails <c>Same</c>. Do not "simplify" this to a registration check.</para>
///
/// <para>Checks the real composition root through
/// <see cref="ArbitarrWebApplicationFactory"/> rather than a source-level grep, for the reason
/// <see cref="ClassifierPollingWorkerCompositionTests"/> does.</para>
/// </summary>
public sealed class ThrottledRecorderHostedCompositionTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public ThrottledRecorderHostedCompositionTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void The_hosted_api_key_recorder_is_the_same_instance_the_request_path_uses()
    {
        var requestPathRecorder = _factory.Services.GetRequiredService<IApiKeyLastUsedRecorder>();

        var hosted = Assert.Single(
            _factory.Services.GetServices<IHostedService>().OfType<ThrottledApiKeyLastUsedRecorder>());

        Assert.Same(requestPathRecorder, hosted);
    }

    [Fact]
    public void The_hosted_session_activity_recorder_is_the_same_instance_the_request_path_uses()
    {
        var requestPathRecorder = _factory.Services.GetRequiredService<ISessionActivityRecorder>();

        var hosted = Assert.Single(
            _factory.Services.GetServices<IHostedService>().OfType<ThrottledSessionActivityRecorder>());

        Assert.Same(requestPathRecorder, hosted);
    }
}
