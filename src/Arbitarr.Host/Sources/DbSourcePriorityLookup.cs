using Arbitarr.Core.Pipeline;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-cvru: the real <see cref="ISourcePriorityLookup"/>, reading <c>DisplayName</c> →
/// <c>Priority</c> straight off the enabled <c>Sources</c> rows. Replaces the
/// <see cref="AllEqualSourcePriority"/> placeholder that <c>Program.cs</c> registered before the
/// source registry (arb-x7w8.4) existed to expose real priorities.
/// </summary>
/// <remarks>
/// <para><b>WHY A SEPARATE READ FROM <see cref="SourceRegistry"/> RATHER THAN THREADING THE VALUE
/// THROUGH IT.</b> <see cref="SourceRegistry"/> hands back <see cref="IUpstreamSource"/> instances,
/// and <c>IUpstreamSource</c> exposes only <c>Name</c> — not <c>Source.Priority</c>. Widening that
/// contract just to carry a number DedupStage alone needs would leak an ordering concern into every
/// adapter implementation. A second, small, direct query over the same <c>Enabled</c> rows is
/// simpler and more honest than that.</para>
///
/// <para><b>ONLY <c>Enabled</c> ROWS ARE READ, matching <see cref="SourceRegistry"/>'s own
/// filter.</b> A disabled source can never appear in a dedup group, because
/// <c>UpstreamMergeStage</c> only fans out over <see cref="ISourceRegistry"/>'s resolved (enabled)
/// set — so a lookup that also read disabled rows could only ever be asked about names that could
/// never occur, which is pure risk (a disabled source silently keeping influence over live
/// ordering) with no offsetting benefit.</para>
///
/// <para><b>REGISTERED SCOPED, the same lifetime as <see cref="ISourceRegistry"/> and
/// <c>DedupStage</c>.</b> A singleton would cache priorities across requests and miss an operator's
/// live edit — exactly the staleness arb-x7w8.4's whole registry redesign exists to avoid. Each
/// scope reads fresh, so the next request sees the change.</para>
///
/// <para><b>AN UNKNOWN NAME RETURNS ZERO, NEVER THROWS</b>, per the interface's documented contract:
/// dedup ordering is a presentation preference, not a reason to fail a search. A disabled source's
/// name falls through to this same default, because it is excluded from the read below.</para>
/// </remarks>
public sealed class DbSourcePriorityLookup : ISourcePriorityLookup
{
    private readonly ArbitarrDbContext _dbContext;

    /// <summary>
    /// The resolution this scope has already performed, or null until the first one. Mirrors
    /// <see cref="SourceRegistry"/>'s own memo field: one scope, one read, so a request with several
    /// dedup calls does not re-query the table per name.
    /// </summary>
    private Dictionary<string, int>? _prioritiesByName;

    public DbSourcePriorityLookup(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public int PriorityOf(string sourceName)
    {
        var priorities = _prioritiesByName ??= _dbContext.Sources
            .AsNoTracking()
            .Where(s => s.Enabled)
            .ToDictionary(s => s.DisplayName, s => s.Priority, StringComparer.Ordinal);

        return priorities.TryGetValue(sourceName, out var priority) ? priority : 0;
    }
}
