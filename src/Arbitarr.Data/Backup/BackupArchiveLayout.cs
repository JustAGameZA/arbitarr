namespace Arbitarr.Data.Backup;

/// <summary>
/// The names of the two entries inside a backup archive, and the rule for what a backup covers.
///
/// <para><b>WHAT IS IN THE ARCHIVE, AND WHY NOT MORE (#56).</b> Exactly two entries: a consistent
/// snapshot of <c>arbitarr.db</c> and the raw <c>release-guid-secret.key</c>. The plan's §2 settles
/// the second: a database restored onto a box with a different HMAC secret is a working
/// configuration with a broken GUID history, so a database-only backup would look like a complete
/// safety net and would not be one.</para>
///
/// <para><b>THE APPLICATION LOG DATABASE IS DELIBERATELY EXCLUDED.</b> There are TWO SQLite files
/// under the config directory, not one — this snapshot's <c>arbitarr.db</c> and the separate log
/// store named by <see cref="Arbitarr.Data.Logging.LogStore.DatabaseFileName"/>. Anything that
/// enumerates the config directory's stores must grep for that constant rather than for
/// "arbitarr.db", or it silently covers only half the data. Here the omission is the decision, not
/// an oversight:
///
///   - The two files were split precisely so a configuration backup does not drag log contents
///     along (see LogStore's own remarks and the Program.cs registration comment).
///   - Logs are not configuration. Restoring them would restore nothing an operator asked for.
///   - Application logs are an unvetted text surface — exception messages, paths, and internal
///     names nobody composed for an audience — and this archive is already a credential-bearing
///     file. Adding an unbounded quantity of unvetted text to a file operators will move around
///     on USB sticks widens a blast radius for no recovery benefit.
///
/// If a future feature ever needs the log store exported, that is a support-bundle export and a
/// different artefact with different handling — not an extra entry here.</para>
/// </summary>
public static class BackupArchiveLayout
{
    /// <summary>The archive entry holding the consistent snapshot of the configuration database.</summary>
    public const string DatabaseEntryName = "arbitarr.db";

    /// <summary>
    /// The archive entry holding the per-instance HMAC secret. Matches the on-disk file name
    /// <c>ReleaseGuidSecretFile</c> uses, so restore writes it straight back beside the database.
    /// </summary>
    public const string SecretKeyEntryName = "release-guid-secret.key";

    /// <summary>
    /// A small text entry recording which EF Core migration the snapshot was taken at, so restore
    /// can refuse an archive from a NEWER build before touching anything (plan §3.2). Read from
    /// the snapshot's own <c>__EFMigrationsHistory</c> table rather than trusted from this file —
    /// this entry is a human-readable convenience and is never the authority.
    /// </summary>
    public const string ManifestEntryName = "arbitarr-backup.txt";
}
