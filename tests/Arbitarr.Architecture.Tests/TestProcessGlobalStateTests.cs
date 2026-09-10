using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-rga.3: two process-global calls are BANNED from every test assembly, because each one
/// reaches outside the caller's own test and breaks whichever class happens to be running beside it
/// once the suite runs in parallel.
///
/// <list type="bullet">
/// <item><c>SqliteConnection.ClearAllPools()</c> force-closes every pooled connection in the
/// process, not just the caller's, which is what produced <c>ObjectDisposedException</c> from a
/// connection a neighbouring class had closed (arb-cbc, arb-5ba). The scoped replacements are
/// <c>Arbitarr.TestSupport.SqliteTestDatabase</c> and <c>Arbitarr.TestSupport.SqlitePools</c>.</item>
/// <item><c>Environment.SetEnvironmentVariable</c> mutates process-wide state, so two hosts
/// starting concurrently overwrite each other's configuration. The replacement is
/// <c>builder.UseSetting(...)</c> per host (arb-rga.2).</item>
/// </list>
///
/// <para><b>Why IL, not source or reflection.</b> A grep over source is defeated by an alias, a
/// fully-qualified name, or a wrapper; reflection over a loaded assembly can see the types a method
/// mentions but not the calls its body makes. Reading the IL with Cecil sees the actual
/// <c>call</c> instruction whatever the source spelled, so the ban cannot be evaded by rephrasing.
/// This is a ban with NO allow-list: a test that genuinely needs one of these has a design problem
/// an exemption would hide.</para>
///
/// <para><b>Scope: TEST IL only.</b> This scan covers the assemblies named below and nothing else.
/// Production code is scanned separately by <see cref="ProductionProcessGlobalStateTests"/>, which
/// bans <c>ClearAllPools</c> across <c>src/</c> — so between the two, no assembly in the solution
/// calls it. That production ban is new (arb-n21): the restore path used to call
/// <see cref="Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools"/> deliberately, because it had
/// to drop every handle before swapping the database file, and a test driving the restore therefore
/// reached a process-global clear at runtime no matter what this ban said. It now clears only the
/// pools naming the database being replaced, via <c>Arbitarr.Data.Backup.SqlitePoolCleaner</c>.
/// <c>Environment.SetEnvironmentVariable</c> stays banned here only: production configuration code
/// has legitimate reasons to set process environment, whereas two hosts starting concurrently in
/// ONE test process overwrite each other.</para>
///
/// <para><b>Why the assemblies are found by PATH.</b> Adding the test projects as ProjectReferences
/// here fails with NU1605, so the scan opens each test assembly's build output directly with
/// <see cref="ModuleDefinition.ReadModule(string)"/>. That makes the scan depend on those
/// assemblies having been BUILT before this project's tests run — <c>dotnet build</c> at the
/// solution level does that, and the CI job must keep doing so. A missing assembly therefore FAILS
/// this test loudly rather than passing vacuously over an empty set, which is the failure mode
/// CLAUDE.md section 4 exists to prevent.</para>
///
/// <para><b>Assembly names and the path walk</b> now live once, on <see cref="BuiltAssemblies"/>
/// (arb-hxa) — see its remarks for why the lists are spelled out and why they are independent of
/// build-test.yml's own TEST_ASSEMBLIES oracle.</para>
/// </summary>
public class TestProcessGlobalStateTests
{
    private static readonly string[] BannedMethods =
    [
        "Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools",
        "System.Environment::SetEnvironmentVariable",
    ];

    [Fact]
    public void No_test_assembly_calls_process_global_state_mutators()
    {
        var assemblyPaths = BuiltAssemblies.TestAssemblyNames
            .Select(name => (Name: name, Path: ResolveTestAssemblyPath(name)))
            .ToArray();

        // Loud failure, not a vacuous pass: if the scan cannot see an assembly, that is not
        // evidence the assembly is clean.
        var missing = assemblyPaths.Where(a => a.Path is null).Select(a => a.Name).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Could not locate the built assembly for: {string.Join(", ", missing)}. " +
            "This scan reads IL from each test project's build output, so every test project must " +
            "be built before Arbitarr.Architecture.Tests runs (see the class remarks). Run " +
            "'dotnet build' at the solution level first; in CI, the job must build the solution " +
            "before running this project.");

        var violations = new List<string>();

        foreach (var (name, path) in assemblyPaths)
        {
            foreach (var violation in FindBannedCalls(path!))
            {
                violations.Add($"{name}: {violation}");
            }
        }

        // The bait below is this assembly's own deliberate, never-executed sample of both banned
        // calls; it is what proves the scan can see them at all, so it is not a violation. Matched
        // on the exact "<assembly>: <bait type>." prefix rather than by substring, so this cannot
        // become a back-door exemption for some other type that merely contains the bait's name.
        var baitPrefix = $"Arbitarr.Architecture.Tests: {BaitTypeName}.";
        violations.RemoveAll(v => v.StartsWith(baitPrefix, StringComparison.Ordinal));

        Assert.True(
            violations.Count == 0,
            "No test assembly may call process-global state mutators — they reach outside the " +
            "caller's own test and break whichever class runs beside it (see the class remarks for " +
            "the scoped replacements). Found:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves the scan is NON-VACUOUS: it must actually find the banned calls when they are
    /// present. Without this, <see cref="No_test_assembly_calls_process_global_state_mutators"/>
    /// would pass just as happily if the IL walk were broken, matched nothing, or read the wrong
    /// member — the exact shape CLAUDE.md section 4 records as having shipped three times.
    ///
    /// <para>The bait is <see cref="Bait"/> in this assembly, whose two methods call the two banned
    /// members. It is never executed; only its IL is read.</para>
    /// </summary>
    [Fact]
    public void The_scan_detects_the_banned_calls_when_they_are_present()
    {
        var path = ResolveTestAssemblyPath("Arbitarr.Architecture.Tests");
        Assert.NotNull(path);

        // FindBannedCalls returns "<type>.<method> calls <banned>", so the bait's own findings are
        // the ones starting with its exact type name.
        var baitViolations = FindBannedCalls(path!)
            .Where(v => v.StartsWith(BaitTypeName + ".", StringComparison.Ordinal))
            .ToArray();

        // Both bans, each proven detectable by the same walk the real test uses. Asserting only
        // that "something was found" would still pass if one of the two patterns never matched.
        foreach (var banned in BannedMethods)
        {
            Assert.Contains(baitViolations, v => v.EndsWith(banned, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The bait type as CECIL spells it. Reflection renders a nested type with a '+' separator
    /// (<c>Outer+Bait</c>) while Cecil's <c>TypeDefinition.FullName</c> uses '/' — so comparing the
    /// reflected name against a scanned one silently never matches, and every bait call would be
    /// reported as a real violation while the non-vacuity proof found nothing. Derived from
    /// <c>typeof</c> rather than written out so a rename cannot leave this behind.
    /// </summary>
    private static string BaitTypeName => typeof(Bait).FullName!.Replace('+', '/');

    /// <summary>
    /// Walks every method body in the assembly and reports each call to a banned member, as
    /// "&lt;type&gt;.&lt;method&gt; calls &lt;banned&gt;".
    /// </summary>
    private static IEnumerable<string> FindBannedCalls(string assemblyPath) =>
        FindBannedCalls(assemblyPath, BannedMethods);

    /// <summary>
    /// The same walk, over a caller-chosen ban list. Shared with
    /// <see cref="ProductionProcessGlobalStateTests"/> rather than duplicated: that scan bans a
    /// SUBSET of these members (production may legitimately set an environment variable), and a
    /// second copy of the IL walk would be a second thing to keep correct — with the copy that
    /// drifted silently finding nothing and reporting a clean tree.
    /// </summary>
    internal static IEnumerable<string> FindBannedCalls(string assemblyPath, params string[] bannedMethods)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    if (instruction.Operand is not MethodReference called)
                    {
                        continue;
                    }

                    var fullName = $"{called.DeclaringType.FullName}::{called.Name}";
                    if (bannedMethods.Contains(fullName, StringComparer.Ordinal))
                    {
                        yield return $"{type.FullName}.{method.Name} calls {fullName}";
                    }
                }
            }
        }
    }

    /// <summary>
    /// Bait for <see cref="The_scan_detects_the_banned_calls_when_they_are_present"/>: the only
    /// sanctioned calls to the banned members anywhere in the test tree, present so the scan can be
    /// proven to see them. NEVER CALLED — the always-false guard makes that structural rather than
    /// a promise, so this cannot be mistaken for a usable helper.
    /// </summary>
    private static class Bait
    {
        public static void ClearsAllPools()
        {
            if (AlwaysFalse)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }
        }

        public static void SetsAnEnvironmentVariable()
        {
            if (AlwaysFalse)
            {
                Environment.SetEnvironmentVariable("ARBITARR_NEVER_SET", null);
            }
        }

        // Deliberately not a const: a const would let the compiler drop the bodies above, and the
        // bait's IL would vanish along with the proof it exists to provide.
        private static bool AlwaysFalse => bool.Parse(bool.FalseString);
    }

    /// <summary>
    /// Thin forwarder to <see cref="BuiltAssemblies.ResolveTestAssemblyPath"/> — kept under this
    /// name to avoid churning every consumer (<see cref="QuarantineTraitTests"/>,
    /// <see cref="ProductionProcessGlobalStateTests"/>, <see cref="NoInlineDatabaseConnectionStringsTests"/>
    /// and this class's own tests). The walk itself lives once, on <see cref="BuiltAssemblies"/>
    /// (arb-hxa) — see its remarks for the five-parent depth and why the assemblies are found by
    /// PATH rather than by ProjectReference.
    /// </summary>
    internal static string? ResolveTestAssemblyPath(string assemblyName) =>
        BuiltAssemblies.ResolveTestAssemblyPath(assemblyName);

    /// <summary>
    /// Thin forwarder to <see cref="BuiltAssemblies.ResolveAssemblyPath"/> — kept under this name to
    /// avoid churning every consumer. See <see cref="BuiltAssemblies"/> for the walk itself.
    /// </summary>
    internal static string? ResolveAssemblyPath(string rootDirectoryName, string assemblyName) =>
        BuiltAssemblies.ResolveAssemblyPath(rootDirectoryName, assemblyName);
}
