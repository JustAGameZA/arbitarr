using Microsoft.Data.Sqlite;

namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// Wraps a <see cref="SqliteException"/> raised while
/// <see cref="Arbitarr.Integration.Tests.ArbitarrWebApplicationFactory.SeedAsync"/> is seeding, adding
/// the fields the next CI sighting of arb-tdc4 (intermittent Error 5, "database is locked") needs to
/// be diagnosable from a log line alone.
///
/// <para><b>Deliberately test-side only.</b> Two repro rounds (see the bead) neither proved nor
/// disproved either suspected cause, so there is no failing baseline to fix a production behaviour
/// against yet. This does not retry, sleep, or swallow anything: the original exception becomes the
/// <see cref="Exception.InnerException"/> of a new one, so the test still fails exactly as before,
/// just with more to go on.</para>
///
/// <para><b>What the message carries, and what it deliberately omits.</b> The SQLite primary and
/// extended error codes, elapsed milliseconds, whether the first maintenance pass had already
/// completed, and the database FILE NAME (never a full path — this is a public repo and CI logs are
/// public too, CLAUDE.md's secrets policy). No connection string and no row data: a connection string
/// can carry a path, and row data is exactly the kind of content the seed delegate closes over that
/// this helper has no business inspecting.</para>
/// </summary>
/// <summary>
/// The replacement exception <see cref="SeedDiagnostics.Wrap"/> builds. A plain, undecorated
/// <see cref="Exception"/> subtype: it exists only to carry a diagnostic message and the original
/// <see cref="SqliteException"/> as <see cref="Exception.InnerException"/>, and nothing here inspects
/// its type, so it does not need to mimic <see cref="SqliteException"/>'s shape.
/// </summary>
internal sealed class SeedDiagnosticsException : Exception
{
    internal SeedDiagnosticsException(string message, SqliteException innerException)
        : base(message, innerException)
    {
    }
}

internal static class SeedDiagnostics
{
    /// <summary>
    /// Builds the replacement exception. Named <c>Wrap</c> rather than <c>Rethrow</c>: this returns
    /// the exception for the caller to throw, so the caller's own <c>throw</c> preserves ITS stack
    /// trace at the point of the rethrow, and the original's stack trace survives unmodified as the
    /// <see cref="Exception.InnerException"/>.
    /// </summary>
    /// <param name="exception">The <see cref="SqliteException"/> the seed delegate raised.</param>
    /// <param name="elapsed">How long the seed delegate had been running when it threw.</param>
    /// <param name="maintenanceFirstPassCompleted">
    /// Whether the host's <c>MaintenanceHostedService</c> had already published its first-pass
    /// completion at the moment the exception was caught, or null when that could not be determined
    /// (the service was not found, or the host was already disposed).
    /// </param>
    /// <param name="configDirectory">
    /// The host's config directory. Only <see cref="Path.GetFileName(string)"/> of the database path
    /// under it is used in the message — never this directory itself, which is a local filesystem
    /// path and must not appear in a public CI log.
    /// </param>
    /// <returns>
    /// A new <see cref="SeedDiagnosticsException"/> carrying <paramref name="exception"/> as its
    /// <see cref="Exception.InnerException"/>. Not a <see cref="SqliteException"/> itself:
    /// <see cref="SqliteException"/> has no public constructor that also accepts an inner exception,
    /// and reflecting a protected base setter to force one on would be exactly the kind of fragile
    /// trick this diagnostics path should not depend on. The caller's own <c>throw</c> preserves this
    /// new exception's stack trace at the rethrow site, and <paramref name="exception"/>'s original
    /// stack trace survives unmodified as the inner exception.
    /// </returns>
    public static SeedDiagnosticsException Wrap(
        SqliteException exception,
        TimeSpan elapsed,
        bool? maintenanceFirstPassCompleted,
        string configDirectory)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var databaseFileName = Path.GetFileName(new Arbitarr.Data.Backup.BackupPaths(configDirectory).DatabasePath);

        var maintenanceState = maintenanceFirstPassCompleted switch
        {
            true => "completed",
            false => "not completed",
            null => "unknown",
        };

        var message =
            $"SeedAsync failed against '{databaseFileName}' with SqliteErrorCode={exception.SqliteErrorCode}, " +
            $"SqliteExtendedErrorCode={exception.SqliteExtendedErrorCode} after {elapsed.TotalMilliseconds:F0}ms. " +
            $"First maintenance pass at the time of failure: {maintenanceState}. See the inner exception for " +
            "the original stack trace.";

        return new SeedDiagnosticsException(message, exception);
    }
}
