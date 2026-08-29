using System.Reflection;

// Arbitarr.Host is the explicit composition root: the only project permitted to
// reference source-adapter and other outer-layer projects (AC6). Currently minimal —
// other steps extend DI wiring and config binding here.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var version = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "dev";

app.MapGet("/health", () => Results.Json(new
{
    status = "ok",
    name = "Arbitarr",
    version,
}));

app.Run();
