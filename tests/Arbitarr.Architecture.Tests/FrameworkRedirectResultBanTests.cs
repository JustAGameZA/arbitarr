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
/// log the destination at Information. Others (MVC's result-type constructors,
/// <c>HttpResponse.Redirect</c>, <c>ResponseExtensions.Redirect</c>) are banned on the same
/// principle even though this scan did not independently verify each one logs: an API surface
/// named "redirect" that ships in the same framework and is reached for by the same instinct that
/// reached for <c>Results.Redirect</c> the first time is exactly the shape arb-j4hq exists to close
/// off, and a narrower list would leave the door open through a sibling API. There is one
/// sanctioned way to redirect in this codebase — <c>RedirectWithoutLogging</c> — and it is a
/// hand-written <see cref="Microsoft.AspNetCore.Http.IResult"/>, not a framework type, so it can
/// never appear in this scan's findings no matter how it is called.</para>
///
/// <para><b>Why IL, not source or reflection.</b> Modelled on
/// <see cref="ProductionProcessGlobalStateTests"/> and its shared
/// <see cref="TestProcessGlobalStateTests.FindBannedCalls(string, string[])"/> walk: a grep over
/// source is defeated by a `using` alias, a fully-qualified spelling, or a thin wrapper method:
/// reading the IL sees the actual <c>call</c>/<c>callvirt</c>/<c>newobj</c> instruction and the
/// resolved member it targets, whatever the source spelled. This scan additionally walks
/// <c>newobj</c> (construction of the MVC and Minimal API result types), which the shared
/// <c>FindBannedCalls</c> helper does not cover, so it is implemented locally rather than by
/// widening that shared helper's opcode set for every other caller.</para>
///
/// <para><b>Matched by resolved type/member identity, never by source text</b> — the same reason
/// IL is used at all. A member is only a match if its declaring type's full name and member name
/// (constructor arity aside) equal an entry in <see cref="BannedTypeMembers"/> exactly. This is
/// also why the extension method <c>ResponseExtensions.Redirect(HttpResponse, string, bool,
/// bool)</c> needs its OWN entry rather than being covered by the <c>HttpResponse.Redirect</c> one:
/// an extension method compiles to a static call whose declaring type is
/// <c>Microsoft.AspNetCore.Http.ResponseExtensions</c>, not <c>HttpResponse</c> — the instance
/// syntax at the call site (<c>response.Redirect(url, permanent, preserveMethod)</c>) does not
/// change what the IL actually names.</para>
///
/// <para><b>What this scan cannot see, stated plainly rather than left implicit.</b>
/// <list type="bullet">
/// <item><c>ControllerBase</c>'s own <c>Redirect*</c>/<c>LocalRedirect*</c>/
/// <c>RedirectToAction*</c>/<c>RedirectToPage*</c>/<c>RedirectToRoute*</c> helper methods are NOT
/// banned directly, and cannot be with this technique: those methods live in a framework assembly
/// this project does not scan, so a call to <c>ControllerBase.Redirect(url)</c> from a hypothetical
/// controller in <c>src</c> would show up in THIS assembly's IL as a call to
/// <c>Microsoft.AspNetCore.Mvc.ControllerBase::Redirect</c> — a member this list does not name.
/// They are covered only INDIRECTLY, through the result types they construct internally
/// (<c>RedirectResult</c>, <c>LocalRedirectResult</c>, <c>RedirectToActionResult</c>,
/// <c>RedirectToPageResult</c>, <c>RedirectToRouteResult</c>) — all of which are banned below —
/// which stops a controller from returning one some other way, but would not stop
/// <c>ControllerBase.Redirect</c> itself from being called if this codebase ever gains a
/// controller. Arbitarr has none today (routes are Minimal API), so the direct method entries are
/// deferred rather than added speculatively; adding the FIRST controller-derived endpoint is the
/// trigger to widen <see cref="BannedTypeMembers"/> with <c>ControllerBase</c>'s own method
/// names.</item>
/// <item>A method-group or delegate reference to any banned member (e.g. passing
/// <c>Results.Redirect</c> as a delegate rather than calling it, which compiles to <c>ldftn</c>
/// rather than <c>call</c>) is outside this scan. Only direct <c>call</c>/<c>callvirt</c>/
/// <c>newobj</c> instructions are matched.</item>
/// </list>
/// This list does not claim to close every path to a framework redirect result — only the ones
/// enumerated in <see cref="BannedTypeMembers"/>, matched exactly as IL names them.</para>
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
    ///
    /// <para>Every entry here is exercised by exactly one never-called method on
    /// <see cref="FrameworkRedirectResultBait"/>, and
    /// <see cref="The_scan_detects_each_banned_entry_when_it_is_present"/> asserts, per entry, that
    /// the scan reports it — so a typo in a declaring-type name, or a broken opcode branch, fails
    /// that test instead of silently narrowing the ban.</para>
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

        // RedirectHttpResult/RedirectToRouteHttpResult are NOT listed as MemberKind.Constructor:
        // both types are public, but every constructor on both is `internal` to
        // Microsoft.AspNetCore.Http.Results.dll (verified against the shipped assembly), so no
        // production assembly in this solution can ever construct one directly with `newobj` — the
        // ONLY way to obtain either type from outside the framework is through the
        // Results/TypedResults static factories already banned above. A constructor entry for
        // either type would be unreachable from src by construction, and unbaitable: a bait
        // exercising it would itself fail to compile, which is worse than not asserting it, since a
        // failing bait would silently drop out of the per-entry proof rather than fail loudly.

        // MVC's result types, constructed directly rather than via a static factory. Banned on the
        // same principle as the Minimal API surface even without independently re-verifying each
        // one logs here: it is the same "redirect" instinct arb-j4hq closed off, in a sibling API
        // that ships in the same framework. RedirectToActionResult/RedirectToPageResult/
        // RedirectToRouteResult are what ControllerBase.RedirectToAction*/RedirectToPage*/
        // RedirectToRoute* construct internally — see the class remarks on why the helper METHODS
        // themselves are not (yet) named here.
        ("Microsoft.AspNetCore.Mvc.RedirectResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Mvc.LocalRedirectResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Mvc.RedirectToActionResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Mvc.RedirectToPageResult", ".ctor", MemberKind.Constructor),
        ("Microsoft.AspNetCore.Mvc.RedirectToRouteResult", ".ctor", MemberKind.Constructor),

        // HttpResponse.Redirect(string[, bool]) — the oldest surface, still reachable from any
        // handler holding an HttpContext.
        ("Microsoft.AspNetCore.Http.HttpResponse", "Redirect", MemberKind.Method),

        // The instance-syntax extension overload with permanent/preserveMethod flags. Its declaring
        // type in IL is ResponseExtensions, NOT HttpResponse, even though the call site reads
        // response.Redirect(url, permanent, preserveMethod) — an extension method never changes
        // where the compiler puts the call. Missing this entry is exactly the gap that let
        // response.Redirect(url, permanent: false, preserveMethod: false) pass this scan clean.
        ("Microsoft.AspNetCore.Http.ResponseExtensions", "Redirect", MemberKind.Method),
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
            "TypedResults.Redirect/LocalRedirect/RedirectToRoute, MVC's RedirectResult/" +
            "LocalRedirectResult/RedirectToActionResult/RedirectToPageResult/RedirectToRouteResult, " +
            "HttpResponse.Redirect, or ResponseExtensions.Redirect): the framework result logs its " +
            "destination at Information before writing the response header, and in redirect NZB " +
            "access mode that destination carries the indexer's own API key (arb-j4hq). Use " +
            "Arbitarr.Api.Search.DownloadProxyEndpoint's private RedirectWithoutLogging result " +
            "instead, which sets the Location header and status without logging it. Found:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves this scan is NON-VACUOUS, per banned entry: the same walk, over a test-assembly
    /// fixture method that DOES use that exact entry, must report it. Without a per-entry
    /// assertion, a single "something was found" check would still pass if one entry's type name
    /// were misspelt, if the <c>Newobj</c> branch were broken while <c>Call</c>/<c>Callvirt</c>
    /// still worked, or if the new <c>ResponseExtensions</c> entry silently never matched — exactly
    /// the class of gap that let <c>ResponseExtensions.Redirect</c> through in the first place. The
    /// bait lives in this test assembly (<see cref="FrameworkRedirectResultBait"/>), one
    /// never-called method per entry, never in src, so it cannot itself trip the production-only
    /// assertion above.
    /// </summary>
    [Fact]
    public void The_scan_detects_each_banned_entry_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = FindRedirectResultUsages(baitAssembly!).ToArray();

        foreach (var entry in BannedTypeMembers)
        {
            var expectedSuffix = $"{entry.DeclaringType}::{entry.MemberName}";
            Assert.True(
                found.Any(v =>
                    v.Contains(nameof(FrameworkRedirectResultBait), StringComparison.Ordinal)
                    && v.EndsWith(expectedSuffix, StringComparison.Ordinal)),
                $"The scan did not detect the bait's use of {expectedSuffix}. Either the bait is " +
                "missing a case for this entry, or this entry's DeclaringType/MemberName no longer " +
                "matches what the IL actually names (a typo here would silently narrow the " +
                "production ban to nothing for this entry). Found:\n  " + string.Join("\n  ", found));
        }
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
    /// Bait for <see cref="The_scan_detects_each_banned_entry_when_it_is_present"/>: one
    /// never-called method per entry in <see cref="BannedTypeMembers"/>, so the scan can be proven
    /// to see every entry individually rather than "at least one of them". NEVER CALLED — each
    /// method's always-false guard makes that structural rather than a promise, so none of these can
    /// be mistaken for a usable helper. Lives in this test assembly, which the production assertion
    /// never scans, so it cannot collide with the ban it exists to prove works.
    /// </summary>
    private static class FrameworkRedirectResultBait
    {
        public static Microsoft.AspNetCore.Http.IResult CallsResultsRedirect() =>
            AlwaysFalse ? Microsoft.AspNetCore.Http.Results.Redirect("https://example.invalid/never-reached") : Ok();

        public static Microsoft.AspNetCore.Http.IResult CallsResultsLocalRedirect() =>
            AlwaysFalse ? Microsoft.AspNetCore.Http.Results.LocalRedirect("/never-reached") : Ok();

        public static Microsoft.AspNetCore.Http.IResult CallsResultsRedirectToRoute() =>
            AlwaysFalse ? Microsoft.AspNetCore.Http.Results.RedirectToRoute("never-reached") : Ok();

        public static Microsoft.AspNetCore.Http.IResult CallsTypedResultsRedirect() =>
            AlwaysFalse
                ? Microsoft.AspNetCore.Http.TypedResults.Redirect("https://example.invalid/never-reached")
                : Ok();

        public static Microsoft.AspNetCore.Http.IResult CallsTypedResultsLocalRedirect() =>
            AlwaysFalse ? Microsoft.AspNetCore.Http.TypedResults.LocalRedirect("/never-reached") : Ok();

        public static Microsoft.AspNetCore.Http.IResult CallsTypedResultsRedirectToRoute() =>
            AlwaysFalse ? Microsoft.AspNetCore.Http.TypedResults.RedirectToRoute("never-reached") : Ok();

        public static object? ConstructsMvcRedirectResult() =>
            AlwaysFalse ? new Microsoft.AspNetCore.Mvc.RedirectResult("https://example.invalid/never-reached") : null;

        public static object? ConstructsMvcLocalRedirectResult() =>
            AlwaysFalse ? new Microsoft.AspNetCore.Mvc.LocalRedirectResult("/never-reached") : null;

        public static object? ConstructsRedirectToActionResult() =>
            AlwaysFalse
                ? new Microsoft.AspNetCore.Mvc.RedirectToActionResult("NeverReached", "NeverReached", null)
                : null;

        public static object? ConstructsRedirectToPageResult() =>
            AlwaysFalse ? new Microsoft.AspNetCore.Mvc.RedirectToPageResult("/never-reached") : null;

        public static object? ConstructsRedirectToRouteResult() =>
            AlwaysFalse ? new Microsoft.AspNetCore.Mvc.RedirectToRouteResult((object?)null) : null;

        public static void CallsHttpResponseRedirect(Microsoft.AspNetCore.Http.HttpResponse response)
        {
            if (AlwaysFalse)
            {
                response.Redirect("https://example.invalid/never-reached");
            }
        }

        public static void CallsResponseExtensionsRedirect(Microsoft.AspNetCore.Http.HttpResponse response)
        {
            if (AlwaysFalse)
            {
                // Called explicitly as a static method to force the exact overload — at an ordinary
                // call site this reads as instance syntax (response.Redirect(url, permanent,
                // preserveMethod)), which still compiles to this same static call on
                // ResponseExtensions, not HttpResponse. That is the exact call shape that escaped
                // the scan before this entry existed.
                Microsoft.AspNetCore.Http.ResponseExtensions.Redirect(response, "https://example.invalid/never-reached", false, false);
            }
        }

        private static Microsoft.AspNetCore.Http.IResult Ok() => Microsoft.AspNetCore.Http.Results.Ok();

        // Deliberately not a const: a const would let the compiler drop the bodies above, and the
        // bait's IL would vanish along with the proof it exists to provide.
        private static bool AlwaysFalse => bool.Parse(bool.FalseString);
    }
}
