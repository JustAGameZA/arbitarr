using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Arbitarr.Data.Sources;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-x7w8.10: wraps each resolved <see cref="IUpstreamSource"/> in a
/// <see cref="BudgetedUpstreamSource"/>, pairing it with its own <see cref="Source"/> configuration
/// row so the budget is read per source rather than from one global figure.
///
/// <para><b>This exists so the wrapping happens at the LIST boundary.</b> arb-x7w8.4 replaces the
/// single typed <c>IUpstreamSource</c> registration with a registry resolving N sources per scope; a
/// decorator hung off that single registration would disappear when it does, and it would disappear
/// SILENTLY — every test would still pass, and budgets would simply stop being enforced. Wrapping
/// whatever the list resolves is invariant under that change.</para>
///
/// <para><b>An unconfigured source is passed through UNWRAPPED rather than blocked.</b> A source
/// with no row has no limits to enforce and no <see cref="Source.LimitsUnit"/> to count over, so
/// there is nothing for the gate to do; refusing to serve it instead would turn a missing
/// configuration row into an outage. This is the same posture the null-is-unlimited rule takes one
/// level down — absence of a limit is not a limit of zero.</para>
/// </summary>
public sealed class BudgetedUpstreamSourceFactory
{
    private readonly ArbitarrDbContext _dbContext;
    private readonly SourceApiHitCounter _counter;
    private readonly SourceBackoffStore _backoff;
    private readonly IEventSink _eventSink;

    public BudgetedUpstreamSourceFactory(
        ArbitarrDbContext dbContext,
        SourceApiHitCounter counter,
        SourceBackoffStore backoff,
        IEventSink eventSink)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _counter = counter ?? throw new ArgumentNullException(nameof(counter));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    /// <summary>
    /// Wraps every source in <paramref name="sources"/> that has a configuration row, matching by
    /// <see cref="Source.DisplayName"/> against <see cref="IUpstreamSource.Name"/> — ordinally, the
    /// same comparison <c>SourceSeeder</c> uses, so a casing difference does not silently produce an
    /// ungated source.
    /// </summary>
    public IReadOnlyList<IUpstreamSource> WrapAll(IEnumerable<IUpstreamSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var resolved = sources.ToArray();

        if (resolved.Length == 0)
        {
            return resolved;
        }

        // Read SYNCHRONOUSLY because this runs inside a DI factory, which has no async seam. The
        // read is one small indexed table, resolved once per scope rather than per call.
        var configurations = _dbContext.Sources
            .AsNoTracking()
            .ToDictionary(s => s.DisplayName, StringComparer.Ordinal);

        return resolved
            .Select(source => configurations.TryGetValue(source.Name, out var configuration)
                ? new BudgetedUpstreamSource(source, configuration, _counter, _backoff, _eventSink)
                : source)
            .ToArray();
    }
}
