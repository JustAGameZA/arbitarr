using Arbitarr.Core.Pipeline;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Host.Sources;
using Arbitarr.TestSupport;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Arbitarr.Host.Tests;

/// <summary>
/// arb-cvru: <see cref="DbSourcePriorityLookup"/> is the real, DB-backed
/// <see cref="ISourcePriorityLookup"/> that replaces the <see cref="AllEqualSourcePriority"/>
/// placeholder <c>Program.cs</c> registered before the source registry (arb-x7w8.4) existed to
/// expose real priorities. This carries the same contract test arch-342 asked for
/// (<c>DedupStageTests</c> exercises the interface against a fake) against the real implementation.
/// </summary>
public sealed class DbSourcePriorityLookupTests : IDisposable
{
    // RFC 5737 TEST-NET-1: non-routable, no real address is committed.
    private const string BaseUrlA = "http://192.0.2.50:9117";
    private const string BaseUrlB = "http://192.0.2.51:9117";

    private readonly SqliteTestDatabase _database = new("arbitarr-cvru-priority-lookup");

    public void Dispose() => _database.Dispose();

    private ArbitarrDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ArbitarrDbContext>();
        optionsBuilder.UseSqlite(_database.ConnectionString);
        var context = new ArbitarrDbContext(optionsBuilder.Options);
        context.Database.Migrate();
        return context;
    }

    private static Source Row(string displayName, string baseUrl, int priority, bool enabled = true) =>
        new()
        {
            Kind = "Newznab",
            DisplayName = displayName,
            BaseUrl = baseUrl,
            ApiPath = "/api",
            Priority = priority,
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// An unrecognised name returns the neutral default rather than throwing — dedup ordering is a
    /// presentation preference, and an unknown source name is not a reason to fail a search (see
    /// <see cref="ISourcePriorityLookup.PriorityOf"/>'s documented contract).
    /// </summary>
    [Fact]
    public async Task An_unknown_name_returns_zero_and_does_not_throw()
    {
        using var context = CreateContext();
        context.Sources.Add(Row("known", BaseUrlA, priority: 7));
        await context.SaveChangesAsync();

        var priority = new DbSourcePriorityLookup(context).PriorityOf("nobody-configured-this-name");

        Assert.Equal(0, priority);
    }

    /// <summary>
    /// A known, enabled source's name resolves to its own stored <see cref="Source.Priority"/> — and
    /// the two rows carry DISTINCT, non-default priorities, so a lookup that returned a constant for
    /// every name (or the wrong row's value) could not pass both assertions by accident (CLAUDE.md
    /// §4 non-vacuity discipline).
    /// </summary>
    [Fact]
    public async Task A_known_enabled_source_resolves_to_its_own_stored_priority()
    {
        using var context = CreateContext();
        context.Sources.AddRange(
            Row("alpha", BaseUrlA, priority: 42),
            Row("beta", BaseUrlB, priority: 13));
        await context.SaveChangesAsync();

        var lookup = new DbSourcePriorityLookup(context);

        Assert.Equal(42, lookup.PriorityOf("alpha"));
        Assert.Equal(13, lookup.PriorityOf("beta"));
    }

    /// <summary>
    /// A DISABLED source's name resolves to zero, falling through to the unknown-name default,
    /// because disabled rows are excluded from the read — matching <see cref="Arbitarr.Host.Sources.SourceRegistry"/>'s
    /// own <c>Where(s =&gt; s.Enabled)</c> filter. A disabled source can never appear in a dedup
    /// group (<c>UpstreamMergeStage</c> only fans out over the resolved, enabled set), so a lookup
    /// that let a disabled row's priority leak through would be pure risk with no offsetting benefit.
    /// </summary>
    [Fact]
    public async Task A_disabled_source_resolves_to_zero_not_its_stored_priority()
    {
        using var context = CreateContext();
        context.Sources.Add(Row("turned-off", BaseUrlA, priority: 99, enabled: false));
        await context.SaveChangesAsync();

        var priority = new DbSourcePriorityLookup(context).PriorityOf("turned-off");

        Assert.Equal(0, priority);
    }
}
