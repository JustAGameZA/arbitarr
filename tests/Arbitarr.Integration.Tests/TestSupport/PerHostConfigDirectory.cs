namespace Arbitarr.Integration.Tests.TestSupport;

/// <summary>
/// arb-j4hq: a FRESH <c>/config</c> directory, and therefore a fresh <c>arbitarr.db</c>, for each
/// <c>WebApplicationFactory</c> a test class builds.
///
/// <para><b>The problem this removes.</b> Several classes here build more than one host and point
/// every one of them at a single per-class directory, collecting the factories in a list that
/// nothing disposes until class teardown. Each host starts roughly eight hosted services against
/// the database in its config directory, so those hosts' background workers all run against ONE
/// SQLite file for the lifetime of the class. Under load an earlier host's worker can hold the
/// write lock past the 3000 ms <c>busy_timeout</c> and a later host's request answers 500 — a
/// failure that reads as a bug in the route under test rather than as contention between two hosts
/// that were never meant to share a store.</para>
///
/// <para><b>A subdirectory per host, NOT disposing the previous factory at the top of the class's
/// host builder.</b> Eager disposal would also work, but it makes each host's isolation depend on
/// call ORDER, and it silently forbids a test that legitimately needs two live hosts at once.
/// Separate directories make the isolation structural: two hosts cannot contend for a file they do
/// not share, in any order, concurrently or not.</para>
///
/// <para><b>Nested under the class's existing root rather than minted beside it</b>, so teardown
/// still has ONE path to hand <see cref="ConfigDirectoryTeardown.Delete"/>: that helper deletes
/// recursively, and a per-host directory minted at the temp root would leak one directory per host
/// instead of the per-class one this replaces.</para>
///
/// <para><b>Not for a class whose hosts must SHARE a store.</b>
/// <c>ReleaseLookupSurvivesRestartAndTtlTests</c> brings a second host up on the first's database
/// deliberately, because that is what makes its "restart" a restart rather than a fresh install.
/// Giving it a directory per host would not fix a flake there; it would delete the behaviour under
/// test.</para>
///
/// <para><c>SqliteConnection.ClearAllPools</c> is deliberately NOT part of any of this. It is
/// process-global and would reach into every other test class running in parallel; see
/// <see cref="ConfigDirectoryTeardown"/>.</para>
/// </summary>
internal static class PerHostConfigDirectory
{
    /// <summary>
    /// Creates and returns a new <see cref="Guid"/>-named subdirectory of <paramref name="root"/>.
    /// Created eagerly rather than left to the host: the composition root reads the path during
    /// startup, and a missing directory there fails as a startup error rather than as the isolation
    /// problem it actually is.
    /// </summary>
    internal static string Create(string root)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
