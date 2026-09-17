using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-ifyh: no PRODUCTION assembly may construct or call a framework redirect result.
///
/// <para><b>Why this exists.</b> arb-j4hq replaced <c>Results.Redirect</c> in
/// <c>DownloadProxyEndpoint</c> with the private, non-logging <c>NonLoggingRedirectResult</c>
/// (behind <c>RedirectWithoutLogging</c>), because the framework's redirect result logs its
/// destination at Information via the <c>Microsoft.AspNetCore.Http.Result.RedirectResult</c> (and
/// equivalent) logger category before it writes the response header. In redirect NZB access mode
/// that destination is the indexer's own URL, which carries the indexer's API key — so a framework
/// redirect result here is a credential leak, not a style choice. Until this test, the ban on
/// reintroducing one was comment-only: nothing failed if a future change quietly went back to
/// <c>Results.Redirect</c> once the reason had faded from memory.</para>
///
/// <para><b>NO allow-list, by design.</b> Every member below is banned in every production
/// assembly with no exemption path. If a route in this codebase ever needs to redirect a caller,
/// its logging behaviour has to be someone's explicit decision (as <c>RedirectWithoutLogging</c>
/// is), not a default it fell into via a helper name that reads as harmless.</para>
///
/// <para><b>Banned regardless of whether a given member logs today.</b> Some of the members below
/// (the Minimal API <c>Results</c>/<c>TypedResults</c> statics and the
/// <c>RedirectHttpResult</c>/<c>RedirectToRouteHttpResult</c> types they return) are confirmed to
/// log the destination at Information. Others (MVC's <c>RedirectResult</c>/<c>LocalRedirectResult</c>
/// constructors, <c>HttpResponse.Redirect</c>) are banned on the same principle even though this
/// scan did not independently verify each one logs: an API surface named "redirect" that ships in
/// the same framework and is reached for by the same instinct that reached for
/// <c>Results.Redirect</c> the first time is exactly the shape arb-j4hq exists to close off, and a
/// narrower list would leave the door open through a sibling API. There is one sanctioned way to
/// redirect in this codebase — <c>RedirectWithoutLogging</c> — and it is a hand-written
/// <see cref="Microsoft.AspNetCore.Http.IResult"/>, not a framework type, so it can never appear in
/// this scan's findings no matter how it is called.</para>
///
/// <para><b>Why IL, not source or reflection.</b> Modelled on
/// <see cref="ProductionProcessGlobalStateTests"/> and its shared
/// <see cref="TestProcessGlobalStateTests.FindBannedCalls(string, string[])"/> walk: a grep over
/// source is defeated by a `using` alias, a fully-qualified spelling, or a thin wrapper method:
/// reading the IL sees the actual <c>call</c>/<c>callvirt</c>/<c>newobj</c> instruction and the
/// resolved member it targets, whatever the source spelled. This scan additionally walks
/// <c>newobj</c> (construction of <c>RedirectResult</c>/<c>LocalRedirectResult</c>), which the
/// shared <c>FindBannedCalls</c> helper does not cover, so it is implemented locally rather than by
/// widening that shared helper's opcode set for every other caller.</para>
///
/// <para><b>Matched by resolved type/member identity, never by source text</b> — the same reason
/// IL is used at all. A member is only a match if its declaring type's full name and member name
/// (constructor arity aside) equal an entry in <see cref="BannedTypeMembers"/> exactly.</para>
///
/// <para>The assembly list is <see cref="BuiltAssemblies.ProductionAssemblyNames"/> (arb-hxa), the
/// same list <see cref="ProductionProcessGlobalStateTests"/> scans.</para>
/// </summary>
public class FrameworkRedirectResultBanTests
{
    /// <summary>
    /// Framework members banned in every production assembly. Each entry is a declaring type's
    /// full name paired with the member name to match on the IL instruction's resolved operand.
    /// <c>MemberKind.Method</c> matches a <c>call</c>/<c>callvirt</c> to that method name on that
    /// type (constructor arity and overloads collapse together, which is intended: every overload
    /// of <c>Results.Redirect</c> is banned, not just one signature). <c>MemberKind.Constructor</c>
    /// matches a <c>newobj</c> whose constructed type is that type (MVC's result types are
    /// constructed directly by callers, not returned from a static factory).
    /// </summary>
    private static readonly (string DeclaringType, string MemberName, MemberKind Kind)[] BannedTypeMembers =
    [
        // Minimal API static factories. Confirmed to log the destination at Information via the
        // Microsoft.AspNetCore.Http.Result.RedirectResult logger category before writing the header
        // — this is the exact surface arb-j4hq replaced.
        ("Microsoft.AspNetCore.Http.Results", "Redirect", MemberKind.Method),
        ("Microsoft.AspNetCore.Http.Results", "LocalRedirect", MemberKind.Method),
        ("Microsoft.AspNetCore.Http.Results", "RedirectToRoute", MemberKind.Method),
        ("Microsoft.AspNetCore.Http.TypedResults", "Redirect", MemberKind.Method),
        ("Microsoft.AspNetCore.Http.TypedResults", "LocalRedirect", MemberKind.Method),
        ("Microsoft.AspNetCore.Http.TypedResults", "RedirectToRoute", MemberKind.Method),

        // The IResult types the factories above return, banned directly too: a caller that obtains
        // one some other way (a custom factory, reflection, a future framework helper) is banned by
        // the type itself, not only by the two named entry points into it.
        ("Microsoft.AspNetCore.Http.HttpResults.RedirectHttpResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Http.HttpResults.RedirectToRouteHttpResult", ".ctor", MemberKind.Constructor),

        // MVC's older result types, constructed directly rather than via a static factory. Banned
        // on the same principle as the Minimal API surface even without independently re-verifying
        // each one logs here: it is the same "redirect" instinct arb-j4hq closed off, in a sibling
        // API that ships in the same framework.
        ("Microsoft.AspNetCore.Mvc.RedirectResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Mvc.LocalRedirectResult", ".ctor", MemberKind.Constructor),

        // HttpResponse.Redirect(string[, bool]) — the oldest surface, still reachable from any
        // handler holding an HttpContext.
        ("Microsoft.AspNetCore.Http.HttpResponse", "Redirect", MemberKind.Method),
    ];

    private enum MemberKind
    {
        Method,
        Constructor,
    }

    [Fact]
    public void No_production_assembly_uses_a_framework_redirect_result()
    {
        var assemblyPaths = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => (Name: name, Path: TestProcessGlobalStateTests.ResolveAssemblyPath("src", name)))
            .ToArray();

        // Loud failure, not a vacuous pass: an assembly the scan cannot open is not an assembly it
        // has found to be clean. Also doubles as the "production assembly set is non-empty and
        // includes Arbitarr.Api" evidence this test's non-vacuity proof relies on.
        Assert.NotEmpty(assemblyPaths);
        Assert.Contains(assemblyPaths, a => a.Name == "Arbitarr.Api");

        var missing = assemblyPaths.Where(a => a.Path is null).Select(a => a.Name).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Could not locate the built assembly for: {string.Join(", ", missing)}. " +
            "This scan reads IL from each production project's build output, so the solution must " +
            "be built before Arbitarr.Architecture.Tests runs. Run 'dotnet build' at the solution " +
            "level first; in CI, the job must build the solution before running this project.");

        var violations = assemblyPaths
            .SelectMany(a => FindRedirectResultUsages(a.Path!).Select(v => $"{a.Name}: {v}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "No production assembly may use a framework redirect result (Results.Redirect, " +
            "TypedResults.Redirect/LocalRedirect/RedirectToRoute, RedirectHttpResult, " +
            "RedirectToRouteHttpResult, MVC's RedirectResult/LocalRedirectResult, or " +
            "HttpResponse.Redirect): the framework result logs its destination at Information " +
            "before writing the response header, and in redirect NZB access mode that destination " +
            "carries the indexer's own API key (arb-j4hq). Use " +
            "Arbitarr.Api.Search.DownloadProxyEndpoint's private RedirectWithoutLogging result " +
            "instead, which sets the Location header and status without logging it. Found:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves this scan is NON-VACUOUS: the same walk, over a test-assembly fixture method that
    /// DOES call a banned member, must report it. Without this, the assertion above would pass just
    /// as happily if the IL walk were broken, if a banned name were misspelled, or if every path
    /// resolved to something with no method bodies — the shape CLAUDE.md section 4 records as
    /// having shipped three times. The bait lives in this test assembly
    /// (<see cref="FrameworkRedirectResultBait"/>), never in src, so it cannot itself trip the
    /// production-only assertion above.
    /// </summary>
    [Fact]
    public void The_scan_detects_a_framework_redirect_result_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = FindRedirectResultUsages(baitAssembly!).ToArray();

        Assert.Contains(
            found,
            v => v.Contains(nameof(FrameworkRedirectResultBait), StringComparison.Ordinal)
                && v.Contains("Redirect", StringComparison.Ordinal));
    }

    /// <summary>
    /// Walks every method body in the assembly and reports each <c>call</c>/<c>callvirt</c> to a
    /// banned method, and each <c>newobj</c> constructing a banned type, as
    /// "&lt;type&gt;.&lt;method&gt; uses &lt;declaring type&gt;::&lt;member&gt;".
    /// </summary>
    private static IEnumerable<string> FindRedirectResultUsages(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    var match = Match(instruction);
                    if (match is not null)
                    {
                        yield return $"{type.FullName}.{method.Name} uses {match}";
                    }
                }
            }
        }
    }

    private static string? Match(Instruction instruction)
    {
        if (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt)
        {
            if (instruction.Operand is not MethodReference called)
            {
                return null;
            }

            var declaringType = called.DeclaringType.FullName;
            var found = BannedTypeMembers.FirstOrDefault(b =>
                b.Kind == MemberKind.Method
                && string.Equals(b.DeclaringType, declaringType, StringComparison.Ordinal)
                && string.Equals(b.MemberName, called.Name, StringComparison.Ordinal));

            return found.DeclaringType is null ? null : $"{found.DeclaringType}::{found.MemberName}";
        }

        if (instruction.OpCode == OpCodes.Newobj)
        {
            if (instruction.Operand is not MethodReference constructed)
            {
                return null;
            }

            var declaringType = constructed.DeclaringType.FullName;
            var found = BannedTypeMembers.FirstOrDefault(b =>
                b.Kind == MemberKind.Constructor
                && string.Equals(b.DeclaringType, declaringType, StringComparison.Ordinal));

            return found.DeclaringType is null ? null : $"{found.DeclaringType}::{found.MemberName}";
        }

        return null;
    }

    /// <summary>
    /// Bait for <see cref="The_scan_detects_a_framework_redirect_result_when_it_is_present"/>: the
    /// only sanctioned call to a banned redirect member anywhere in the test tree, present so the
    /// scan can be proven to see it. NEVER CALLED — the always-false guard makes that structural
    /// rather than a promise, so this cannot be mistaken for a usable helper. Lives in this test
    /// assembly, which the production assertion never scans, so it cannot collide with the ban it
    /// exists to prove works.
    /// </summary>
    private static class FrameworkRedirectResultBait
    {
        public static Microsoft.AspNetCore.Http.IResult CallsFrameworkRedirect()
        {
            if (AlwaysFalse)
            {
                return Microsoft.AspNetCore.Http.Results.Redirect("https://example.invalid/never-reached");
            }

            return Microsoft.AspNetCore.Http.Results.Ok();
        }

        // Deliberately not a const: a const would let the compiler drop the bodies above, and the
        // bait's IL would vanish along with the proof it exists to provide.
        private static bool AlwaysFalse => bool.Parse(bool.FalseString);
    }
}
