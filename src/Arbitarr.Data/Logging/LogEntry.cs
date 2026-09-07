namespace Arbitarr.Data.Logging;

/// <summary>
/// One application log row, in the shape Sonarr's <c>LogResource</c> uses
/// (<c>Time, Level, Logger, Message, Exception, ExceptionType</c>) — #65 plan §1. Kept as a plain
/// record rather than an EF entity because the log database is a standalone SQLite file with no
/// <see cref="ArbitarrDbContext"/> over it; see <see cref="LogStore"/> for why.
/// </summary>
/// <param name="Id">Monotonic row id, also the paging cursor and the newest-first sort key.</param>
/// <param name="Time">When the entry was logged (UTC).</param>
/// <param name="Level">The <c>Microsoft.Extensions.Logging.LogLevel</c> name, e.g. "Information".</param>
/// <param name="Logger">The logger category, with the <c>Arbitarr.</c> prefix stripped.</param>
/// <param name="Message">The formatted message, after <see cref="LogMessageCleanser"/>.</param>
/// <param name="Exception">The exception text, after <see cref="LogMessageCleanser"/>; null if none.</param>
/// <param name="ExceptionType">The exception's type name; null if there was no exception.</param>
public sealed record LogEntry(
    long Id,
    DateTimeOffset Time,
    string Level,
    string Logger,
    string Message,
    string? Exception,
    string? ExceptionType);

/// <summary>One page of <see cref="LogEntry"/> rows, plus the total matching the same filters.</summary>
/// <param name="Entries">The rows on this page, newest first.</param>
/// <param name="Total">
/// How many rows match the filters in total, across every page. Needed by the UI to render "page 2
/// of 9" and to disable the next-page control on the last page; a page-length check cannot
/// distinguish "last page" from "exactly-full page".
/// </param>
public sealed record LogPage(IReadOnlyList<LogEntry> Entries, int Total);
