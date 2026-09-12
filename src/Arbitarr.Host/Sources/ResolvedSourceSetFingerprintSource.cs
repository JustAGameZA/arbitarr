using System.Security.Cryptography;
using System.Text;
using Arbitarr.Api.Search;
using Arbitarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-b5z: the live <see cref="ISourceSetFingerprintSource"/>, deriving the fingerprint from the
/// enabled <c>Sources</c> rows this process would actually search.
/// </summary>
/// <remarks>
/// <para><b>THE VALUE IS DERIVED PER CALL, AND SINCE arb-x7w8.4 THAT IS THE POINT.</b> Until the
/// source registry landed, the source set could not change while the process ran — it was resolved
/// once at startup into <see cref="ResolvedSourceConfiguration"/>, so this type hashed that once at
/// construction and the value was constant for the life of the host. <see cref="SourceRegistry"/>
/// ended that: adding, removing, disabling or re-addressing a source now takes effect on the NEXT
/// REQUEST, with no restart. A fingerprint still frozen at startup would then be the same token for
/// two genuinely different source sets, and would serve one set's SQLite-persisted snapshot rows to
/// the other — precisely the failure the fingerprint exists to prevent, moved from across restarts
/// to within one process. So it is read from the database on each call, which is what keeps
/// "which sources produced that result set" a true statement rather than a startup-time one.</para>
///
/// <para><b>WHAT IS IN IT, AND WHY EACH PART.</b> Per enabled row: the id, the kind, the base URL,
/// the API path, and the priority. The id and base URL settle WHICH upstream was queried; the kind
/// and API path settle WHAT was asked of it (a row flipped from Newznab to Torznab, or relocated to
/// a different endpoint by a reverse proxy, returns a different feed from the same address); and the
/// priority settles the ORDER, which the dedup stage reads to decide which member of an exact-match
/// group is presented first — so two sets differing only in priority produce genuinely different
/// result ordering and must not share a snapshot. The display name is deliberately absent: renaming
/// a source in the UI changes nothing about what it returns, and discarding every snapshot for a
/// cosmetic edit is a cost with no matching benefit.</para>
///
/// <para><b>THE API KEY IS NOT IN THE FINGERPRINT, deliberately.</b> The question this answers is
/// "which sources produced that result set", and the identity, address and endpoint of each source
/// settles it: rotating a key does not change which upstream is queried or what it returns, so it
/// must not discard every snapshot. Excluding it also keeps the secret out of a value that is
/// hashed, cached, and reasoned about in logs and tests — the fingerprint is not a secret-bearing
/// surface and must not become one. Note this type never reads a key at all: it queries the
/// <c>Sources</c> table and never the write-only Settings rows the keys live in, so there is no path
/// by which one could reach the hash.</para>
///
/// <para>Hashed rather than concatenated so the token component is fixed-width and carries no
/// operator-supplied text. Rows are ordered by id — a TOTAL and stable order independent of the
/// priority that is itself hashed — so the same set cannot produce two fingerprints because the
/// database returned it in a different order.</para>
/// </remarks>
public sealed class ResolvedSourceSetFingerprintSource : ISourceSetFingerprintSource
{
    /// <summary>
    /// ASCII US (unit separator), between the fields of one row. Written as a numeric code point
    /// rather than as a literal control character so the source text stays legible and survives any
    /// tool that normalises or strips what it cannot display.
    /// </summary>
    private const char FieldSeparator = (char)0x1f;

    /// <summary>ASCII RS (record separator), between rows.</summary>
    private const char RowSeparator = (char)0x1e;

    private readonly ArbitarrDbContext _dbContext;

    public ResolvedSourceSetFingerprintSource(ArbitarrDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async ValueTask<string> GetAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Sources
            .AsNoTracking()
            .Where(s => s.Enabled)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Kind, s.BaseUrl, s.ApiPath, s.Priority })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var raw = new StringBuilder();
        foreach (var row in rows)
        {
            // The unit separator matches the snapshot token's own convention, and separates EVERY
            // field as well as every row — without it between the fields, two different sets could
            // concatenate into one identical raw string (a base URL ending in a path segment that
            // the next field begins with), which is exactly the boundary case the separator exists
            // to close. The record separator between rows does the same job one level up.
            raw.Append(row.Id).Append(FieldSeparator)
                .Append(row.Kind).Append(FieldSeparator)
                .Append(row.BaseUrl).Append(FieldSeparator)
                .Append(row.ApiPath).Append(FieldSeparator)
                .Append(row.Priority).Append(RowSeparator);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToString())));
    }
}
