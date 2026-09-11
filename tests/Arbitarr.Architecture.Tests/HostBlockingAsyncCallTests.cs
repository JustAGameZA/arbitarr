using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-zwk (root cause of the arb-agh flake): <c>Arbitarr.Host</c> may not block a thread on an
/// async call — <c>.GetAwaiter().GetResult()</c>, <c>.Result</c>, or <c>.Wait()</c>.
///
/// <para><b>Why this needs a ban and not just a fix.</b> The composition root read the release-lookup
/// TTL with <c>GetReleaseLookupTtlAsync().GetAwaiter().GetResult()</c> inside a SCOPED factory, so
/// every search blocked a thread-pool thread on a database round trip before it could start. Under
/// load that starves the pool, which is how it surfaced: an intermittent, unattributable timeout
/// (arb-agh) rather than an obvious error. The call has been removed, but nothing stopped the next
/// registration from reaching for the same shape — a DI factory delegate cannot be <c>async</c>, so
/// blocking is the path of least resistance exactly where it hurts most.</para>
///
/// <para><b>Why IL rather than a grep</b>, following <see cref="TestProcessGlobalStateTests"/>'s
/// reasoning: a grep is defeated by an alias, a fully-qualified name, or a helper wrapping the call.
/// Reading the IL sees the instruction whatever the source spelled.</para>
///
/// <para><b>Why compiler-generated types are skipped, and why that does not weaken the ban.</b> A
/// real <c>await</c> compiles into a state machine that calls <c>GetResult()</c> on its awaiter, so
/// scanning every method would flag every async method in the assembly. Those state machines are
/// emitted as nested types whose names contain <c>&lt;</c>, which no C# identifier can, so skipping
/// them excludes exactly the generated code and nothing an author wrote. The same marker excludes
/// the synthetic entry point: a file using top-level statements compiles to a synchronous
/// <c>Main</c> that blocks on the generated <c>&lt;Main&gt;$</c> body, which cannot be avoided
/// because the process has no caller to yield to.</para>
///
/// <para><b>The exclusion is verified, not assumed.</b> The DI factory that caused arb-agh lived in
/// a lambda, which the compiler emits as an ordinary NAMED method on a closure type — no angle
/// brackets — so the original defect is still caught. <see cref="Bait"/> reproduces that exact
/// shape (a blocking call inside a factory delegate), so the non-vacuity test below fails if this
/// exclusion is ever widened enough to swallow it.</para>
///
/// <para><b>Scope is deliberately narrow: Arbitarr.Host only.</b> That is where DI factories live and
/// where the defect was. Blocking in a console entry point or a migration tool is a different
/// trade-off, and widening this ban to all of <c>src/</c> without reviewing those callers would be a
/// change to a rule rather than the enforcement of one.</para>
/// </summary>
public class HostBlockingAsyncCallTests
{
    private const string HostAssembly = "Arbitarr.Host";

    /// <summary>
    /// The blocking shapes, by the member the IL actually calls. <c>GetResult</c> covers
    /// <c>.GetAwaiter().GetResult()</c>; <c>get_Result</c> covers <c>.Result</c>; <c>Wait</c> covers
    /// <c>.Wait()</c>.
    /// </summary>
    private static readonly string[] BannedMembers =
    [
        "GetResult",
        "get_Result",
        "Wait",
    ];

    /// <summary>Awaiter and task types whose blocking members are the ones worth banning.</summary>
    private static bool IsAsyncResultType(TypeReference type)
    {
        var name = type.FullName;
        return name.StartsWith("System.Runtime.CompilerServices.TaskAwaiter", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.ConfiguredTaskAwaitable", StringComparison.Ordinal)
            || name.StartsWith("System.Runtime.CompilerServices.ValueTaskAwaiter", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal)
            || name.StartsWith("System.Threading.Tasks.ValueTask", StringComparison.Ordinal);
    }

    internal static IEnumerable<string> FindBlockingCalls(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            // Exclude ONLY async state machines — the types the compiler emits for `await`, which
            // necessarily call GetResult() on their awaiter. They are identified by the interface
            // they implement, not by their name: a name-shaped filter ('<' anywhere) also skips
            // CLOSURE types, and a factory lambda lives on a closure — which is exactly where the
            // arb-agh defect was. Excluding by name made this scan blind to the shape it exists to
            // catch; the lambda bait proves it.
            if (type.Interfaces.Any(i =>
                    i.InterfaceType.FullName == "System.Runtime.CompilerServices.IAsyncStateMachine"))
            {
                continue;
            }

            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }

                // The synthetic entry point is the ONE permitted blocker, and it is not a judgement
                // call: a file using top-level statements with `await` compiles to a synchronous
                // `Main` that blocks on the generated `<Main>$` body. Nothing can await above it —
                // the process has no caller to yield to — so this is the compiler's shape, not an
                // author's choice.
                //
                // Matched by its EXACT generated names, never by "contains '<'": the looser form
                // also skips lambda bodies ("<Bait>b__0"), and a factory lambda is precisely where
                // the arb-agh defect lived. The bait test below fails if this is ever widened.
                if (method.Name is "<Main>" or "<Main>$" or "Main" && type.Name == "Program")
                {
                    continue;
                }

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt)
                    {
                        continue;
                    }

                    if (instruction.Operand is not MethodReference reference)
                    {
                        continue;
                    }

                    if (BannedMembers.Contains(reference.Name, StringComparer.Ordinal)
                        && IsAsyncResultType(reference.DeclaringType))
                    {
                        yield return
                            $"{type.FullName}::{method.Name} -> {reference.DeclaringType.Name}::{reference.Name}";
                    }
                }
            }
        }
    }

    [Fact]
    public void The_host_never_blocks_a_thread_on_an_async_call()
    {
        var assemblyPath = TestProcessGlobalStateTests.ResolveAssemblyPath("src", HostAssembly);

        // Loud failure, not a vacuous pass: an assembly the scan cannot open is not one it has
        // found to be clean (CLAUDE.md §4).
        Assert.True(
            assemblyPath is not null,
            $"Could not locate the built assembly for {HostAssembly}. This scan reads IL from the " +
            "build output, so the solution must be built before Arbitarr.Architecture.Tests runs.");

        var violations = FindBlockingCalls(assemblyPath!).ToArray();

        Assert.True(
            violations.Length == 0,
            "Arbitarr.Host may not block a thread on an async call (.GetAwaiter().GetResult(), " +
            ".Result, .Wait()). A scoped DI factory doing this blocks a thread-pool thread on every " +
            "scope — every search — which starved the pool and surfaced as the arb-agh flake. Hand " +
            "the dependency an async delegate and await it at the point of use instead (see " +
            "ReleaseLookupStore's TTL reader). Found:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves the scan is NON-VACUOUS (CLAUDE.md §4): the same walk, over an assembly that DOES
    /// contain a blocking call, must report it. Without this the assertion above would pass just as
    /// happily if the IL walk were broken, if the member names were misspelled, or if the
    /// compiler-generated filter were excluding everything — the shape CLAUDE.md §4 records as
    /// having shipped three times.
    ///
    /// <para>The bait is in THIS test assembly, never in <c>src/</c>: shipping a real blocking call
    /// into production to test the ban against it would be the very thing the ban forbids. This scan
    /// only ever reads <c>Arbitarr.Host</c>, so the bait cannot collide with it.</para>
    /// </summary>
    [Fact]
    public void The_scan_detects_a_blocking_call_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = FindBlockingCalls(baitAssembly!).ToArray();

        Assert.Contains(found, v => v.Contains(nameof(Bait), StringComparison.Ordinal));
    }

    /// <summary>
    /// Never executed. Exists so <see cref="The_scan_detects_a_blocking_call_when_it_is_present"/>
    /// has a real blocking call to find, in a test assembly rather than in production code.
    ///
    /// <para>The blocking call is placed inside a FACTORY LAMBDA on purpose: that is the shape the
    /// arb-agh defect actually had (<c>AddScoped&lt;T&gt;(sp =&gt; ... .GetAwaiter().GetResult())</c>),
    /// and it is the shape that proves the compiler-generated-name exclusion above does not swallow
    /// the very thing this ban exists to catch. A bare method-body bait would not — a lambda is
    /// emitted differently from a plain method, and only this arrangement tests that difference.</para>
    /// </summary>
    internal static Func<int> Bait()
    {
        var factory = () => Task.FromResult(1).GetAwaiter().GetResult();
        return factory;
    }
}
