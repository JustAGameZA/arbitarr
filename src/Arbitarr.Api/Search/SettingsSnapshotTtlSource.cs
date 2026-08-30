using Arbitarr.Data.Settings;

namespace Arbitarr.Api.Search;

/// <summary>
/// Live <see cref="ISnapshotTtlSource"/> implementation (M7-8c/AC24): reads
/// <see cref="Arbitarr.Core.Settings.SettingsSnapshot.QuerySnapshotTtl"/> via
/// <see cref="SettingsRepository"/> on every call, so a <c>QuerySnapshotTtl</c> setting changed
/// through the admin API takes effect on the very next <see cref="PaginationSnapshotService"/>
/// request without a restart. Registered scoped in <c>Program.cs</c>, sharing the same per-request
/// <c>ArbitarrDbContext</c>/<c>SettingsRepository</c> instance as the rest of the request pipeline.
/// </summary>
public sealed class SettingsSnapshotTtlSource(SettingsRepository settingsRepository) : ISnapshotTtlSource
{
    public async ValueTask<TimeSpan> GetAsync(CancellationToken cancellationToken)
    {
        var snapshot = await settingsRepository.LoadSnapshotAsync(cancellationToken);
        return snapshot.QuerySnapshotTtl;
    }
}
