using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Arbitarr.Host.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-x7w8.4: <see cref="ResolvedSourceSetFingerprintSource"/> now derives its value from the
/// enabled <c>Sources</c> rows on every call, rather than hashing one startup-resolved configuration
/// once. These are the derivation tests; the wiring is pinned separately by
/// <c>Arbitarr.Integration.Tests.SourceSetFingerprintWiringTests</c> against the real container.
/// </summary>
/// <remarks>
/// <para><b>THE PROPERTY THAT MATTERS IS PER-SOURCE-SET DISTINCTNESS, not "it changed once".</b> A
/// fingerprint that moved on the first edit and then stuck would pass a single before/after
/// assertion while still handing one source set's persisted snapshots to another — which is the
/// exact failure the fingerprint exists to prevent. So the central test below builds THREE different
/// sets and asserts three DISTINCT values, and a constant implementation fails it.</para>
/// </remarks>
public sealed class ResolvedSourceSetFingerprintSourceTests : IDisposable
{
    // RFC 5737 TEST-NET-1 throughout: non-routable, and no real address is committed.
    private const string FirstBaseUrl = "http://192.0.2.30:5076";
    private const string SecondBaseUrl = "http://192.0.2.31:9117";

    private readonly SqliteTestDatabase _database = new("arbitarr-x7w84-fingerprint");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static Source Row(
        string displayName,
        string baseUrl,
        string kind = SourceRepository.NewznabKind,
        string apiPath = "/api",
        int priority = 0,
        bool enabled = true) =>
        new()
        {
            Kind = kind,
            DisplayName = displayName,
            BaseUrl = baseUrl,
            ApiPath = apiPath,
            Priority = priority,
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static Task<string> FingerprintOf(ArbitarrDbContext context) =>
        new ResolvedSourceSetFingerprintSource(context).GetAsync(CancellationToken.None).AsTask();

    /// <summary>
    /// An install with no sources still produces a fingerprint rather than throwing. It is the
    /// honest answer for a set of zero, and the value the /api/config/effective empty state sits
    /// behind.
    /// </summary>
    [Fact]
    public async Task An_empty_source_set_produces_a_fingerprint()
    {
        using var context = CreateContext();

        Assert.False(string.IsNullOrEmpty(await FingerprintOf(context)));
    }

    /// <summary>
    /// <b>THE CENTRAL ASSERTION: three different source sets produce three DISTINCT fingerprints.</b>
    /// </summary>
    /// <remarks>
    /// Asserting distinctness across three sets rather than a single before/after pair is what makes
    /// this bite. A constant implementation fails at the first comparison, but so does a
    /// nearly-constant one that changes on the first write and then stops — and that second shape is
    /// the realistic regression, because it is what caching the value after the first call would
    /// produce. Set 1 is empty, set 2 has one source, set 3 has two.
    /// </remarks>
    [Fact]
    public async Task Three_different_source_sets_produce_three_distinct_fingerprints()
    {
        using var context = CreateContext();

        var empty = await FingerprintOf(context);

        context.Sources.Add(Row("first", FirstBaseUrl));
        await context.SaveChangesAsync();
        var one = await FingerprintOf(context);

        context.Sources.Add(Row("second", SecondBaseUrl));
        await context.SaveChangesAsync();
        var two = await FingerprintOf(context);

        Assert.Equal(3, new HashSet<string>(StringComparer.Ordinal) { empty, one, two }.Count);
    }

    /// <summary>
    /// Disabling a source changes the fingerprint, because a disabled source is not searched and
    /// therefore did not produce the results a snapshot holds. An implementation reading every row
    /// rather than the enabled ones passes every other test in this file and fails this one.
    /// </summary>
    [Fact]
    public async Task Disabling_a_source_changes_the_fingerprint()
    {
        using var context = CreateContext();
        var row = Row("toggled", FirstBaseUrl);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var whileEnabled = await FingerprintOf(context);

        row.Enabled = false;
        await context.SaveChangesAsync();

        Assert.NotEqual(whileEnabled, await FingerprintOf(context));
    }

    /// <summary>
    /// Priority is part of the fingerprint: it decides the order sources are presented in, which the
    /// dedup stage reads to pick the surviving member of an exact-match group. Two sets differing
    /// only in priority therefore produce differently-ordered results and must not share a snapshot.
    /// </summary>
    [Fact]
    public async Task A_changed_priority_changes_the_fingerprint()
    {
        using var context = CreateContext();
        var row = Row("reprioritised", FirstBaseUrl, priority: 0);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var atZero = await FingerprintOf(context);

        row.Priority = 50;
        await context.SaveChangesAsync();

        Assert.NotEqual(atZero, await FingerprintOf(context));
    }

    /// <summary>
    /// The API path is part of the fingerprint: the same host relocated to a different endpoint by a
    /// reverse proxy serves a different feed, so its results are not interchangeable with the old
    /// ones.
    /// </summary>
    [Fact]
    public async Task A_changed_api_path_changes_the_fingerprint()
    {
        using var context = CreateContext();
        var row = Row("relocated", FirstBaseUrl, apiPath: "/api");
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var atDefaultPath = await FingerprintOf(context);

        row.ApiPath = "/api/v2.0/indexers/all/results/torznab";
        await context.SaveChangesAsync();

        Assert.NotEqual(atDefaultPath, await FingerprintOf(context));
    }

    /// <summary>
    /// Renaming a source does NOT change the fingerprint. The display name says nothing about what
    /// the source returns, and discarding every persisted snapshot for a cosmetic edit is a cost
    /// with no matching benefit.
    /// </summary>
    [Fact]
    public async Task Renaming_a_source_does_not_change_the_fingerprint()
    {
        using var context = CreateContext();
        var row = Row("before-rename", FirstBaseUrl);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var beforeRename = await FingerprintOf(context);

        row.DisplayName = "after-rename";
        await context.SaveChangesAsync();

        Assert.Equal(beforeRename, await FingerprintOf(context));
    }

    /// <summary>
    /// The API key never reaches the fingerprint, and the POSITIVE CONTROL is what makes that
    /// assertion bite.
    /// </summary>
    /// <remarks>
    /// Asserting only that the planted key is absent from the hash would pass just as happily for a
    /// key that was never stored at all — an empty set contains nothing (CLAUDE.md §4). So this
    /// first proves the key IS in the database and reachable (the settings row is read back and
    /// asserted to hold it), and only then asserts the fingerprint is byte-identical to the one
    /// computed before the key existed. Equality across a key that is demonstrably in play is the
    /// control; the absence of the substring on its own is not.
    /// </remarks>
    [Fact]
    public async Task The_api_key_is_not_part_of_the_fingerprint()
    {
        const string plantedKey = "secret-api-key-fingerprint-probe";

        using var context = CreateContext();
        var row = Row("keyed", FirstBaseUrl);
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var withoutKey = await FingerprintOf(context);

        context.Settings.Add(new SettingEntry
        {
            Name = SourceRepository.ApiKeySettingName(row.Id),
            Value = plantedKey,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        // The positive control: the key is genuinely stored and readable, so the equality below is
        // about a value that was in play rather than one that never arrived.
        var storedKey = await context.Settings.AsNoTracking()
            .Where(e => e.Name == SourceRepository.ApiKeySettingName(row.Id))
            .Select(e => e.Value)
            .FirstOrDefaultAsync();
        Assert.Equal(plantedKey, storedKey);

        var withKey = await FingerprintOf(context);

        Assert.Equal(withoutKey, withKey);
        Assert.DoesNotContain(plantedKey, withKey, StringComparison.OrdinalIgnoreCase);

        // ROTATING the key must not move it either. A key going absent-to-present and a key changing
        // value are different edits, and an implementation that hashed only the key's PRESENCE would
        // pass the equality above while failing this — so both are asserted rather than one standing
        // in for the other.
        var keyRow = await context.Settings.FirstAsync(e => e.Name == SourceRepository.ApiKeySettingName(row.Id));
        keyRow.Value = "secret-api-key-fingerprint-probe-rotated";
        await context.SaveChangesAsync();

        Assert.Equal(withoutKey, await FingerprintOf(context));
    }

    /// <summary>
    /// Two source sets whose fields would concatenate into the same raw string must still produce
    /// different fingerprints. This is the boundary the field separator exists to close: without one
    /// between the base URL and the API path, <c>".../a" + "/b"</c> and <c>"..." + "/a/b"</c> are the
    /// same text.
    /// </summary>
    [Fact]
    public async Task A_boundary_shifted_source_set_produces_a_different_fingerprint()
    {
        using var context = CreateContext();
        var row = Row("boundary", "http://192.0.2.60/a", apiPath: "/b");
        context.Sources.Add(row);
        await context.SaveChangesAsync();

        var first = await FingerprintOf(context);

        row.BaseUrl = "http://192.0.2.60";
        row.ApiPath = "/a/b";
        await context.SaveChangesAsync();

        Assert.NotEqual(first, await FingerprintOf(context));
    }
}
