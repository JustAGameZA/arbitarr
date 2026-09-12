using System.Net;
using Arbitarr.Data;
using Arbitarr.Integration.Tests.TestSupport;
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

    /// <summary>
    /// Defaults OFF in tests: nearly every test on this factory asserts the login/key semantics that
    /// LAN passthrough would override for a local peer, so an on-by-default here would silently flip
    /// their meaning. <see cref="LanPassthroughTests"/> switches it on per case.
    /// </summary>
    private readonly bool _lanPassthrough;

    public RemoteAddressWebApplicationFactory(IPAddress remoteAddress, bool lanPassthrough = false)
    {
        _remoteAddress = remoteAddress;
        _lanPassthrough = lanPassthrough;
    }

    /// <summary>The per-instance <c>/config</c> directory this host was given.</summary>
    public string ConfigDirectory => _configDirectory;

    /// <summary>
    /// The exception that defeated the final delete attempt, or null when the directory went away.
    ///
    /// <para>KEEP IN STEP WITH <see cref="ArbitarrWebApplicationFactory"/>, which carries the full
    /// rationale. Since arb-dhua this is expected to be NULL and is asserted on by
    /// <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/>; the failure is still tolerated at
    /// disposal time rather than thrown, because a throw there would fault whichever unrelated test
    /// is in flight.</para>
    /// </summary>
    public Exception? LastDeleteFailure { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_configDirectory);

        // UseSetting, NEVER Environment.SetEnvironmentVariable — see the same note on
        // ArbitarrWebApplicationFactory. The env var is process-wide and races every other host in
        // the process; this setting belongs to this builder alone.
        builder.UseSetting("Arbitarr:ConfigDir", _configDirectory);

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new RemoteAddressStartupFilter(_remoteAddress));

            // Registered after Program.cs so it wins; per-host, never via the environment variable
            // (process-wide, races other hosts in the process — same reason as ConfigDir above).
            services.AddSingleton(new Arbitarr.Api.Security.LanPassthroughOptions { Enabled = _lanPassthrough });
        });
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

    /// <summary>
    /// arb-rwhb: stops and awaits the host BEFORE deleting the config directory, so no background
    /// work is still reading the files being removed.
    ///
    /// <para>KEEP IN STEP WITH <see cref="ArbitarrWebApplicationFactory"/>, which carries the full
    /// rationale and the identical pair of overrides. This factory hosts the same composition root,
    /// so it inherits the same immediate startup backup from <c>MaintenanceHostedService</c> and had
    /// the same race; a fix applied to only one of the two leaves it live in the other.</para>
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync().ConfigureAwait(false);

        DeleteConfigDirectory();
    }

    /// <inheritdoc cref="DisposeAsync" />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            DeleteConfigDirectory();
        }
    }

    /// <summary>
    /// Removes the per-instance config directory. This SUCCEEDS since arb-dhua and is asserted on
    /// by <see cref="ConfigDirectoryIsDeletedOnDisposalTests"/>. Mirrors
    /// <see cref="ArbitarrWebApplicationFactory"/>, which carries the full reasoning.
    /// </summary>
    private void DeleteConfigDirectory()
    {
        // Closes the pooled handles on this instance's databases and then deletes. That is HALF of
        // what the delete needs: the other half is that a disposed ArbitarrDbContext actually
        // returns its connection to the pool, which it did not do until
        // ArbitarrDbContextOptionsFactory.Create was given ownership of the connection it opens
        // (arb-dhua -- the pool inventory was never the problem). Never ClearAllPools: banned from
        // test IL, and process-global (arb-cbc/arb-5ba). ConfigDirectoryTeardown carries the full
        // account; sharing the one implementation with the other factory is what the KEEP IN STEP
        // note above used to ask of a reader and now no longer has to (arb-gphi).
        //
        // TryDelete rather than Delete because this runs from Dispose -- see ConfigDirectoryTeardown.
        LastDeleteFailure = ConfigDirectoryTeardown.TryDelete(_configDirectory);
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
