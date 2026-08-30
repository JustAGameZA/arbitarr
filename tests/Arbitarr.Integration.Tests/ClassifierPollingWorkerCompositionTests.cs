using Arbitarr.Ai;
using Arbitarr.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// The classifier must actually run in the composed Host, not merely be registered
/// as a scoped service nobody resolves. Checks the real <c>Program.cs</c> composition root (AC6)
/// rather than a source-level grep.
/// </summary>
public sealed class ClassifierPollingWorkerCompositionTests : IClassFixture<ArbitarrWebApplicationFactory>
{
    private readonly ArbitarrWebApplicationFactory _factory;

    public ClassifierPollingWorkerCompositionTests(ArbitarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Host_registers_ClassifierPollingWorker_as_a_hosted_service()
    {
        var hostedServices = _factory.Services.GetServices<IHostedService>();

        Assert.Single(hostedServices.OfType<ClassifierPollingWorker>());
    }

    [Fact]
    public void Host_can_resolve_the_scoped_ClassifierWorker_the_polling_worker_depends_on()
    {
        using var scope = _factory.Services.CreateScope();

        var worker = scope.ServiceProvider.GetRequiredService<ClassifierWorker>();

        Assert.NotNull(worker);
    }
}
