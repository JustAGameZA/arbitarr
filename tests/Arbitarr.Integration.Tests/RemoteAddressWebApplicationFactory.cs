using System.Net;
using Arbitarr.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Arbitarr.Integration.Tests;

/// <summary>
/// #43: hosts the real Host composition root like <see cref="ArbitarrWebApplicationFactory"/> does,
/// but additionally stamps a chosen <see cref="ConnectionInfo.RemoteIpAddress"/> on every request,
/// so a test can pin which side of <c>AdminApiKeyFilter</c>'s local-network bootstrap bypass it is
/// exercising.
///
/// WHY THIS IS NEEDED. The in-memory test transport leaves <c>RemoteIpAddress</c> null (verified by
/// probe, not assumed), and the filter treats null as untrusted. So an un-stamped test host only
/// ever exercises the fail-closed side: it can never demonstrate that the bypass admits a local
/// caller, nor that it refuses a remote one. Both directions need a real address on the connection.
///
/// It is stamped via an <see cref="IStartupFilter"/> so the middleware WRAPS the real Host pipeline
/// rather than replacing it (a plain <c>builder.Configure</c> would discard every route the Host
/// registers, and the enumeration sweep would then have nothing to walk). The address goes onto the
/// connection itself, never a header: the filter deliberately ignores headers, so a header-based
/// stand-in would prove nothing about the behaviour under test.
///
/// A sibling of <see cref="ArbitarrWebApplicationFactory"/> rather than a subclass, because that
/// type is sealed and is shared by every other integration test — widening it for one test's needs
/// would be a larger change than this one deserves.
/// </summary>
public sealed class RemoteAddressWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _configDirectory =
        Path.Combine(Path.GetTempPath(), "arbitarr-remote-address-tests", Guid.NewGuid().ToString("N"));

    private readonly IPAddress _remoteAddress;

    public RemoteAddressWebApplicationFactory(IPAddress remoteAddress)
    {
        _remoteAddress = remoteAddress;
    }

    /// <summary>The per-instance <c>/config</c> directory this host was given.</summary>
    public string ConfigDirectory => _configDirectory;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_configDirectory);

        // UseSetting, NEVER Environment.SetEnvironmentVariable — see the same note on
        // ArbitarrWebApplicationFactory. The env var is process-wide and races every other host in
        // the process; this setting belongs to this builder alone.
        builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);

        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(_remoteAddress)));
    }

    /// <summary>Runs <paramref name="seed"/> against a fresh scoped <see cref="ArbitarrDbContext"/> and saves changes.</summary>
    public async Task SeedAsync(Func<ArbitarrDbContext, Task> seed)
    {
        using var client = CreateClient();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArbitarrDbContext>();
        await seed(dbContext);
        await dbContext.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_configDirectory))
                {
                    Directory.Delete(_configDirectory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked SQLite file on Windows shouldn't fail the test run.
            }
        }
    }

    private sealed class RemoteAddressStartupFilter : IStartupFilter
    {
        private readonly IPAddress _remoteAddress;

        public RemoteAddressStartupFilter(IPAddress remoteAddress)
        {
            _remoteAddress = remoteAddress;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = _remoteAddress;
                await nextMiddleware();
            });

            next(app);
        };
    }
}
