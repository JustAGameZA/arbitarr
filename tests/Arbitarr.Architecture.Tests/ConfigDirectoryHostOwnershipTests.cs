using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-1lt8: inside <c>Arbitarr.Integration.Tests</c>, a type may not BOTH take the shared
/// <c>WebApplicationFactory&lt;Program&gt;</c> fixture AND delete a config directory.
///
/// <para><b>The hazard.</b> A host derived with <c>WithWebHostBuilder</c> from a shared
/// <c>IClassFixture&lt;WebApplicationFactory&lt;Program&gt;&gt;</c> belongs to the ROOT factory, not
/// to the deriving class: xunit owns the shared fixture and disposes it when the whole assembly
/// finishes, so the derived host is still running — still holding open file handles under the
/// config directory — while the class's own disposal runs. Deleting the directory there removes
/// files a live host holds, and <c>ConfigDirectoryTeardown.Delete</c> throws by design, failing
/// whichever test happened to be disposing rather than the one that caused it. That is the arb-rwhb
/// hazard; #281 (arb-gphi) removed the last eight instances of it, and this scan is what stops a
/// ninth. The rule it enforces is written up under
/// <c>docs/standards/process.md</c> → "A class that creates a config directory owns the host".</para>
///
/// <para><b>Per-test <c>WithWebHostBuilder</c> on an OWNED root stays legal</b>, and this scan is
/// shaped so that it is: the ban keys on where the ROOT came from (an injected shared fixture),
/// never on <c>WithWebHostBuilder</c> itself. A class that builds its own
/// <c>ArbitarrWebApplicationFactory.OverConfigDirectory</c> root and derives per-test hosts from it
/// owns every one of them, awaits the root's <c>DisposeAsync</c> from xunit's
/// <c>IAsyncLifetime</c>, and then deletes — which is the sanctioned shape, and which this scan
/// must not flag. <c>CategoryParamCapTests</c> is the worked example.</para>
///
/// <para><b>Why IL, not source.</b> A grep for <c>IClassFixture&lt;WebApplicationFactory&lt;Program&gt;&gt;</c>
/// is defeated by a <c>using</c> alias, a fully-qualified spelling, or a generic base class that
/// carries the fixture on the test class's behalf; a grep for the delete is defeated by a wrapper.
/// Cecil reads the interface rows, the constructor signatures and the actual <c>call</c>
/// instruction whatever the source spelled, so neither half can be evaded by rephrasing.</para>
///
/// <para><b>This ban has NO allow-list.</b> A class that genuinely needs to delete a config
/// directory needs to own the host writing into it, which is a change to the class rather than an
/// exemption here — an exemption would preserve exactly the teardown fault the scan exists to
/// detect. Note in particular that the fix is never to add the class to
/// <see cref="ProductionProcessGlobalStateTests"/>'s allow-list, which governs an unrelated
/// ban.</para>
///
/// <para><b>Scope: Arbitarr.Integration.Tests only.</b> It is the only assembly that hosts the
/// application over a real config directory; <c>ConfigDirectoryTeardown</c> is <c>internal</c> to
/// it, so no other assembly can reach the banned half at all. The assembly is opened by file path
/// via <see cref="BuiltAssemblies"/> for the reason recorded there (NU1605), which means a missing
/// build output FAILS this scan loudly rather than passing vacuously over an empty set.</para>
/// </summary>
public class ConfigDirectoryHostOwnershipTests
{
    private const string ScannedAssemblyName = "Arbitarr.Integration.Tests";

    /// <summary>
    /// The SHARED fixture type, as Cecil spells a closed generic. A class that takes this either as
    /// an <c>IClassFixture&lt;&gt;</c> argument or as a constructor parameter is being handed a host
    /// it does not own. <c>ArbitarrWebApplicationFactory</c> is deliberately NOT here: an
    /// <c>IClassFixture&lt;ArbitarrWebApplicationFactory&gt;</c> is also shared, but such a class
    /// does not create the config directory — the factory does, and the factory deletes its own on
    /// disposal via <c>TryDelete</c>. The hazard is specifically a class deleting a directory
    /// underneath a host owned by someone else.
    /// </summary>
    private const string SharedFixtureTypeName =
        "Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory`1<Program>";

    /// <summary>
    /// Both entry points of the teardown helper. <c>TryDelete</c> is banned alongside
    /// <c>Delete</c> because the two differ only in whether the failure throws: swapping to the
    /// non-throwing one turns the teardown fault SILENT rather than fixing it, which is the more
    /// dangerous of the two and exactly the "fix" a failing <c>Delete</c> invites.
    /// <c>ConfigDirectoryTeardown.ClearPools</c> is absent on purpose — clearing pooled handles
    /// under a live host is harmless (the host reopens), and it is what the caller-supplied-directory
    /// host does legitimately.
    /// </summary>
    private static readonly string[] BannedDeleteCalls =
    [
        "Arbitarr.Integration.Tests.TestSupport.ConfigDirectoryTeardown::Delete",
        "Arbitarr.Integration.Tests.TestSupport.ConfigDirectoryTeardown::TryDelete",
    ];

    [Fact]
    public void No_integration_test_type_deletes_a_config_directory_under_a_shared_fixture_host()
    {
        var path = BuiltAssemblies.ResolveTestAssemblyPath(ScannedAssemblyName);

        // Loud failure, not a vacuous pass: if the scan cannot see the assembly, that is not
        // evidence the assembly is clean.
        Assert.True(
            path is not null,
            $"Could not locate the built assembly for {ScannedAssemblyName}. This scan reads IL from " +
            "that project's build output, so it must be built before Arbitarr.Architecture.Tests " +
            "runs (see the class remarks). Run 'dotnet build' at the solution level first.");

        var violations = FindViolations(path!);

        Assert.True(
            violations.Count == 0,
            "A test class that deletes a config directory must OWN the host writing into it. These " +
            "types take the shared WebApplicationFactory<Program> fixture — whose host xunit keeps " +
            "alive until the assembly finishes — and delete a config directory anyway, so the delete " +
            "runs under a live host and faults whichever test is disposing. Give the class its own " +
            "ArbitarrWebApplicationFactory.OverConfigDirectory root, implement xunit's IAsyncLifetime, " +
            "and await the root's DisposeAsync before deleting; do not exempt it. Found:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves the scan is NON-VACUOUS (CLAUDE.md §4): it must actually flag the banned shape when it
    /// is present. Without this, the scan above would pass just as happily if the interface walk
    /// matched nothing, the constructor walk read the wrong parameter, or the generic type name were
    /// spelled the way reflection renders it rather than the way Cecil does — and "zero violations"
    /// on a clean master is precisely the reading that cannot tell those apart.
    ///
    /// <para>The bait is <see cref="Bait"/> in THIS assembly: a type that both takes the shared
    /// fixture and calls the delete. Because Arbitarr.Architecture.Tests cannot reference
    /// Arbitarr.Integration.Tests (see the csproj), the bait cannot call the real helper or take the
    /// real fixture type — so the walk is run over this assembly against a bait-shaped pair of NAMES
    /// (<see cref="Bait"/>'s own stand-ins), exercising the same interface/constructor/call-instruction
    /// matching the real scan uses. What is proven is the DETECTION; the real names are constants
    /// checked by <see cref="The_banned_names_match_the_real_types"/> below.</para>
    /// </summary>
    [Fact]
    public void The_scan_detects_the_banned_shape_when_it_is_present()
    {
        var path = BuiltAssemblies.ResolveTestAssemblyPath("Arbitarr.Architecture.Tests");
        Assert.NotNull(path);

        var found = FindViolations(path!, Bait.FixtureTypeName, Bait.DeleteCallName);

        // Attributable to the bait by its exact type name plus a separator, not by substring, so
        // this cannot become a back-door exemption for some other type. The separator may be '.'
        // (the call sits on the bait itself) or '/' (it sits in the bait's nested async
        // state machine, which is where the compiler actually puts it) — both are the bait, and
        // accepting only the first would fail the moment the bait is written in the realistic async
        // shape.
        var baitName = typeof(Bait).FullName!.Replace('+', '/');
        Assert.Contains(
            found,
            v => v.StartsWith(baitName + ".", StringComparison.Ordinal)
                 || v.StartsWith(baitName + "/", StringComparison.Ordinal));

        // The bait's sibling takes the fixture but does NOT delete; it must NOT be reported, which is
        // what proves the scan requires BOTH halves rather than flagging every fixture consumer.
        var innocentName = typeof(InnocentBait).FullName!.Replace('+', '/');
        Assert.DoesNotContain(
            found,
            v => v.StartsWith(innocentName + ".", StringComparison.Ordinal)
                 || v.StartsWith(innocentName + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The bait stands in for the real types by NAME, so the names themselves are the one thing its
    /// detection proof cannot check. This closes that gap: it reads the scanned assembly's IL and
    /// asserts both real names resolve to something there. A rename or a move on either side would
    /// otherwise leave <see cref="BannedDeleteCalls"/> or <see cref="SharedFixtureTypeName"/>
    /// matching nothing and the scan passing over a tree it can no longer see — indistinguishable,
    /// from the outside, from the clean tree it is supposed to be reporting.
    ///
    /// <para>The two halves are anchored DIFFERENTLY on purpose, because a clean tree contains one
    /// and not the other: the helper is called (by classes that own their host), while no type takes
    /// the shared fixture at all — that absence is the rule holding. So the fixture name is anchored
    /// on field types, which the legal shape produces, rather than on the banned shape.</para>
    /// </summary>
    [Fact]
    public void The_banned_names_match_the_real_types()
    {
        var path = BuiltAssemblies.ResolveTestAssemblyPath(ScannedAssemblyName);
        Assert.NotNull(path);

        using var module = ModuleDefinition.ReadModule(path!);

        var calledNames = module.GetTypes()
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => i.Operand as MethodReference)
            .Where(r => r is not null)
            .Select(r => $"{r!.DeclaringType.FullName}::{r.Name}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var banned in BannedDeleteCalls)
        {
            Assert.True(
                calledNames.Contains(banned),
                $"'{banned}' is called nowhere in {ScannedAssemblyName}. Either the helper moved or " +
                "was renamed — in which case update BannedDeleteCalls, because the scan is currently " +
                "matching nothing — or it genuinely has no callers, in which case delete it.");
        }

        // NOT "some type TAKES the fixture": since #281 none does, and that is the state this scan
        // exists to preserve — an assertion that a violation-shaped consumer exists would fail
        // exactly when the tree is correct. What is checked instead is that the NAME still resolves
        // to something in this assembly's IL, which the legal shape supplies in quantity: a class
        // that owns its root holds it in a field typed WebApplicationFactory<Program>.
        var fieldTypeNames = module.GetTypes()
            .SelectMany(t => t.Fields)
            .Select(f => f.FieldType.FullName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            fieldTypeNames.Contains(SharedFixtureTypeName),
            $"'{SharedFixtureTypeName}' is the type of no field in {ScannedAssemblyName}, so " +
            "SharedFixtureTypeName is almost certainly matching nothing and the scan is vacuous. " +
            "Cecil spells a closed generic 'Namespace.Name`1<Arg>', which is NOT how reflection " +
            "renders it — check that spelling first, then whether the type was renamed or moved.");
    }

    /// <summary>
    /// Reports each type that has BOTH halves, as "&lt;type&gt; takes &lt;fixture&gt; and calls
    /// &lt;delete&gt;". Parameterised on the two names so the bait can exercise the identical walk
    /// against its own stand-ins.
    /// </summary>
    private static List<string> FindViolations(string assemblyPath) =>
        FindViolations(assemblyPath, SharedFixtureTypeName, BannedDeleteCalls);

    private static List<string> FindViolations(
        string assemblyPath,
        string fixtureTypeName,
        params string[] bannedDeleteCalls)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        var violations = new List<string>();

        foreach (var type in module.GetTypes())
        {
            if (!TakesTheSharedFixture(type, fixtureTypeName))
            {
                continue;
            }

            foreach (var deleteCall in FindDeleteCalls(type, bannedDeleteCalls))
            {
                violations.Add($"{deleteCall} (on {type.FullName}, which takes {fixtureTypeName})");
            }
        }

        return violations;
    }

    private static bool TakesTheSharedFixture(TypeDefinition type) =>
        TakesTheSharedFixture(type, SharedFixtureTypeName);

    /// <summary>
    /// True when the type is HANDED the fixture rather than creating one: either via an
    /// <c>IClassFixture&lt;T&gt;</c> interface row (xunit's own injection) or via a constructor
    /// parameter of that type (the same host arriving by hand, or from a collection fixture). Both
    /// are checked because either alone leaves the other spelling undetected, and they are genuinely
    /// separate shapes in the IL — the interface row does not imply a matching parameter.
    /// </summary>
    private static bool TakesTheSharedFixture(TypeDefinition type, string fixtureTypeName)
    {
        var viaClassFixture = type.Interfaces.Any(i =>
            i.InterfaceType is GenericInstanceType generic
            && generic.ElementType.FullName == "Xunit.IClassFixture`1"
            && generic.GenericArguments.Any(a => a.FullName == fixtureTypeName));

        var viaConstructor = type.Methods
            .Where(m => m.IsConstructor)
            .SelectMany(m => m.Parameters)
            .Any(p => p.ParameterType.FullName == fixtureTypeName);

        return viaClassFixture || viaConstructor;
    }

    /// <summary>
    /// Every banned delete call the type makes, as "&lt;type&gt;.&lt;method&gt; calls
    /// &lt;banned&gt;". Scoped to the type rather than reusing
    /// <see cref="TestProcessGlobalStateTests.FindBannedCalls(string, string[])"/> because this ban
    /// is not "nobody calls this" but "not this type, given what else it does" — the walk has to
    /// attribute each call to the declaring type to pair it with the fixture half.
    ///
    /// <para><b>Nested compiler-generated types are walked as part of their declaring type, and that
    /// is load-bearing (measured, not assumed).</b> A correct teardown is <c>async Task
    /// DisposeAsync()</c>, and the C# compiler moves an async body into a generated state-machine
    /// type nested under the class — so the <c>call</c> to the delete helper lives in
    /// <c>&lt;DisposeAsync&gt;d__N.MoveNext</c>, NOT in any method the class itself declares. A walk
    /// over <c>type.Methods</c> alone therefore finds nothing on precisely the shape this scan
    /// exists to catch: mutation-testing the banned shape onto <c>CategoryParamCapTests</c> (whose
    /// teardown is async, as every correct one is) produced a PASS before this recursion was added.
    /// Local functions and lambdas land in the same place for the same reason.</para>
    /// </summary>
    private static IEnumerable<string> FindDeleteCalls(TypeDefinition type, string[] bannedDeleteCalls)
    {
        foreach (var nested in type.NestedTypes)
        {
            foreach (var found in FindDeleteCalls(nested, bannedDeleteCalls))
            {
                yield return found;
            }
        }

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
                if (bannedDeleteCalls.Contains(fullName, StringComparer.Ordinal))
                {
                    yield return $"{type.FullName}.{method.Name} calls {fullName}";
                }
            }
        }
    }

    /// <summary>
    /// The banned shape, in miniature: a type that takes a shared fixture via
    /// <c>IClassFixture&lt;T&gt;</c> AND calls a delete helper. Its stand-in fixture and helper are
    /// local types, because this assembly cannot reference Arbitarr.Integration.Tests — what the
    /// bait proves is that the walk pairs the two halves on one type and reports it.
    /// NEVER EXECUTED: only its IL is read.
    /// </summary>
    private sealed class Bait : Xunit.IClassFixture<Bait.StandInFixture>
    {
        internal static string FixtureTypeName => typeof(StandInFixture).FullName!.Replace('+', '/');

        internal static string DeleteCallName =>
            typeof(StandInTeardown).FullName!.Replace('+', '/') + "::Delete";

        /// <summary>
        /// <c>async</c> deliberately, matching the real shape: a correct teardown is
        /// <c>async Task DisposeAsync()</c>, so the compiler moves this body into a nested
        /// state-machine type and the delete call is NOT in a method this type declares. Making the
        /// bait async is what keeps <see cref="FindDeleteCalls"/>'s nested-type recursion proven —
        /// with a synchronous bait the scan passed its own non-vacuity test while missing every real
        /// offender. NEVER EXECUTED.
        /// </summary>
        public async Task DisposesLikeTheHazard()
        {
            if (AlwaysFalse)
            {
                await Task.Yield();
                StandInTeardown.Delete("never");
            }
        }

        // Deliberately not a const: a const would let the compiler drop the body above, and the
        // bait's IL would vanish along with the proof it exists to provide.
        private static bool AlwaysFalse => bool.Parse(bool.FalseString);

        internal sealed class StandInFixture;

        internal static class StandInTeardown
        {
            public static void Delete(string configDirectory) => _ = configDirectory;
        }
    }

    /// <summary>
    /// Takes the same stand-in fixture but deletes nothing — the negative half of the proof. Without
    /// it, a walk that reported EVERY fixture consumer would still satisfy
    /// <see cref="The_scan_detects_the_banned_shape_when_it_is_present"/> while flagging every legal
    /// class on master. NEVER EXECUTED.
    /// </summary>
    private sealed class InnocentBait : Xunit.IClassFixture<Bait.StandInFixture>;
}
