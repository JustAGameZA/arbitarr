using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-n21: no PRODUCTION assembly may call <c>SqliteConnection.ClearAllPools()</c>.
///
/// <para><b>Why this is a separate ban from the test-assembly one.</b>
/// <see cref="TestProcessGlobalStateTests"/> bans the same call in TEST IL, and its class remarks
/// used to record production's restore path as a deliberate, permitted exception — it force-closed
/// every pooled connection in the process because it had to drop every handle on
/// <c>arbitarr.db</c> before swapping the file. That exception is now closed:
/// <c>Arbitarr.Data.Backup.SqlitePoolCleaner</c> clears exactly the pools that name the database
/// being replaced, so the restore no longer needs a process-global hammer — and a test that drives
/// the restore path no longer reaches one at runtime.</para>
///
/// <para>Without this test that closure is a fact about today's source, not a rule. The next
/// restore-adjacent change reaches for <c>ClearAllPools</c> exactly as readily as the last one did,
/// and the test-side ban cannot see it: it scans test IL only. The over-broad clear is what
/// produced arb-cbc/arb-5ba, and it re-enters the process through production just as easily as
/// through a test.</para>
///
/// <para><b>Scope is deliberately narrow.</b> Only <c>ClearAllPools</c>, and only <c>src/</c>.
/// <c>Environment.SetEnvironmentVariable</c> is NOT banned here — production configuration code has
/// legitimate reasons to set process environment, and the test-side ban exists because two hosts
/// starting concurrently in ONE process overwrite each other, which is a test-host problem.</para>
///
/// <para><b>The assembly list</b> now lives on <see cref="BuiltAssemblies.ProductionAssemblyNames"/>
/// (arb-hxa). It is DELIBERATELY INDEPENDENT of build-test.yml's own <c>TEST_ASSEMBLIES</c> token —
/// that workflow's guards job diffs its own list against this project's reference graph as a
/// separate oracle, and the two are not merged into one source of truth on purpose (a drift between
/// them is exactly what that guard exists to catch). If the workflow's guard step targets this file
/// by name or line shape, and this array has since moved, the guard's extraction must be repointed
/// at <see cref="BuiltAssemblies"/> in the same change or it fails to find the array at all.</para>
/// </summary>
public class ProductionProcessGlobalStateTests
{
    private const string BannedMethod = "Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools";

    [Fact]
    public void No_production_assembly_clears_every_connection_pool_in_the_process()
    {
        var assemblyPaths = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => (Name: name, Path: TestProcessGlobalStateTests.ResolveAssemblyPath("src", name)))
            .ToArray();

        // Loud failure, not a vacuous pass: an assembly the scan cannot open is not an assembly it
        // has found to be clean.
        var missing = assemblyPaths.Where(a => a.Path is null).Select(a => a.Name).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Could not locate the built assembly for: {string.Join(", ", missing)}. " +
            "This scan reads IL from each production project's build output, so the solution must " +
            "be built before Arbitarr.Architecture.Tests runs. Run 'dotnet build' at the solution " +
            "level first; in CI, the job must build the solution before running this project.");

        var violations = assemblyPaths
            .SelectMany(a => TestProcessGlobalStateTests
                .FindBannedCalls(a.Path!, BannedMethod)
                .Select(v => $"{a.Name}: {v}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "No production assembly may call SqliteConnection.ClearAllPools(): it force-closes " +
            "every pooled connection in the process, including the log store's and any other " +
            "database's, which is what produced arb-cbc/arb-5ba. Clear the pools for one database " +
            "with Arbitarr.Data.Backup.SqlitePoolCleaner.ClearPoolsFor(databasePath) instead. " +
            "Found:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves this scan is NON-VACUOUS: the same walk, over an assembly that DOES contain the call,
    /// must report it. Without this, the assertion above would pass just as happily if the IL walk
    /// were broken, if the banned name were misspelled, or if every path resolved to something with
    /// no method bodies — the shape CLAUDE.md section 4 records as having shipped three times.
    ///
    /// <para>The bait is <c>TestProcessGlobalStateTests.Bait</c>, which already exists for the
    /// test-side ban and calls <c>ClearAllPools</c> from a never-executed body. Reused rather than
    /// duplicated into a production assembly, because a second bait would mean shipping a real call
    /// to the banned member inside <c>src/</c> — the exact thing this test forbids. The bait lives
    /// in a test assembly and this scan never looks at test assemblies, so the two cannot collide.
    /// </para>
    /// </summary>
    [Fact]
    public void The_scan_detects_the_banned_call_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = TestProcessGlobalStateTests.FindBannedCalls(baitAssembly!, BannedMethod).ToArray();

        Assert.Contains(found, v => v.EndsWith(BannedMethod, StringComparison.Ordinal));
    }
}
