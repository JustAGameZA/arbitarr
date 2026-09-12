using Arbitarr.Core.Media;
using Arbitarr.Core.Settings;
using Arbitarr.Data.Media;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// arb-6l9b.1: <see cref="RadarrInstanceRepository"/>'s storage contract, and the one place it
/// deliberately DIVERGES from <see cref="ArrInstanceRepository"/> — the epoch.
/// </summary>
/// <remarks>
/// <para><b>THE NO-BUMP TEST IS THE REASON THIS FILE EXISTS.</b> Everything else here mirrors the
/// Sonarr repository's contract, but "a Radarr write does not bump <c>IArrInstanceEpoch</c>" is a
/// decision (arb-arrq, settled 2026-09-12) that a later "tidy-up" unifying the two repositories would
/// silently reverse, evicting <c>SeriesTitleResolver</c>'s memo on every unrelated Radarr write. An
/// absence is exactly the kind of claim that rots unasserted, so it is pinned with a positive control
/// proving the epoch under test CAN move.</para>
/// </remarks>
public sealed class RadarrInstanceRepositoryTests : IDisposable
{
    private const string BaseUrl = "http://radarr.example:7878/";
    private const string ApiKey = "placeholder-radarr-key-0123456789";

    private readonly SqliteTestDatabase _database = new("arbitarr-radarr-instance");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    /// <summary>
    /// THE DECISION, ASSERTED: a Radarr write leaves the *arr instance epoch exactly where it was.
    /// That epoch is Sonarr's — it exists to evict <c>SeriesTitleResolver</c>'s memo — and Radarr has
    /// no cache keyed on it, so bumping it here would throw away a Sonarr cache that is still
    /// entirely correct for a write that has nothing to do with it.
    ///
    /// <para><b>POSITIVE CONTROL:</b> the very same epoch instance is first shown to ADVANCE for a
    /// Sonarr write against the same database. Without that, "the epoch did not change" would pass
    /// just as happily against an epoch that never changes for anything, which is precisely the
    /// vacuous shape CLAUDE.md §4 warns about — and it is the whole assertion here, since the claim
    /// is an absence.</para>
    /// </summary>
    [Fact]
    public async Task A_radarr_write_does_not_bump_the_sonarr_instance_epoch()
    {
        await using var context = CreateContext();
        var epoch = new ArrInstanceEpoch();

        // POSITIVE CONTROL: this epoch does move, for the writer it belongs to.
        var sonarr = new ArrInstanceRepository(context, timeProvider: null, epoch);
        await sonarr.SetAsync("http://sonarr.example:8989/", "placeholder-sonarr-key-0123456789", CancellationToken.None);
        var afterSonarrWrite = epoch.Current;
        Assert.NotEqual(0, afterSonarrWrite);

        // ...therefore this absence is a real one: the Radarr repository does not take the epoch at
        // all, and a write through it cannot move the counter.
        var radarr = new RadarrInstanceRepository(context);
        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal(afterSonarrWrite, epoch.Current);
    }

    /// <summary>
    /// The two instances are separate singletons sharing one Settings table, so a write to one must
    /// not disturb the other's rows. Asserted PER ROW and with distinct values on each side, so a
    /// cross-write would be visible rather than hidden behind a matching value.
    /// </summary>
    [Fact]
    public async Task The_two_instances_store_under_separate_rows()
    {
        await using var context = CreateContext();

        var sonarr = new ArrInstanceRepository(context);
        var radarr = new RadarrInstanceRepository(context);

        await sonarr.SetAsync("http://sonarr.example:8989/", "placeholder-sonarr-key-0123456789", CancellationToken.None);
        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        Assert.Equal("http://sonarr.example:8989/", await sonarr.GetBaseUrlAsync(CancellationToken.None));
        Assert.Equal(BaseUrl, await radarr.GetBaseUrlAsync(CancellationToken.None));

        // The row NAMES are distinct too, which is what makes the separation structural rather than
        // incidental — and both are colon-namespaced, the mechanism that keeps them off the settings
        // catalog projection (CLAUDE.md §1).
        Assert.NotEqual(
            ArrInstanceRepository.SonarrApiKeySettingName,
            RadarrInstanceRepository.RadarrApiKeySettingName);
        Assert.Contains(':', RadarrInstanceRepository.RadarrApiKeySettingName);
        Assert.Contains(':', RadarrInstanceRepository.RadarrBaseUrlSettingName);
    }

    /// <summary>
    /// Clearing Radarr removes both of ITS rows and none of Sonarr's, asserted per row with the
    /// Sonarr rows shown present beforehand.
    /// </summary>
    [Fact]
    public async Task Clearing_radarr_leaves_the_sonarr_rows_intact()
    {
        await using var context = CreateContext();

        var sonarr = new ArrInstanceRepository(context);
        var radarr = new RadarrInstanceRepository(context);

        await sonarr.SetAsync("http://sonarr.example:8989/", "placeholder-sonarr-key-0123456789", CancellationToken.None);
        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        // POSITIVE CONTROLS: everything is present before the clear.
        Assert.True(await sonarr.HasApiKeyAsync(CancellationToken.None));
        Assert.True(await radarr.HasApiKeyAsync(CancellationToken.None));

        Assert.True(await radarr.ClearAsync(CancellationToken.None));

        Assert.False(await radarr.HasApiKeyAsync(CancellationToken.None));
        Assert.Null(await radarr.GetBaseUrlAsync(CancellationToken.None));

        Assert.True(await sonarr.HasApiKeyAsync(CancellationToken.None));
        Assert.Equal("http://sonarr.example:8989/", await sonarr.GetBaseUrlAsync(CancellationToken.None));
    }

    /// <summary>
    /// Returns false when nothing was stored, so a caller can report truthfully rather than claim a
    /// delete that did not happen.
    /// </summary>
    [Fact]
    public async Task Clearing_an_unconfigured_instance_reports_that_nothing_was_removed()
    {
        await using var context = CreateContext();
        var radarr = new RadarrInstanceRepository(context);

        Assert.False(await radarr.ClearAsync(CancellationToken.None));

        // POSITIVE CONTROL: the same call DOES report true once there is something to remove, so the
        // false above is about an empty store rather than a method that always returns false.
        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        Assert.True(await radarr.ClearAsync(CancellationToken.None));
    }

    /// <summary>
    /// A null key leaves the stored value alone — the source-API-key contract. Asserted through the
    /// single reader, which is the only way the value is legitimately observable.
    /// </summary>
    [Fact]
    public async Task A_null_key_on_a_later_write_leaves_the_stored_key_alone()
    {
        await using var context = CreateContext();
        var radarr = new RadarrInstanceRepository(context);

        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);
        await radarr.SetAsync("http://radarr-moved.example:7878/", apiKey: null, CancellationToken.None);

        Assert.Equal("http://radarr-moved.example:7878/", await radarr.GetBaseUrlAsync(CancellationToken.None));
        Assert.True(await radarr.HasApiKeyAsync(CancellationToken.None));
        Assert.Equal(ApiKey, await radarr.ReadApiKeyForUpstreamRequestAsync(CancellationToken.None));
    }

    /// <summary>
    /// A rejected key must not leave the address written: reject-never-clamp is also
    /// reject-never-partially-apply, and both values are validated before either is written.
    /// </summary>
    [Fact]
    public async Task A_rejected_key_does_not_write_the_address()
    {
        await using var context = CreateContext();
        var radarr = new RadarrInstanceRepository(context);

        await radarr.SetAsync(BaseUrl, ApiKey, CancellationToken.None);

        await Assert.ThrowsAsync<ArrInstanceValidationException>(
            () => radarr.SetAsync("http://radarr-rejected.example:7878/", "   ", CancellationToken.None));

        Assert.Equal(BaseUrl, await radarr.GetBaseUrlAsync(CancellationToken.None));
    }

    /// <summary>
    /// THE USERINFO REJECTION, which is what lets the base URL be served back freely and logged. A
    /// URL carrying a credential would smuggle a secret into a value this type documents as
    /// non-secret, and <c>IHttpClientFactory</c> logs every PATH segment of the absolute URI at
    /// Information — only the query string is collapsed.
    /// </summary>
    [Theory]
    [InlineData("http://operator:placeholder-radarr-key-0123456789@radarr.example:7878/")]
    [InlineData("https://operator:placeholder-radarr-key-0123456789@radarr.example/")]
    [InlineData("http://operator@radarr.example:7878/")]
    public void A_base_url_carrying_userinfo_is_rejected(string baseUrl) =>
        Assert.Throws<ArrInstanceValidationException>(() => RadarrInstanceRepository.ValidateBaseUrl(baseUrl));

    /// <summary>
    /// The positive control for the theory above: addresses WITHOUT userinfo are accepted, so the
    /// rejections are about the credential rather than about a validator that rejects everything.
    /// </summary>
    [Theory]
    [InlineData("http://radarr.example:7878/")]
    [InlineData("https://radarr.example/")]
    [InlineData("http://192.0.2.90:7878")]
    public void A_base_url_without_userinfo_is_accepted(string baseUrl) =>
        RadarrInstanceRepository.ValidateBaseUrl(baseUrl);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://radarr.example:7878/")]
    [InlineData("/api/v3/movie")]
    public void A_malformed_base_url_is_rejected(string baseUrl) =>
        Assert.Throws<ArrInstanceValidationException>(() => RadarrInstanceRepository.ValidateBaseUrl(baseUrl));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_api_key_is_rejected(string apiKey) =>
        Assert.Throws<ArrInstanceValidationException>(() => RadarrInstanceRepository.ValidateApiKey(apiKey));

    /// <summary>
    /// THE MECHANISM ITSELF (CLAUDE.md §1), ASSERTED STRUCTURALLY RATHER THAN THROUGH A RESPONSE.
    /// The Radarr rows can never surface on <c>GET /api/admin/settings</c> because that route projects
    /// from <see cref="SettingsCatalog.Entries"/> and no <see cref="SettingKey"/> value can produce a
    /// colon-namespaced name. <c>AdminRadarrEndpointsTests</c> pins the consequence end to end; this
    /// pins the cause, so a change that adds a <c>SettingKey</c> able to name either row fails here
    /// with the reason rather than three layers away with a symptom.
    /// </summary>
    [Fact]
    public void No_setting_key_can_name_either_radarr_row()
    {
        var keyNames = Enum.GetNames<SettingKey>();

        // POSITIVE CONTROL: the enum really does have members and this comparison really does match
        // when the names agree — without it, an empty enum would satisfy every absence below.
        Assert.NotEmpty(keyNames);
        Assert.Contains(SettingKey.FreshUntil.ToString(), keyNames, StringComparer.Ordinal);

        Assert.DoesNotContain(
            RadarrInstanceRepository.RadarrApiKeySettingName,
            keyNames,
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            RadarrInstanceRepository.RadarrBaseUrlSettingName,
            keyNames,
            StringComparer.OrdinalIgnoreCase);

        // And neither row is in the catalog projection itself, which is the collection the settings
        // route actually reads.
        Assert.DoesNotContain(
            SettingsCatalog.Entries,
            entry => string.Equals(
                entry.Key.ToString(),
                RadarrInstanceRepository.RadarrApiKeySettingName,
                StringComparison.OrdinalIgnoreCase));
    }
}
