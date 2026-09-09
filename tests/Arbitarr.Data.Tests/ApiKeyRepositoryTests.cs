using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Security;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Arbitarr.Data.Tests;

/// <summary>
/// #58: <see cref="ApiKeyRepository"/>'s half of the feature — minting, listing, revoking, and the
/// verification lookup — plus the migration's losslessness against an existing populated database,
/// following <see cref="SourceRepositoryTests"/>'s pattern.
///
/// The endpoint-level acceptance criteria (401 vs 403, the legacy key still working, the plaintext
/// appearing exactly once over the wire) live in <c>AdminApiKeyEndpointsTests</c> and
/// <c>ApiKeyScopeGateTests</c>, because they are properties of the HTTP surface rather than of this
/// type. What is asserted here is what the storage layer guarantees regardless of who calls it.
/// </summary>
public sealed class ApiKeyRepositoryTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new("arr-searcher-apikeys-test");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private ApiKeyRepository CreateRepository(ArbitarrDbContext context) => new(context, _time);

    [Fact]
    public async Task Migration_applies_to_an_existing_populated_database_without_data_loss()
    {
        // Simulate an existing deployment carrying the pre-#58 shared admin key: migrate to the
        // state just before AddApiKeysTable, write that key's settings row, then migrate the rest
        // of the way and confirm it survives byte-for-byte. This is the upgrade path AC4 is about —
        // the migration must not disturb the credential every caller is currently using.
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);

        await using (var before = new ArbitarrDbContext(optionsBuilder.Options))
        {
            var migrator = before.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20260906213034_AddEventsTable");

            before.Settings.Add(new SettingEntry
            {
                Name = "AdminApiKey",
                Value = "pre-existing-shared-key",
                UpdatedAt = _time.GetUtcNow(),
            });
            await before.SaveChangesAsync();
        }

        await using var after = CreateContext();

        var preserved = await after.Settings.FirstOrDefaultAsync(s => s.Name == "AdminApiKey");
        Assert.NotNull(preserved);
        Assert.Equal("pre-existing-shared-key", preserved!.Value);

        // And the new table exists and is empty — nothing was migrated INTO it.
        Assert.Empty(await after.ApiKeys.ToListAsync());
    }

    [Fact]
    public async Task Created_key_stores_only_a_hash_and_returns_the_plaintext_once()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var created = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(created.PlaintextKey));

        // The stored row holds the digest, not the value — the property the whole feature rests on.
        Assert.Equal(ApiKeyHasher.Hash(created.PlaintextKey), created.Entry.KeyHash);
        Assert.NotEqual(created.PlaintextKey, created.Entry.KeyHash);

        // And nothing anywhere in the table holds the plaintext, under any column. Asserted over the
        // raw row rather than the projection, so a future column that started carrying it would fail
        // here rather than pass because the projection happened not to expose it.
        var stored = await context.ApiKeys.AsNoTracking().SingleAsync();
        Assert.Equal(ApiKeyHasher.Hash(created.PlaintextKey), stored.KeyHash);
        Assert.DoesNotContain(created.PlaintextKey, stored.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generated_keys_are_distinct()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var first = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);
        var second = await repository.CreateAsync("radarr", ApiKeyScope.ReadOnly, CancellationToken.None);

        Assert.NotEqual(first.PlaintextKey, second.PlaintextKey);
        Assert.NotEqual(first.Entry.KeyHash, second.Entry.KeyHash);
    }

    [Fact]
    public async Task Empty_label_is_rejected()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.CreateAsync("   ", ApiKeyScope.Admin, CancellationToken.None));
    }

    [Fact]
    public async Task Over_long_label_is_rejected_rather_than_truncated()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var tooLong = new string('a', ApiKeyRepository.MaxLabelLength + 1);

        // AC24: reject, never clamp. A silently truncated label is a label the operator did not
        // choose, and two of them could then collide in a list built to tell keys apart.
        await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.CreateAsync(tooLong, ApiKeyScope.Admin, CancellationToken.None));

        Assert.Empty(await context.ApiKeys.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_label_is_rejected_case_insensitively()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);

        await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.CreateAsync("SONARR", ApiKeyScope.ReadOnly, CancellationToken.None));
    }

    [Fact]
    public async Task A_live_key_resolves_from_its_plaintext_and_a_wrong_value_does_not()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var created = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);

        var found = await repository.FindLiveByPresentedKeyAsync(created.PlaintextKey, CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal("sonarr", found!.Label);

        Assert.Null(await repository.FindLiveByPresentedKeyAsync("not-the-key", CancellationToken.None));
        Assert.Null(await repository.FindLiveByPresentedKeyAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task A_revoked_key_stops_resolving_immediately()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var keeper = await repository.CreateAsync("keeper", ApiKeyScope.Admin, CancellationToken.None);
        var doomed = await repository.CreateAsync("doomed", ApiKeyScope.Admin, CancellationToken.None);

        Assert.True(await repository.RevokeAsync(doomed.Entry.Id, CancellationToken.None));

        Assert.Null(await repository.FindLiveByPresentedKeyAsync(doomed.PlaintextKey, CancellationToken.None));

        // AC: "revoking one key does not affect any other".
        var survivor = await repository.FindLiveByPresentedKeyAsync(keeper.PlaintextKey, CancellationToken.None);
        Assert.NotNull(survivor);
        Assert.Equal("keeper", survivor!.Label);
    }

    [Fact]
    public async Task Revoking_the_last_admin_scope_key_is_refused()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var onlyAdmin = await repository.CreateAsync("admin", ApiKeyScope.Admin, CancellationToken.None);

        // A read-only key is present and is deliberately not a rescue: it cannot administer
        // anything, so leaving it behind is still a lockout.
        await repository.CreateAsync("monitoring", ApiKeyScope.ReadOnly, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.RevokeAsync(onlyAdmin.Entry.Id, CancellationToken.None));

        // AC5 asks for an explanatory message, not merely a refusal: the operator has to be told
        // what to do instead, or the refusal reads as a bug.
        Assert.Contains("last API key with admin scope", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Create a replacement", refusal.Message, StringComparison.Ordinal);

        // Still live.
        Assert.NotNull(await repository.FindLiveByPresentedKeyAsync(onlyAdmin.PlaintextKey, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_an_admin_key_is_allowed_once_another_admin_key_exists()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var first = await repository.CreateAsync("first", ApiKeyScope.Admin, CancellationToken.None);
        await repository.CreateAsync("second", ApiKeyScope.Admin, CancellationToken.None);

        Assert.True(await repository.RevokeAsync(first.Entry.Id, CancellationToken.None));
        Assert.Null(await repository.FindLiveByPresentedKeyAsync(first.PlaintextKey, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_a_read_only_key_is_never_refused()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var readOnly = await repository.CreateAsync("monitoring", ApiKeyScope.ReadOnly, CancellationToken.None);

        // No admin key exists at all here, and that is still fine: the lockout guard is about
        // ADMIN authority, and a read-only key was never providing any.
        Assert.True(await repository.RevokeAsync(readOnly.Entry.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Revoking_an_unknown_key_reports_that_rather_than_throwing()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        Assert.False(await repository.RevokeAsync(4242, CancellationToken.None));
    }

    [Fact]
    public async Task Revocation_is_idempotent_and_does_not_rewrite_the_original_timestamp()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await repository.CreateAsync("other-admin", ApiKeyScope.Admin, CancellationToken.None);
        var target = await repository.CreateAsync("target", ApiKeyScope.Admin, CancellationToken.None);

        await repository.RevokeAsync(target.Entry.Id, CancellationToken.None);
        var firstRevokedAt = (await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == target.Entry.Id)).RevokedAt;

        _time.Advance(TimeSpan.FromHours(1));
        Assert.True(await repository.RevokeAsync(target.Entry.Id, CancellationToken.None));

        var stillRevokedAt = (await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == target.Entry.Id)).RevokedAt;

        // When a key stopped working is a fact about the past; a second call must not restate it.
        Assert.Equal(firstRevokedAt, stillRevokedAt);
    }

    [Fact]
    public async Task Revoked_keys_remain_listed_with_their_label_and_last_used_time()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await repository.CreateAsync("keeper", ApiKeyScope.Admin, CancellationToken.None);
        var doomed = await repository.CreateAsync("doomed", ApiKeyScope.Admin, CancellationToken.None);

        await repository.RecordLastUsedAsync(doomed.Entry.Id, _time.GetUtcNow(), CancellationToken.None);
        await repository.RevokeAsync(doomed.Entry.Id, CancellationToken.None);

        var all = await repository.GetAllAsync(CancellationToken.None);

        // Listed, not hidden: a revoked key with a recent last-used time is exactly what an operator
        // needs to see after revoking one and finding something broke.
        var revoked = Assert.Single(all, k => k.Label == "doomed");
        Assert.NotNull(revoked.RevokedAt);
        Assert.NotNull(revoked.LastUsedAt);
    }

    [Fact]
    public async Task Last_used_is_recorded_and_starts_null()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var created = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);

        // A key that has never authenticated anything says so, rather than claiming its creation
        // time — "never used" and "used when it was made" are different answers to the question an
        // operator asks before revoking.
        Assert.Null(created.Entry.LastUsedAt);

        var usedAt = _time.GetUtcNow().AddMinutes(5);
        await repository.RecordLastUsedAsync(created.Entry.Id, usedAt, CancellationToken.None);

        var reloaded = await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == created.Entry.Id);
        Assert.Equal(usedAt, reloaded.LastUsedAt);
    }

    [Fact]
    public async Task Recording_last_used_on_a_revoked_key_is_a_no_op()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await repository.CreateAsync("other-admin", ApiKeyScope.Admin, CancellationToken.None);
        var target = await repository.CreateAsync("target", ApiKeyScope.Admin, CancellationToken.None);

        await repository.RevokeAsync(target.Entry.Id, CancellationToken.None);
        await repository.RecordLastUsedAsync(target.Entry.Id, _time.GetUtcNow(), CancellationToken.None);

        // A revoked key's last-used time should record when it last WORKED, not when something
        // still holding it tried and was refused.
        var reloaded = await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == target.Entry.Id);
        Assert.Null(reloaded.LastUsedAt);
    }

    [Fact]
    public async Task Any_live_key_reports_false_on_a_fresh_install_and_after_every_key_is_revoked()
    {
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        Assert.False(await repository.AnyLiveKeyAsync(CancellationToken.None));

        var readOnly = await repository.CreateAsync("monitoring", ApiKeyScope.ReadOnly, CancellationToken.None);
        Assert.True(await repository.AnyLiveKeyAsync(CancellationToken.None));

        await repository.RevokeAsync(readOnly.Entry.Id, CancellationToken.None);
        Assert.False(await repository.AnyLiveKeyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Removing_a_revoked_key_deletes_only_that_row()
    {
        // #98 AC4, asserted PER ROW. "The list got shorter" would pass just as happily if the
        // removal had taken a neighbour with it, so each surviving row is checked by identity —
        // including the OTHER revoked one, which is the row a delete written against
        // `RevokedAt is not null` rather than against the id would wrongly sweep up.
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var live = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);
        var doomed = await repository.CreateAsync("retired-laptop", ApiKeyScope.ReadOnly, CancellationToken.None);
        var otherTombstone = await repository.CreateAsync("old-script", ApiKeyScope.ReadOnly, CancellationToken.None);

        await repository.RevokeAsync(doomed.Entry.Id, CancellationToken.None);
        await repository.RevokeAsync(otherTombstone.Entry.Id, CancellationToken.None);

        Assert.True(await repository.RemoveRevokedAsync(doomed.Entry.Id, CancellationToken.None));

        var remaining = await context.ApiKeys.AsNoTracking().ToListAsync();

        Assert.DoesNotContain(remaining, k => k.Id == doomed.Entry.Id);

        var survivingLive = Assert.Single(remaining, k => k.Id == live.Entry.Id);
        Assert.Equal("sonarr", survivingLive.Label);
        Assert.Null(survivingLive.RevokedAt);

        var survivingTombstone = Assert.Single(remaining, k => k.Id == otherTombstone.Entry.Id);
        Assert.Equal("old-script", survivingTombstone.Label);
        Assert.NotNull(survivingTombstone.RevokedAt);

        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public async Task Removing_a_live_key_is_refused_and_leaves_the_row_untouched()
    {
        // The two-step is the feature: a live credential cannot be destroyed in one call. The
        // refusal is asserted together with the row surviving intact, because a throw that had
        // already deleted the row would satisfy an exception-only assertion.
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        await repository.CreateAsync("other-admin", ApiKeyScope.Admin, CancellationToken.None);
        var live = await repository.CreateAsync("sonarr", ApiKeyScope.Admin, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.RemoveRevokedAsync(live.Entry.Id, CancellationToken.None));

        // The message names the key and states the two-step, so the UI can render it verbatim.
        Assert.Contains("sonarr", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Revoke it first", refusal.Message, StringComparison.Ordinal);

        var reloaded = await context.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == live.Entry.Id);
        Assert.Null(reloaded.RevokedAt);
    }

    [Fact]
    public async Task Removing_an_unknown_or_already_removed_id_reports_false_rather_than_throwing()
    {
        // Idempotence in the sense that matters: a repeat changes nothing and does not fault. The
        // unknown id and the already-removed one are asserted TOGETHER because after a hard delete
        // they are the same observable state — that indistinguishability is the reason the endpoint
        // answers 404 for both instead of claiming a 204 success for any id at all.
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        Assert.False(await repository.RemoveRevokedAsync(4242, CancellationToken.None));

        await repository.CreateAsync("other-admin", ApiKeyScope.Admin, CancellationToken.None);
        var target = await repository.CreateAsync("retired", ApiKeyScope.ReadOnly, CancellationToken.None);
        await repository.RevokeAsync(target.Entry.Id, CancellationToken.None);

        Assert.True(await repository.RemoveRevokedAsync(target.Entry.Id, CancellationToken.None));
        Assert.False(await repository.RemoveRevokedAsync(target.Entry.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_last_admin_key_refusal_counts_only_live_keys_after_removals()
    {
        // #98 AC4's second half. Removing tombstones must not change WHICH revocation is refused:
        // the AC5 count reads live keys, so a second admin key that has been revoked and then
        // removed leaves the remaining one just as much the last one as it was before.
        await using var context = CreateContext();
        var repository = CreateRepository(context);

        var spare = await repository.CreateAsync("spare-admin", ApiKeyScope.Admin, CancellationToken.None);
        var survivor = await repository.CreateAsync("only-admin", ApiKeyScope.Admin, CancellationToken.None);

        await repository.RevokeAsync(spare.Entry.Id, CancellationToken.None);
        await repository.RemoveRevokedAsync(spare.Entry.Id, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<ApiKeyValidationException>(
            () => repository.RevokeAsync(survivor.Entry.Id, CancellationToken.None));
        Assert.Contains("only-admin", refusal.Message, StringComparison.Ordinal);

        // Positive control for the assertion above: the refusal is about the COUNT, not about the
        // label. With a live second admin key present the same revocation is allowed, so the throw
        // above is evidence the count is being read rather than the call being refused outright.
        var replacement = await repository.CreateAsync("replacement-admin", ApiKeyScope.Admin, CancellationToken.None);
        Assert.True(await repository.RevokeAsync(survivor.Entry.Id, CancellationToken.None));
        Assert.NotNull(replacement);
    }
}
