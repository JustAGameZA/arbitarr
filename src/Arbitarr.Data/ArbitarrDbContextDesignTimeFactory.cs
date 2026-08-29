using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ArrSearcher.Data;

/// <summary>
/// Design-time factory so <c>dotnet ef migrations add</c> can construct the context without a
/// running host. Not used at runtime — the real connection string/pragma configuration is
/// supplied via DI by the composition root (Host), per Step 2's scope boundary.
/// </summary>
public sealed class ArrSearcherDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ArrSearcherDbContext>
{
    public ArrSearcherDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArrSearcherDbContext>();
        optionsBuilder.UseSqlite("Data Source=design-time.db");
        return new ArrSearcherDbContext(optionsBuilder.Options);
    }
}
