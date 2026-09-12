using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-1z1l: every production call to <c>UseSqlite</c> that hands EF Core a
/// <see cref="System.Data.Common.DbConnection"/> must pass <c>contextOwnsConnection: true</c>.
///
/// <para><b>Why this needs a test at all.</b> EF's <c>DbConnection</c> overload leaves ownership
/// with the CALLER by default, so the context never disposes the connection it was given. The #230
/// fix — <c>ArbitarrDbContextOptionsFactory.Create</c> handing the connection over — depends
/// entirely on that flag being <c>true</c>, and nothing else enforces it. Drop the flag and the
/// code still compiles (a three-parameter overload binds happily), still passes every behavioural
/// test on Linux, and fails only through a Windows-specific symptom: a file handle survives
/// disposal, so a directory delete fails. That is a long way from the edit that caused it.</para>
///
/// <para><b>Both failure shapes are covered, because they are different edits.</b> Passing
/// <c>false</c> is one; dropping the argument entirely — which silently selects the
/// <c>(builder, DbConnection, Action)</c> overload that has no ownership parameter and defaults to
/// caller-owned — is the other, and the one a "simplify this call" cleanup actually produces. A
/// scan that only looked for <c>ldc.i4.0</c> would pass over it.</para>
///
/// <para><b>The overload set, as of Microsoft.EntityFrameworkCore.Sqlite 10.0.11</b> (pinned in
/// <c>src/Arbitarr.Data/Arbitarr.Data.csproj</c>). <c>SqliteDbContextOptionsBuilderExtensions</c>
/// declares four <c>UseSqlite</c> shapes, each in a non-generic and a generic
/// (<c>DbContextOptionsBuilder&lt;TContext&gt;</c>) form: no data source; <c>string</c>;
/// <c>DbConnection</c>; and <c>DbConnection</c> + <c>bool contextOwnsConnection</c>. Only the last
/// two take a connection, and only the last carries the flag. Every shape ends in an OPTIONAL
/// <c>Action&lt;SqliteDbContextOptionsBuilder&gt; sqliteOptionsAction</c> — optional in C#, but not
/// in the IL, where the compiler always pushes an argument for it (<c>ldnull</c> when omitted). So
/// the argument count at the call site is always the declared parameter count, and the flag's
/// position is found from the signature rather than guessed.</para>
///
/// <para><b>Why the flag is located by stack simulation rather than a fixed step-back, and the one
/// case that still defeats it.</b> Stepping back by STACK DELTA finds the instruction that actually
/// produced the flag whatever the neighbouring arguments cost, which a fixed instruction count
/// cannot. But it is still a LINEAR walk over a straight-line instruction list, and one legal call
/// shape is not straight-line: passing a real <c>sqliteOptionsAction</c> lambda emits the compiler's
/// delegate CACHE, which contains a forward <c>brtrue.s</c> over the construction plus
/// <c>dup</c>/<c>stsfld</c>. No backward linear walk can model that branch — measured, not assumed:
/// a throwaway probe outside the repo confirmed the walk loses the stack there.</para>
///
/// <para>That case is therefore reported as UNRESOLVABLE, with its own message, and it FAILS rather
/// than passing. The alternative — treating "I could not read the flag" as "the flag is fine" — is
/// the vacuous pass this class exists to prevent, and it would hide the very edit most likely to be
/// made alongside an ownership change. The failure text says how to make the call readable again
/// (hoist the lambda into a static field or a named method, so the argument is a single push). No
/// current call site takes this path; if one legitimately needs to, that is a deliberate decision
/// with a visible cost, which is the right trade for a rule whose only other enforcement is a
/// Windows-only file-deletion symptom.</para>
/// </summary>
public class SqliteConnectionOwnershipTests
{
    private const string ExtensionsType =
        "Microsoft.EntityFrameworkCore.SqliteDbContextOptionsBuilderExtensions";

    private const string DbConnectionType = "System.Data.Common.DbConnection";

    /// <summary>
    /// Marks a flag whose producing instruction the backward walk could not identify. Reported as a
    /// failure with its own wording, never as a pass — see the class remarks.
    /// </summary>
    private const string Unresolvable = "(not resolvable)";

    /// <summary>
    /// The ownership parameter's source name. Used only in failure text: it cannot be matched
    /// against the IL, because a call-site <see cref="MethodReference"/> carries parameter TYPES
    /// but not names.
    /// </summary>
    private const string OwnershipParameter = "contextOwnsConnection";

    [Fact]
    public void Every_production_UseSqlite_that_takes_a_connection_passes_ownership_to_the_context()
    {
        var assemblyPaths = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => (Name: name, Path: BuiltAssemblies.ResolveAssemblyPath("src", name)))
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

        var callSites = assemblyPaths
            .SelectMany(a => FindConnectionCallSites(a.Path!).Select(c => c with { Assembly = a.Name }))
            .ToArray();

        // NON-VACUITY, asserted before the real check rather than in a separate test: this scan's
        // whole failure mode is matching nothing — a renamed extension type, a signature change in a
        // future EF version, or an IL walk that reads the wrong operand all produce an empty set,
        // and an empty set satisfies "no violations" perfectly. #230's call site is the one this
        // rule exists for, so its absence means the scan stopped working, not that the tree is
        // clean.
        Assert.True(
            callSites.Length > 0,
            "Found no UseSqlite(DbConnection, ...) call anywhere in src/, but " +
            "ArbitarrDbContextOptionsFactory.Create makes one. An empty result means this scan no " +
            "longer matches the calls it is meant to police — check that " +
            $"'{ExtensionsType}' is still the declaring type and that its overloads still take a " +
            $"'{DbConnectionType}'.");

        var violations = callSites.Where(c => !c.PassesOwnershipTrue).ToArray();

        Assert.True(
            violations.Length == 0,
            "EF Core's UseSqlite(DbConnection) overloads leave the connection owned by the CALLER " +
            "unless contextOwnsConnection: true is passed, so the context never disposes it and the " +
            "handle outlives the DbContext. That is what #230 fixed, and on Linux nothing detects " +
            "its loss — the only symptom is a Windows file-deletion failure far from the edit. " +
            "Pass contextOwnsConnection: true explicitly; the overload without the parameter is not " +
            "equivalent. Found:\n  " + string.Join("\n  ", violations.Select(v => v.Describe())));
    }

    /// <summary>
    /// A single <c>UseSqlite</c> call that hands over a connection, and what it does with ownership.
    /// </summary>
    private readonly record struct ConnectionCallSite(
        string Assembly,
        string Method,
        bool HasOwnershipParameter,
        bool PassesOwnershipTrue,
        string FlagInstruction)
    {
        internal string Describe()
        {
            if (!HasOwnershipParameter)
            {
                return $"{Assembly}: {Method} calls the UseSqlite overload with NO " +
                       $"{OwnershipParameter} parameter, which leaves the connection owned by the caller";
            }

            return FlagInstruction == Unresolvable
                ? $"{Assembly}: {Method} passes {OwnershipParameter}, but this scan could not read " +
                  "which value: the argument is not a single stack push, which happens when a " +
                  "sqliteOptionsAction lambda emits the compiler's delegate cache (a branch the " +
                  "backward walk cannot follow). Hoist the lambda into a static field or a named " +
                  "method so each argument is one push, and the flag becomes readable again."
                : $"{Assembly}: {Method} passes {OwnershipParameter} as '{FlagInstruction}', not " +
                  "ldc.i4.1 (true)";
        }
    }

    /// <summary>
    /// Every call in the assembly to a <c>UseSqlite</c> overload taking a
    /// <see cref="System.Data.Common.DbConnection"/>, whether or not it carries the ownership flag.
    /// The <c>string</c> and no-data-source overloads are deliberately out of scope: they open a
    /// connection EF itself owns, so the ownership question does not arise.
    /// </summary>
    private static IEnumerable<ConnectionCallSite> FindConnectionCallSites(string assemblyPath)
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

                    if (instruction.Operand is not MethodReference called ||
                        called.Name != "UseSqlite")
                    {
                        continue;
                    }

                    // ElementMethod strips the generic instantiation, so the generic
                    // UseSqlite<TContext> form and the non-generic one are matched identically.
                    var definition = called.GetElementMethod();

                    if (definition.DeclaringType.FullName != ExtensionsType)
                    {
                        continue;
                    }

                    var parameters = definition.Parameters;

                    if (!parameters.Any(p => p.ParameterType.FullName == DbConnectionType))
                    {
                        continue;
                    }

                    // Located by TYPE and position, not by parameter name: a MethodReference read
                    // from a call site carries only the signature, and its Parameters have no names
                    // (they live on the resolved definition, which needs the EF assembly on disk).
                    // Matching on the name silently found nothing and reported the one correct call
                    // site in the tree as missing the flag entirely. Boolean is unambiguous here —
                    // of the four UseSqlite shapes, exactly one has a bool, and it is the ownership
                    // flag (see the overload inventory in the class remarks).
                    var flagIndex = parameters
                        .Select((p, i) => (p, i))
                        .Where(x => x.p.ParameterType.FullName == "System.Boolean")
                        .Select(x => (int?)x.i)
                        .FirstOrDefault();

                    var where = $"{type.FullName}.{method.Name}";

                    if (flagIndex is null)
                    {
                        yield return new ConnectionCallSite(
                            Assembly: string.Empty,
                            Method: where,
                            HasOwnershipParameter: false,
                            PassesOwnershipTrue: false,
                            FlagInstruction: "(absent)");
                        continue;
                    }

                    // Arguments after the flag, each of which pushed exactly one value onto the
                    // stack; stepping back over that many pushes reaches the flag's own producer.
                    var argumentsAfterFlag = parameters.Count - 1 - flagIndex.Value;
                    var producer = FindArgumentProducer(instruction, argumentsAfterFlag);

                    yield return new ConnectionCallSite(
                        Assembly: string.Empty,
                        Method: where,
                        HasOwnershipParameter: true,
                        PassesOwnershipTrue: producer?.OpCode == OpCodes.Ldc_I4_1,
                        FlagInstruction: producer?.OpCode.ToString() ?? Unresolvable);
                }
            }
        }
    }

    /// <summary>
    /// Walks backwards from <paramref name="call"/> to the instruction that pushed the argument
    /// sitting <paramref name="argumentsAfter"/> places below the top of the stack, by simulating
    /// each instruction's net stack effect in reverse.
    ///
    /// <para>Returns <see langword="null"/> when the walk cannot account for the stack — a
    /// <c>dup</c>, a branch, or any opcode whose effect is not statically known. In practice the
    /// one shape that reaches this is the compiler's lambda delegate cache emitted for a real
    /// <c>sqliteOptionsAction</c>; see the class remarks. A null is reported as a FAILURE by the
    /// caller, not skipped: a call site whose flag cannot be read is a call site this scan has not
    /// verified, and treating "I could not tell" as "it is fine" is the vacuous pass this whole
    /// class is built to avoid.</para>
    /// </summary>
    private static Instruction? FindArgumentProducer(Instruction call, int argumentsAfter)
    {
        var current = call.Previous;

        for (var remaining = argumentsAfter; current is not null; remaining--)
        {
            var pushes = PushCount(current);
            var pops = PopCount(current);

            // An instruction that pushes anything other than exactly one value is not an argument
            // producer, and the walk has lost its place.
            if (pushes != 1 || pops is null)
            {
                return null;
            }

            if (remaining == 0)
            {
                return current;
            }

            // Step over this argument AND the instructions that produced its own operands (a nested
            // call, an arithmetic expression), so the next step lands on the preceding argument.
            current = SkipOperandsOf(current, pops.Value);
        }

        return null;
    }

    /// <summary>
    /// Steps back over the instructions that produced <paramref name="operandCount"/> operands for
    /// the instruction already consumed, so the walk stays aligned with the evaluation stack.
    /// </summary>
    private static Instruction? SkipOperandsOf(Instruction consumed, int operandCount)
    {
        var current = consumed.Previous;

        for (var i = 0; i < operandCount && current is not null; i++)
        {
            var pushes = PushCount(current);
            var pops = PopCount(current);

            if (pushes != 1 || pops is null)
            {
                return null;
            }

            current = SkipOperandsOf(current, pops.Value);
        }

        return current;
    }

    /// <summary>
    /// How many values the instruction pushes, or <see langword="null"/> when that is not
    /// statically knowable. Only the opcodes this scan can appear next to are decoded; anything
    /// else returns null and the caller reports the call site as unverifiable rather than clean.
    /// </summary>
    private static int? PushCount(Instruction instruction) => instruction.OpCode.StackBehaviourPush switch
    {
        StackBehaviour.Push0 => 0,
        StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or
        StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
        StackBehaviour.Push1_push1 => 2,
        StackBehaviour.Varpush when instruction.Operand is MethodReference m =>
            m.ReturnType.FullName == "System.Void" ? 0 : 1,
        _ => null,
    };

    /// <summary>
    /// How many values the instruction pops, or <see langword="null"/> when that is not statically
    /// knowable. A <c>call</c>'s pop count comes from its own signature, including the <c>this</c>
    /// argument for an instance method.
    /// </summary>
    private static int? PopCount(Instruction instruction) => instruction.OpCode.StackBehaviourPop switch
    {
        StackBehaviour.Pop0 => 0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or
        StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or
        StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or
        StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or
        StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref => 3,
        StackBehaviour.Varpop when instruction.Operand is MethodReference m =>
            m.Parameters.Count + (m.HasThis ? 1 : 0),
        _ => null,
    };
}
