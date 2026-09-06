using Arbitarr.Core.Settings;
using Arbitarr.Data.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// M7-5: proves <see cref="SettingsRepository"/> validates via <see cref="SettingsValidator"/>
/// before persisting (AC24 — reject out-of-bounds, never clamp; a rejected write leaves no row
/// behind), upserts rather than duplicates a row, and that the AC26b AI kill-switch round-trips.
/// </summary>
public sealed class SettingsRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public SettingsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"arr-searcher-settings-test-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task SetAsync_persists_a_valid_value()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        await repository.SetAsync(SettingKey.WorkerCycleInterval, TimeSpan.FromSeconds(30).ToString(), CancellationToken.None);

        var snapshot = await repository.LoadSnapshotAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(30), snapshot.WorkerCycleInterval);
    }

    [Fact]
    public async Task SetAsync_rejects_an_out_of_bounds_value_and_persists_nothing()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        // Floor is 15s (SettingsValidator.ValidateWorkerCycleInterval).
        var tooLow = TimeSpan.FromSeconds(1);

        await Assert.ThrowsAsync<SettingsValidationException>(
            () => repository.SetAsync(SettingKey.WorkerCycleInterval, tooLow.ToString(), CancellationToken.None));

        var snapshot = await repository.LoadSnapshotAsync(CancellationToken.None);
        Assert.NotEqual(tooLow, snapshot.WorkerCycleInterval);
    }

    [Fact]
    public async Task SetAsync_validates_cross_field_bounds_against_current_persisted_values()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        await repository.SetAsync(SettingKey.FreshUntil, TimeSpan.FromMinutes(10).ToString(), CancellationToken.None);

        // serve_until's floor is the *current* fresh_until (10m here), so 5m must be rejected.
        await Assert.ThrowsAsync<SettingsValidationException>(
            () => repository.SetAsync(SettingKey.ServeUntil, TimeSpan.FromMinutes(5).ToString(), CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_upserts_rather_than_duplicating_a_row()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        await repository.SetAsync(SettingKey.WorkerCycleInterval, TimeSpan.FromSeconds(20).ToString(), CancellationToken.None);
        await repository.SetAsync(SettingKey.WorkerCycleInterval, TimeSpan.FromSeconds(45).ToString(), CancellationToken.None);

        var rowCount = await context.Settings.CountAsync(e => e.Name == SettingKey.WorkerCycleInterval.ToString());
        Assert.Equal(1, rowCount);

        var snapshot = await repository.LoadSnapshotAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(45), snapshot.WorkerCycleInterval);
    }

    // #43: this class previously asserted that AdminApiKey was rejected outright by this write
    // path. That behaviour was the second half of a total-lockout deadlock (the key is readable
    // only from this table, and nothing else could write it), so the assertion is now that the key
    // is WRITABLE but VALIDATED — the property the old throw was standing in for. The key still
    // never reaches the catalog-driven settings surface; AdminSettingsEndpointsTests pins that.

    [Fact]
    public async Task AdminApiKey_is_persisted_when_it_satisfies_the_validator()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        // Exactly at the 16-character floor SettingsValidator.ValidateAdminApiKey enforces.
        const string key = "0123456789abcdef";

        await repository.SetAsync(SettingKey.AdminApiKey, key, CancellationToken.None);

        var row = await context.Settings.FindAsync(SettingKey.AdminApiKey.ToString());
        Assert.NotNull(row);
        Assert.Equal(key, row!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // 15 characters — one short of the floor.
    [InlineData("0123456789abcde")]
    public async Task AdminApiKey_is_rejected_and_persists_nothing_when_it_fails_the_validator(string proposed)
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        await Assert.ThrowsAsync<SettingsValidationException>(
            () => repository.SetAsync(SettingKey.AdminApiKey, proposed, CancellationToken.None));

        // AC24: a rejected write leaves no row behind, so a failed attempt cannot half-configure
        // the gate into a state no operator can satisfy.
        Assert.Null(await context.Settings.FindAsync(SettingKey.AdminApiKey.ToString()));
    }

    [Fact]
    public async Task AiKillSwitch_defaults_to_off_and_round_trips_when_set()
    {
        using var context = CreateContext();
        var repository = new SettingsRepository(context, arrSyncInterval: TimeSpan.FromMinutes(15));

        Assert.False(await repository.GetAiKillSwitchAsync(CancellationToken.None));

        await repository.SetAsync(SettingKey.AiKillSwitch, bool.TrueString, CancellationToken.None);

        Assert.True(await repository.GetAiKillSwitchAsync(CancellationToken.None));
    }
}
