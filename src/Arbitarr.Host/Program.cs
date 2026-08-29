using Microsoft.Extensions.Hosting;

// Arbitarr.Host is the explicit composition root: the only project permitted to
// reference source-adapter and other outer-layer projects (AC6). Currently minimal —
// other steps extend DI wiring and config binding here.
var builder = Host.CreateApplicationBuilder(args);

using var host = builder.Build();

await host.RunAsync();
