using System.Threading;

namespace Arbitarr.Core.Media;

/// <summary>
/// The in-process <see cref="IArrInstanceEpoch"/>. Registered as a SINGLETON, because the writer
/// (a scoped repository handling one admin request) and the reader (a scoped resolver handling a
/// later search request) are different instances and must see the same counter.
/// </summary>
/// <remarks>
/// The field is INSTANCE state advanced with <see cref="Interlocked.Increment(ref long)"/>: the
/// admin write path and the search path run concurrently, and a torn or lost increment would leave
/// the memo serving the previous instance's title — the exact defect this type exists to close.
/// Reads go through <c>Interlocked.Read</c> so a 32-bit process cannot observe half of a written
/// value.
/// </remarks>
public sealed class ArrInstanceEpoch : IArrInstanceEpoch
{
    private long _current;

    /// <inheritdoc />
    public long Current => Interlocked.Read(ref _current);

    /// <inheritdoc />
    public void Bump() => Interlocked.Increment(ref _current);
}
