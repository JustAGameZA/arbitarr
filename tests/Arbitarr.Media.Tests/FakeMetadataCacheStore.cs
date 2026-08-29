using Arbitarr.Media.Cache;

namespace Arbitarr.Media.Tests;

/// <summary>
/// Hand-written in-memory <see cref="IMetadataCacheStore"/> test double, matching this codebase's
/// established fakes-only test pattern (no mocking library referenced by the test project). Backed by
/// a plain dictionary keyed on (seriesKey, source) so <see cref="MetadataCacheCoordinator"/> can be
/// exercised without a real SQLite-backed store.
/// </summary>
internal sealed class FakeMetadataCacheStore : IMetadataCacheStore
{
    private readonly Dictionary<(string SeriesKey, string Source), MetadataCacheLookup> _rows = new();

    public int GetCallCount { get; private set; }

    public int SaveCallCount { get; private set; }

    public int SaveNegativeCallCount { get; private set; }

    public void SeedHit(string seriesKey, string source, string payloadJson, string sourceSnapshotVersion, DateTimeOffset refreshAfter) =>
        _rows[(seriesKey, source)] = MetadataCacheLookup.Hit(payloadJson, sourceSnapshotVersion, refreshAfter);

    public void SeedNegativeHit(string seriesKey, string source, string sourceSnapshotVersion, DateTimeOffset refreshAfter) =>
        _rows[(seriesKey, source)] = MetadataCacheLookup.NegativeHit(sourceSnapshotVersion, refreshAfter);

    public Task<MetadataCacheLookup> GetAsync(string seriesKey, string source, CancellationToken cancellationToken = default)
    {
        GetCallCount++;
        return Task.FromResult(_rows.TryGetValue((seriesKey, source), out var row) ? row : MetadataCacheLookup.Absent());
    }

    public Task SaveAsync(string seriesKey, string source, string payloadJson, string sourceSnapshotVersion, DateTimeOffset refreshAfter, CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        _rows[(seriesKey, source)] = MetadataCacheLookup.Hit(payloadJson, sourceSnapshotVersion, refreshAfter);
        return Task.CompletedTask;
    }

    public Task SaveNegativeAsync(string seriesKey, string source, string sourceSnapshotVersion, DateTimeOffset refreshAfter, CancellationToken cancellationToken = default)
    {
        SaveNegativeCallCount++;
        _rows[(seriesKey, source)] = MetadataCacheLookup.NegativeHit(sourceSnapshotVersion, refreshAfter);
        return Task.CompletedTask;
    }
}
