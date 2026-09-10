using Microsoft.Data.Sqlite;

namespace Arbitarr.Data;

/// <summary>
/// THE ONLY PLACE a connection string naming the application database is built, and therefore the
/// only place that knows the complete set of them.
///
/// <para><b>Why this type exists at all.</b> Microsoft.Data.Sqlite keys its connection pools by the
/// FULL connection string, not by the file. Two strings naming one file are two independent pools,
/// and clearing one leaves the other's handles open. <see cref="RestoreService"/> must drop EVERY
/// handle on the database before it swaps the file, so it needs the complete set — and a set
/// assembled from strings formatted inline at each call site is only complete until someone adds
/// another call site. Building them here makes the set closed by construction: a new shape cannot
/// be introduced without being added to <see cref="ForDatabase"/>, which is what
/// <see cref="Backup.SqlitePoolCleaner"/> walks.</para>
///
/// <para><b>Build strings here, never inline.</b> A <c>new SqliteConnection(...)</c> against the
/// application database must take its string from this type. Formatting
/// <c>$"Data Source={path}"</c> at a call site silently creates a pool nothing clears, and the
/// consequence is platform-specific and silent on Linux: the swap succeeds, the stale pooled handle
/// keeps serving the REPLACED INODE, and the process carries on reading the old database while the
/// restored one sits on disk looking applied. See <see cref="RestoreService"/>'s remarks.</para>
///
/// <para>The log store is deliberately absent. It owns a SEPARATE FILE (arbitarr-logs.db, see
/// <c>LogStore.DatabaseFileName</c>) which a restore does not replace, so its pool must NOT be
/// cleared by a restore — clearing it would be over-broad, and over-broad is how
/// <c>ClearAllPools</c> caused arb-cbc/arb-5ba in the first place.</para>
/// </summary>
public static class DatabaseConnectionStrings
{
    /// <summary>
    /// The string the running application uses for every one of its own connections:
    /// <see cref="SqliteConnectionFactory"/> opens it, and
    /// <see cref="ArbitarrDbContextOptionsFactory"/> hands that open connection to EF, so every
    /// <see cref="ArbitarrDbContext"/> in the process inherits it. Carries
    /// <see cref="SqliteCacheMode.Default"/>, which the builder EMITS rather than eliding — that is
    /// what makes it a different pool from <see cref="Maintenance"/> despite naming the same file.
    /// </summary>
    public static string Application(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Default,
        }.ToString();

    /// <summary>
    /// The bare string used by the maintenance paths that open the database directly rather than
    /// through the factory: <see cref="Backup.BackupService"/>'s <c>SnapshotDatabase</c> source
    /// connection and its <c>ReadAppliedMigrationId</c> reader. No <c>Cache</c> keyword, so it
    /// renders as <c>Data Source={path}</c> and names a DIFFERENT pool from
    /// <see cref="Application"/>.
    /// </summary>
    public static string Maintenance(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
        }.ToString();

    /// <summary>
    /// The string for a backup snapshot's DESTINATION file — the fresh temp file
    /// <see cref="Backup.BackupService"/>'s <c>SnapshotDatabase</c> asks SQLite to write the backup
    /// into.
    ///
    /// <para><b>Deliberately NOT one of <see cref="ForDatabase"/>'s shapes.</b> It names a file a
    /// restore never replaces, so clearing its pool would be over-reach of exactly the kind this
    /// type exists to end.</para>
    ///
    /// <para>It lives here anyway, rather than as an inline builder in <c>BackupService</c>, so
    /// that <c>NoInlineDatabaseConnectionStringsTests</c> need not name <c>BackupService</c> among
    /// the types allowed to BUILD a connection string. That test keeps two separate lists —
    /// build-a-string and open-a-connection — precisely so a type trusted to open is not thereby
    /// trusted to invent the shape; keeping <c>BackupService</c> off the builder list means a
    /// future inline string there is still reported.</para>
    /// </summary>
    public static string SnapshotDestination(string destinationPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
        }.ToString();

    /// <summary>
    /// Every connection string this assembly can open against <paramref name="databasePath"/>.
    /// <see cref="Backup.SqlitePoolCleaner"/> clears the pool for each, which is what lets a
    /// restore swap the file with no surviving handle on it.
    ///
    /// <para><see cref="SnapshotDestination"/> is absent on purpose — see its own remarks: it names
    /// a fresh temp file, not the database being replaced.</para>
    ///
    /// <para>Adding a shape here is what makes it covered. Adding one anywhere else is the bug this
    /// type exists to prevent, and <c>NoInlineDatabaseConnectionStringsTests</c> (in
    /// <c>tests/Arbitarr.Architecture.Tests</c>) is what stops it silently: it reads this
    /// assembly's IL and fails any type outside its named allow-list that constructs a
    /// <c>SqliteConnectionStringBuilder</c> or a <c>SqliteConnection</c>.</para>
    /// </summary>
    public static IEnumerable<string> ForDatabase(string databasePath)
    {
        yield return Application(databasePath);
        yield return Maintenance(databasePath);
    }
}
