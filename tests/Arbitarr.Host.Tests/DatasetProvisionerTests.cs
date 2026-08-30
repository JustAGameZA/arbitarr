using Arbitarr.Host.Provisioning;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// M7-9: <see cref="DatasetProvisioner"/> only scaffolds the config-directory tree for a fresh
/// volume (AC21) — it must never pre-fetch or vendor datasets themselves (AC19), which stay the
/// responsibility of the runtime providers (e.g. <c>AnimeListsProvider</c>) that fetch on demand.
/// </summary>
public sealed class DatasetProvisionerTests : IDisposable
{
    private readonly string _configDirectory;

    public DatasetProvisionerTests()
    {
        _configDirectory = Path.Combine(Path.GetTempPath(), $"arr-searcher-provision-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDirectory))
        {
            Directory.Delete(_configDirectory, recursive: true);
        }
    }

    [Fact]
    public void EnsureProvisioned_creates_the_config_directory_when_absent()
    {
        DatasetProvisioner.EnsureProvisioned(_configDirectory);

        Assert.True(Directory.Exists(_configDirectory));
    }

    [Fact]
    public void EnsureProvisioned_creates_the_datasets_subdirectory()
    {
        DatasetProvisioner.EnsureProvisioned(_configDirectory);

        Assert.True(Directory.Exists(Path.Combine(_configDirectory, "datasets")));
    }

    [Fact]
    public void EnsureProvisioned_is_idempotent_on_an_already_provisioned_directory()
    {
        DatasetProvisioner.EnsureProvisioned(_configDirectory);
        DatasetProvisioner.EnsureProvisioned(_configDirectory);

        Assert.True(Directory.Exists(_configDirectory));
        Assert.True(Directory.Exists(Path.Combine(_configDirectory, "datasets")));
    }

    [Fact]
    public void EnsureProvisioned_does_not_fetch_or_write_any_dataset_files()
    {
        DatasetProvisioner.EnsureProvisioned(_configDirectory);

        Assert.Empty(Directory.GetFiles(_configDirectory, "*", SearchOption.AllDirectories));
    }
}
