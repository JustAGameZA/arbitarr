using Arbitarr.Data.Media;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-6l9b.1: <see cref="RadarrCredentialProvider"/> is the SINGLE production reader of the stored
/// Radarr API key, and it returns null for the half-configured states rather than a credential with a
/// missing half.
/// </summary>
/// <remarks>
/// The half-configured cases are the substance. "No address" and "address but no key" are both
/// reported as null because every consumer wants the same thing from them: the probe would send an
/// unauthenticated request that Radarr answers 401 — recording a failure against a service that is
/// not broken — and a library reader would render an error where "not configured" is the truthful
/// answer. Asserting them here means a consumer cannot be handed a <see cref="RadarrCredential"/>
/// carrying an empty key.
/// </remarks>
public sealed class RadarrCredentialProviderTests : IDisposable
{
    private const string BaseUrl = "http://radarr.example:7878/";
    private const string ApiKey = "placeholder-radarr-key-7b0c25ea";

    private readonly SqliteTestDatabase _database = new("arbitarr-radarr-credential");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    [Fact]
    public async Task A_fully_configured_instance_yields_a_credential()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(new Uri(BaseUrl), credential!.BaseUrl);
        Assert.Equal(ApiKey, credential.ApiKey);
    }

    [Fact]
    public async Task An_unconfigured_instance_yields_null()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        var provider = new RadarrCredentialProvider(repository);

        Assert.Null(await provider.GetAsync(CancellationToken.None));

        // POSITIVE CONTROL: the same provider against the same store DOES produce a credential once
        // one is configured, so the null above is an unconfigured instance rather than a provider
        // that never returns anything.
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        Assert.NotNull(await provider.GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// THE HALF-CONFIGURED STATE: an address with no key is null, not a credential with an empty key.
    /// The positive control is the address itself — it really IS stored — so the null is about the
    /// missing key rather than about an instance that was never configured at all.
    /// </summary>
    [Fact]
    public async Task An_address_with_no_key_yields_null_rather_than_an_empty_credential()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        await repository.SetAsync(BaseUrl, apiKey: null, CancellationToken.None);

        // POSITIVE CONTROL: the address is stored and the key is not.
        Assert.Equal(BaseUrl, await repository.GetBaseUrlAsync(CancellationToken.None));
        Assert.False(await repository.HasApiKeyAsync(CancellationToken.None));

        Assert.Null(await new RadarrCredentialProvider(repository).GetAsync(CancellationToken.None));
    }

    /// <summary>
    /// The provider reads PER CALL rather than capturing at construction, so a key configured after
    /// the provider was built is seen. A value captured once would be absent forever for anyone who
    /// configures Radarr after boot — which is everyone, on a first run.
    /// </summary>
    [Fact]
    public async Task The_credential_is_read_per_call_so_a_later_write_is_seen()
    {
        await using var context = CreateContext();
        var repository = new RadarrInstanceRepository(context);
        var provider = new RadarrCredentialProvider(repository);

        Assert.Null(await provider.GetAsync(CancellationToken.None));

        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        var credential = await provider.GetAsync(CancellationToken.None);
        Assert.NotNull(credential);
        Assert.Equal(ApiKey, credential!.ApiKey);

        // And a repoint is seen too, through the same provider instance.
        await repository.SetAsync("http://radarr-moved.example:7878/", ApiKey, CancellationToken.None);
        var repointed = await provider.GetAsync(CancellationToken.None);
        Assert.NotNull(repointed);
        Assert.Equal(new Uri("http://radarr-moved.example:7878/"), repointed!.BaseUrl);
    }
}
