using Arbitarr.Core.Sources;

namespace Arbitarr.Api.Tests;

/// <summary>In-memory <see cref="IQuerySnapshotStore"/> for exercising <see cref="Arbitarr.Api.Search.PaginationSnapshotService"/> without a real database.</summary>
internal sealed class FakeQuerySnapshotStore : IQuerySnapshotStore
{
    private sealed record Entry(string PayloadJson, DateTimeOffset ExpiresAt);

    private readonly Dictionary<string, Entry> _entries = new();

    public int SaveCallCount { get; private set; }

    /// <summary>The <c>ttl</c> argument passed to each <see cref="SaveAsync"/> call, in call order.</summary>
    public List<TimeSpan> ObservedTtls { get; } = new();

    public Task<string?> GetAsync(string snapshotToken, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(snapshotToken, out var entry) && entry.ExpiresAt > asOf)
        {
            return Task.FromResult<string?>(entry.PayloadJson);
        }

        return Task.FromResult<string?>(null);
    }

    public Task SaveAsync(string snapshotToken, string payloadJson, DateTimeOffset createdAt, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        SaveCallCount++;
        ObservedTtls.Add(ttl);
        _entries[snapshotToken] = new Entry(payloadJson, createdAt + ttl);
        return Task.CompletedTask;
    }
}
