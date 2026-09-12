using Arbitarr.Core.Diagnostics;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Diagnostics;

/// <summary>
/// arb-v3w: EF Core-backed <see cref="IDownloadRefusalStore"/> over the
/// <see cref="DownloadRefusalEntry"/> table. Kept outside Arbitarr.Core so Core stays free of any
/// reference to Arbitarr.Data; Core defines only the persistence-agnostic contract.
///
/// <para>The connection string is NOT formatted here — this takes an
/// <see cref="ArbitarrDbContext"/> whose options the composition root built from
/// <c>DatabaseConnectionStrings</c> (docs/standards/data.md), exactly as every other store in this
/// project does. That also pins WHICH database this is: the application database
/// <c>arbitarr.db</c>, never <c>arbitarr-logs.db</c>, which has no EF migrations at all.</para>
/// </summary>
public sealed class DownloadRefusalStore : IDownloadRefusalStore
{
    private readonly ArbitarrDbContext _dbContext;

    public DownloadRefusalStore(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task UpsertAsync(DownloadRefusal refusal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        var entry = await _dbContext.DownloadRefusalEntries
            .SingleOrDefaultAsync(e => e.SourceName == refusal.SourceName, cancellationToken)
            .ConfigureAwait(false);

        if (entry is null)
        {
            _dbContext.DownloadRefusalEntries.Add(new DownloadRefusalEntry
            {
                SourceName = refusal.SourceName,
                Reason = refusal.Reason,
                ObservedSinceUtc = refusal.ObservedSinceUtc,
                LastObservedUtc = refusal.LastObservedUtc,
            });
        }
        else
        {
            // ObservedSinceUtc is written from the refusal as given, NOT preserved from the row.
            // The caller (the tracker) has already applied the preserve-the-first-instant rule and
            // is handing over the resolved entry; re-deriving it here would be a second copy of that
            // rule, free to disagree with the in-memory one the dashboard is actually reading.
            entry.Reason = refusal.Reason;
            entry.ObservedSinceUtc = refusal.ObservedSinceUtc;
            entry.LastObservedUtc = refusal.LastObservedUtc;
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string sourceName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        await _dbContext.DownloadRefusalEntries
            .Where(e => e.SourceName == sourceName)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadRefusal>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        // The whole table in one read, without paging: it holds at most one row per configured
        // source (see DownloadRefusalEntry), so this is a handful of rows by construction — the
        // condition docs/standards/data.md sets for loading a table whole rather than paging it.
        var entries = await _dbContext.DownloadRefusalEntries
            .AsNoTracking()
            .OrderBy(e => e.SourceName)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return entries
            .Select(e => new DownloadRefusal(e.SourceName, e.Reason, e.ObservedSinceUtc, e.LastObservedUtc))
            .ToArray();
    }

    public async Task<int> PruneUnknownSourcesAsync(
        IReadOnlyCollection<string> knownSourceNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(knownSourceNames);

        // Materialised to an array first because EF translates a Contains over an in-memory
        // collection into the SQL IN list, and the parameter is an interface whose concrete type
        // (and therefore whether it can be enumerated more than once) is the caller's choice.
        //
        // An empty set is NOT special-cased into a no-op: it means the Sources table is empty, so
        // every row here IS orphaned and the unconstrained delete below is the correct answer. See
        // the interface doc — short-circuiting it is the tempting change that reintroduces the bug
        // for the one install where it matters most.
        var known = knownSourceNames.ToArray();

        return await _dbContext.DownloadRefusalEntries
            .Where(e => !known.Contains(e.SourceName))
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
