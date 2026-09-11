using Arbitarr.Core.Media;
using Arbitarr.Data.Media;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-iiy: <see cref="ArrInstanceRepository.SetAsync"/> advances the *arr instance epoch, which is
/// what makes a repoint invalidate <c>SeriesTitleResolver</c>'s tvdbid-to-title memo without that
/// resolver having to read the settings on its hit path.
/// </summary>
/// <remarks>
/// The pairing is the point. "The epoch advanced" on its own would be satisfied by an implementation
/// that bumped on EVERY call — including the ones that wrote nothing — which would throw away a
/// still-correct cache whenever an operator submitted a malformed address. So a successful write and
/// a rejected one are asserted together: one must move the counter and the other must leave it
/// exactly where it was.
/// </remarks>
public sealed class ArrInstanceEpochBumpTests : IDisposable
{
    private const string BaseUrl = "http://192.0.2.31:8989/";
    private const string ApiKey = "placeholder-sonarr-key-0123456789";

    private readonly SqliteTestDatabase _database = new("arbitarr-arr-instance-epoch");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static (ArrInstanceRepository Repository, ArrInstanceEpoch Epoch) Build(ArbitarrDbContext context)
    {
        var epoch = new ArrInstanceEpoch();
        return (new ArrInstanceRepository(context, timeProvider: null, epoch), epoch);
    }

    [Fact]
    public async Task A_successful_write_bumps_the_epoch_exactly_once()
    {
        await using var context = CreateContext();
        var (repository, epoch) = Build(context);

        var before = epoch.Current;
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        // Exactly once, not merely "changed": a repository that bumped per written ROW would move
        // it twice here (address and key), and a cache key that skips two epochs is not wrong but
        // says the invalidation is not the deliberate one-per-write signal it is documented to be.
        Assert.Equal(before + 1, epoch.Current);
    }

    [Fact]
    public async Task A_repoint_bumps_the_epoch_again()
    {
        await using var context = CreateContext();
        var (repository, epoch) = Build(context);

        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        var afterFirst = epoch.Current;

        await repository.SetAsync("http://192.0.2.32:8989/", ApiKey, CancellationToken.None);

        Assert.Equal(afterFirst + 1, epoch.Current);
    }

    [Fact]
    public async Task A_rejected_base_url_leaves_the_epoch_alone()
    {
        await using var context = CreateContext();
        var (repository, epoch) = Build(context);

        // The positive control that this class's assertions can move at all: the same repository and
        // the same epoch DO advance for a write that is accepted. Without it "the epoch did not
        // change" would also pass against an epoch that never changes for anything.
        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        var afterAccepted = epoch.Current;
        Assert.NotEqual(0, afterAccepted);

        await Assert.ThrowsAsync<ArrInstanceValidationException>(
            () => repository.SetAsync("not-a-url", ApiKey, CancellationToken.None));

        Assert.Equal(afterAccepted, epoch.Current);
    }

    [Fact]
    public async Task A_rejected_api_key_leaves_the_epoch_alone()
    {
        await using var context = CreateContext();
        var (repository, epoch) = Build(context);

        await repository.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        var afterAccepted = epoch.Current;
        Assert.NotEqual(0, afterAccepted);

        await Assert.ThrowsAsync<ArrInstanceValidationException>(
            () => repository.SetAsync(BaseUrl, "   ", CancellationToken.None));

        Assert.Equal(afterAccepted, epoch.Current);
    }
}
