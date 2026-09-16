using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Sources;
using Arbitarr.Data;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-x7w8.10: the <see cref="ISourceRegistry"/> DECORATOR that pairs every resolved source with
/// its own <see cref="Source"/> row and wraps it in a <see cref="BudgetedUpstreamSource"/>, so the
/// budget and the durable backoff are read per source rather than from one global figure.
///
/// <para><b>THE WRAPPING HAPPENS AT THE REGISTRY BOUNDARY, AND THAT IS THE ONLY BOUNDARY THAT
/// SURVIVES.</b> An earlier revision of this bead wrapped at an
/// <c>IReadOnlyList&lt;IUpstreamSource&gt;</c> registration, because that was the shape Program.cs
/// had. arb-x7w8.4 removed it: the search path now resolves N sources per scope through
/// <see cref="ISourceRegistry"/> and no list registration exists for a decorator to hang off. Had
/// this stayed where it was, the factory would still have been registered and simply never called —
/// every test would have passed and budgets would have stopped being enforced with nothing to see.
/// <see cref="ISourceRegistry"/>'s own type doc names this as the decoration boundary and keeps the
/// interface at one method for it; decorating the interface every consumer already takes is what
/// makes the gate unavoidable rather than attached to one particular registration's shape.</para>
///
/// <para><b>THE ROW READ IS ASYNC, because <see cref="ResolveAsync"/> is.</b> The previous factory
/// read the <c>Sources</c> table SYNCHRONOUSLY and opened its own scope to do it, both forced by
/// running inside a DI factory delegate that has no async seam. Neither is needed here: this runs
/// inside an <c>await</c>, so it reads through its own injected <see cref="ArbitarrDbContext"/> and
/// blocks nothing. That also removes the blocking read <c>HostBlockingAsyncCallTests</c> exists to
/// ban.</para>
///
/// <para><b>THE PAIRING IS BY <see cref="Source.Id"/>, NOT BY <see cref="Source.DisplayName"/>.</b>
/// The row id is the identity arb-x7w8.4's fingerprint and its skip warnings are already keyed by,
/// and it is the only field on the row an operator cannot edit: renaming a source in the admin UI
/// must not silently produce an ungated source, which is exactly what a name match would do. The
/// registry hands back the ids alongside the adapters (see
/// <see cref="ISourceRegistry.ResolveAsync"/>'s companion on <see cref="SourceRegistry"/>), so
/// nothing is re-queried by name to find them.</para>
///
/// <para><b>Every resolved source HAS a row, so there is no pass-through branch.</b> Before the
/// registry, sources were constructed from a startup-resolved configuration and one could exist with
/// no row behind it; the factory passed such a source through unwrapped rather than blocking it.
/// With the registry every source is built FROM a row inside <see cref="SourceRegistry.ResolveAsync"/>
/// and the id it is paired with came from that same row, so the unmatched case cannot arise and is
/// not written — a branch that cannot execute is not a safety net, it is untested code that reads
/// like one.</para>
///
/// <para><b>The memoisation is the inner registry's and is not duplicated here.</b>
/// <see cref="SourceRegistry"/> already resolves once per scope; this decorator is scoped too and
/// memoises its WRAPPED list for the same reason, so two consumers in one request share one set of
/// decorators rather than each constructing its own over the same adapters.</para>
/// </summary>
public sealed class BudgetedSourceRegistry : ISourceRegistry
{
    private readonly SourceRegistry _inner;
    private readonly ArbitarrDbContext _dbContext;
    private readonly ISourceGateScopeFactory _gateScopeFactory;
    private readonly IEventSink _eventSink;

    /// <summary>
    /// The wrapped set this scope has already produced, or null until the first resolution. One
    /// scope, one set of decorators — see the type doc.
    /// </summary>
    private IReadOnlyList<IUpstreamSource>? _wrapped;

    public BudgetedSourceRegistry(
        SourceRegistry inner,
        ArbitarrDbContext dbContext,
        ISourceGateScopeFactory gateScopeFactory,
        IEventSink eventSink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _gateScopeFactory = gateScopeFactory ?? throw new ArgumentNullException(nameof(gateScopeFactory));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IUpstreamSource>> ResolveAsync(CancellationToken cancellationToken)
    {
        if (_wrapped is not null)
        {
            return _wrapped;
        }

        var resolved = await _inner.ResolveWithRowIdsAsync(cancellationToken).ConfigureAwait(false);

        if (resolved.Count == 0)
        {
            return _wrapped = Array.Empty<IUpstreamSource>();
        }

        // Read by the ids the registry just resolved rather than the whole table: the set is already
        // known, and a row disabled between the two reads is one this scope should not gate against.
        var ids = resolved.Select(r => r.SourceId).ToArray();

        var configurations = await _dbContext.Sources
            .AsNoTracking()
            .Where(s => ids.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, cancellationToken)
            .ConfigureAwait(false);

        var wrapped = new List<IUpstreamSource>(resolved.Count);

        foreach (var (sourceId, source) in resolved)
        {
            // configurations[sourceId] rather than TryGetValue: the id came from a row this same
            // context can read, so a miss is a broken invariant and not a case to degrade through.
            wrapped.Add(new BudgetedUpstreamSource(
                source,
                configurations[sourceId],
                _gateScopeFactory,
                _eventSink));
        }

        return _wrapped = wrapped;
    }
}
