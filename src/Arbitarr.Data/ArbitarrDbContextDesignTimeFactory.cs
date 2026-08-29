using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Arbitarr.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> can construct the context without a
/// running host. Not used at runtime — the real connection string/pragma configuration is
/// supplied via DI by the composition root (Host), per Step 2's scope boundary.
/// </summary>
public sealed class ArbitarrDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ArbitarrDbContext>
{
    public ArbitarrDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite("Data Source=design-time.db");
        return new ArbitarrDbContext(optionsBuilder.Options);
    }
}
