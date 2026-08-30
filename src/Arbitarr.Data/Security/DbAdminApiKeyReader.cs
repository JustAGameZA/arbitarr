using Arbitarr.Core.Security;
using Arbitarr.Core.Settings;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Security;

/// <summary>
/// <see cref="IAdminApiKeyReader"/> backed by the <c>Settings</c> table (<see cref="SettingKey.AdminApiKey"/>
/// row), so the admin key can be rotated at runtime from the admin UI without a restart, unlike
/// the statically-configured Torznab/Newznab client keys.
/// </summary>
public sealed class DbAdminApiKeyReader : IAdminApiKeyReader
{
    private readonly ArbitarrDbContext _dbContext;

    public DbAdminApiKeyReader(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string?> GetCurrentKeyAsync(CancellationToken cancellationToken)
    {
        var row = await _dbContext.Settings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Name == SettingKey.AdminApiKey.ToString(), cancellationToken);

        return string.IsNullOrEmpty(row?.Value) ? null : row.Value;
    }
}
