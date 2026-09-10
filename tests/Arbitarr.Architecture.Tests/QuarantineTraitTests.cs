using Mono.Cecil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-rga.5 / ADR 0011: a test may carry <c>Category=Quarantine</c> to stop blocking merges, but
/// only together with a <c>Bead=arb-xxx</c> trait naming the issue that tracks it. Quarantine is a
/// tracked, visible exception with an owner; without the bead it is a silent skip that nothing ever
/// comes back to, which is the failure this test exists to make impossible.
///
/// <para>The rule is enforced over the same ten test assemblies as
/// <see cref="TestProcessGlobalStateTests"/>, by the same means and for the same reasons: the
/// assemblies are found by PATH (they cannot be ProjectReferences here — NU1605), so the scan
/// depends on them having been BUILT first, and a missing assembly FAILS loudly rather than passing
/// vacuously over an empty set.</para>
///
/// <para><b>Why IL rather than xunit's own discovery.</b> Running the discoverer would only see the
/// traits xunit chooses to surface for tests it decides to run, so a quarantined test in an
/// assembly that failed to load, or one hidden behind a custom discoverer, would simply not appear
/// — and not appearing is indistinguishable from complying. Reading the attribute rows with Cecil
/// sees every <c>[Trait]</c> that was compiled, whatever the runner would have done with it.</para>
///
/// <para><b>The only Quarantine attributes in the tree are in <see cref="Bait"/>.</b> arb-rga.5's
/// acceptance criterion is zero quarantined tests at cutover, and that holds: the bait types are
/// private, nested and factless, so no runner ever collects them as tests, and the rule's own scan
/// excludes them by exact type-name prefix. "Zero Quarantine traits at cutover" therefore reads as
/// zero on TESTS — the bait's attributes exist only so the rule can be proven to bite over a tree
/// that otherwise contains no violation to find.</para>
/// </summary>
public class QuarantineTraitTests
{
    private const string CategoryTraitName = "Category";
    private const string QuarantineCategory = "Quarantine";
    private const string BeadTraitName = "Bead";

    /// <summary>
    /// The bead id shape, as ADR 0011 fixes it: <c>arb-</c> then at least three lowercase
    /// alphanumerics. Anchored at both ends so <c>arb-</c> alone, a trailing comment, or a bare
    /// "TODO" cannot satisfy it.
    /// </summary>
    private const string BeadPattern = "^arb-[a-z0-9]{3,}$";

    /// <summary>
    /// Every test assembly in the solution, by project name — the same list, spelled out for the
    /// same reason as <see cref="TestProcessGlobalStateTests"/>: a glob over whatever happens to be
    /// on disk would silently skip a project that had not been built.
    /// </summary>
    private static readonly string[] TestAssemblyNames =
    [
        "Arbitarr.Ai.Tests",
        "Arbitarr.Api.Tests",
        "Arbitarr.Architecture.Tests",
        "Arbitarr.Core.Identity.Tests",
        "Arbitarr.Core.Tests",
        "Arbitarr.Data.Tests",
        "Arbitarr.Host.Tests",
        "Arbitarr.Integration.Tests",
        "Arbitarr.Media.Tests",
        "Arbitarr.Sources.NzbHydra.Tests",
    ];

    [Fact]
    public void No_quarantined_test_lacks_a_bead_trait()
    {
        var assemblyPaths = TestAssemblyNames
            .Select(name => (Name: name, Path: ResolveTestAssemblyPath(name)))
            .ToArray();

        // Loud failure, not a vacuous pass: if the scan cannot see an assembly, that is not
        // evidence the assembly is clean.
        var missing = assemblyPaths.Where(a => a.Path is null).Select(a => a.Name).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Could not locate the built assembly for: {string.Join(", ", missing)}. " +
            "This scan reads trait attributes from each test project's build output, so every test " +
            "project must be built before Arbitarr.Architecture.Tests runs (see the class remarks). " +
            "Run 'dotnet build' at the solution level first; in CI, the job must build the solution " +
            "before running this project.");

        var violations = new List<string>();

        foreach (var (name, path) in assemblyPaths)
        {
            foreach (var violation in FindQuarantineWithoutBead(path!))
            {
                violations.Add($"{name}: {violation}");
            }
        }

        // The bait below is this assembly's own deliberate sample of the violation; it is what
        // proves the scan can see one at all, so it is not itself a violation.
        //
        // THE TRAILING '/' IS LOAD-BEARING. Every bait finding names a type NESTED inside Bait, and
        // Cecil separates nested types with '/', so "<Bait>/" matches all three of them and nothing
        // else. Without it the prefix is a bare type name, and a future sibling — a Bait2, or a
        // BaitHelpers — would start with that same string and be silently exempted from the ban,
        // which is precisely the back-door TestProcessGlobalStateTests' equivalent comment warns
        // about. Note that file uses '.' rather than '/' because ITS findings are METHODS on the
        // bait type, not types nested inside it; the separator follows the member kind, so copying
        // one into the other would re-open the hole it is there to close.
        var baitPrefix = $"Arbitarr.Architecture.Tests: {BaitTypeName}/";
        violations.RemoveAll(v => v.StartsWith(baitPrefix, StringComparison.Ordinal));

        Assert.True(
            violations.Count == 0,
            $"A Category={QuarantineCategory} trait must always be accompanied by a {BeadTraitName} " +
            $"trait matching {BeadPattern}, naming the issue that tracks the flake. Quarantine " +
            "without a bead is a silent skip nothing ever comes back to (ADR 0011). Found:\n  " +
            string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves the scan is NON-VACUOUS: it must actually find a quarantined test that carries no
    /// bead. Without this, <see cref="No_quarantined_test_lacks_a_bead_trait"/> would pass just as
    /// happily if the attribute walk were broken, read the wrong attribute, or matched nothing at
    /// all — and today it would pass either way, because the tree deliberately contains ZERO
    /// quarantined tests. An all-clear over an empty set is exactly the vacuous assertion CLAUDE.md
    /// section 4 records as having shipped three times.
    ///
    /// <para>The bait is <see cref="Bait"/> in this assembly: a private nested type, never a test
    /// class and never executed, carrying the offending trait combination so the scan has something
    /// real to find. It is private and factless so no runner ever collects it, which is what keeps
    /// the shipped tree free of an actual quarantined test while still proving the rule bites.</para>
    /// </summary>
    [Fact]
    public void The_scan_detects_a_quarantined_test_with_no_bead()
    {
        var path = ResolveTestAssemblyPath("Arbitarr.Architecture.Tests");
        Assert.NotNull(path);

        var baitViolations = FindQuarantineWithoutBead(path!)
            .Where(v => v.StartsWith(BaitTypeName, StringComparison.Ordinal))
            .ToArray();

        // Both shapes the rule has to catch, asserted SEPARATELY. A class-level quarantine and a
        // method-level one are found by different branches of the walk, so one working is no
        // evidence for the other, and a single "something was found" assertion would pass with
        // either branch dead.
        Assert.Contains(
            baitViolations,
            v => v.EndsWith($"{nameof(Bait.QuarantinedClassWithoutABead)} (class-level trait)", StringComparison.Ordinal));
        Assert.Contains(
            baitViolations,
            v => v.EndsWith(
                $"{nameof(Bait.QuarantinedMethodWithoutABead)}.{nameof(Bait.QuarantinedMethodWithoutABead.AQuarantinedMethod)}",
                StringComparison.Ordinal));

        // The method inside the quarantined class is NOT a separate finding: the class is the one
        // place the bead belongs, so reporting its methods too would turn one missing bead into a
        // list as long as the class. Asserting the absence here is what pins that behaviour, since
        // the two Contains assertions above would pass just as well if it were over-reported.
        Assert.DoesNotContain(
            baitViolations,
            v => v.EndsWith(
                $".{nameof(Bait.QuarantinedClassWithoutABead.AMethodInheritingTheClassTrait)}",
                StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves the rule ACCEPTS a correctly-quarantined test, so it cannot be satisfied by a walk
    /// that simply reports everything (or nothing) it sees. <see cref="Bait.QuarantinedWithABead"/>
    /// carries both traits and must produce no finding; without this, a scan hard-wired to flag
    /// every Quarantine trait regardless of its bead would look identical to a correct one.
    /// </summary>
    [Fact]
    public void The_scan_accepts_a_quarantined_test_that_carries_a_bead()
    {
        var path = ResolveTestAssemblyPath("Arbitarr.Architecture.Tests");
        Assert.NotNull(path);

        var findings = FindQuarantineWithoutBead(path!)
            .Where(v => v.Contains(nameof(Bait.QuarantinedWithABead), StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            findings.Length == 0,
            "A Quarantine trait accompanied by a valid Bead trait must NOT be reported. Found:\n  " +
            string.Join("\n  ", findings));
    }

    /// <summary>
    /// The bait type as CECIL spells it. Reflection renders a nested type with a '+' separator
    /// (<c>Outer+Bait</c>) while Cecil's <c>TypeDefinition.FullName</c> uses '/' — so comparing a
    /// reflected name against a scanned one silently never matches, and every bait finding would be
    /// reported as a real violation while the non-vacuity proof found nothing. Derived from
    /// <c>typeof</c> rather than written out so a rename cannot leave this behind.
    /// </summary>
    private static string BaitTypeName => typeof(Bait).FullName!.Replace('+', '/');

    /// <summary>
    /// Reports every type or method carrying <c>Category=Quarantine</c> without a valid
    /// <c>Bead</c> trait. A class-level trait covers the methods in that class, so a method inside a
    /// quarantined class satisfies the rule through its class's own bead and is not reported twice —
    /// the class is reported once instead, which is where the fix belongs.
    /// </summary>
    private static IEnumerable<string> FindQuarantineWithoutBead(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            var classQuarantined = IsQuarantined(type.CustomAttributes);
            var classHasBead = HasValidBead(type.CustomAttributes);

            if (classQuarantined && !classHasBead)
            {
                yield return $"{type.FullName} (class-level trait)";
            }

            foreach (var method in type.Methods)
            {
                if (!IsQuarantined(method.CustomAttributes))
                {
                    continue;
                }

                // Satisfied by the method's own bead, or by the bead of the class that quarantined
                // it. Additionally, a method inside a class that is ITSELF already being reported
                // adds nothing: the class is the one place the missing bead belongs, so reporting
                // its methods too would turn one missing bead into a list as long as the class.
                if (HasValidBead(method.CustomAttributes) || classQuarantined)
                {
                    continue;
                }

                yield return $"{type.FullName}.{method.Name}";
            }
        }
    }

    private static bool IsQuarantined(IEnumerable<CustomAttribute> attributes) =>
        TraitValues(attributes, CategoryTraitName)
            .Any(v => string.Equals(v, QuarantineCategory, StringComparison.Ordinal));

    private static bool HasValidBead(IEnumerable<CustomAttribute> attributes) =>
        TraitValues(attributes, BeadTraitName)
            .Any(v => System.Text.RegularExpressions.Regex.IsMatch(v, BeadPattern));

    /// <summary>
    /// The values of every <c>[Trait(name, value)]</c> on a member with the given trait name.
    /// Matched on the attribute type name rather than a full assembly-qualified name so the walk
    /// does not break when xunit's assembly identity changes, and read positionally because
    /// <c>TraitAttribute</c>'s two arguments are constructor arguments, not properties.
    /// </summary>
    private static IEnumerable<string> TraitValues(IEnumerable<CustomAttribute> attributes, string traitName)
    {
        foreach (var attribute in attributes)
        {
            if (!string.Equals(attribute.AttributeType.Name, "TraitAttribute", StringComparison.Ordinal))
            {
                continue;
            }

            if (attribute.ConstructorArguments.Count != 2)
            {
                continue;
            }

            if (attribute.ConstructorArguments[0].Value is not string name ||
                attribute.ConstructorArguments[1].Value is not string value)
            {
                continue;
            }

            if (string.Equals(name, traitName, StringComparison.Ordinal))
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Bait for the two non-vacuity proofs above: the only <c>Category=Quarantine</c> traits
    /// anywhere in the test tree, present so the scan can be proven to find a violation and to
    /// accept a compliant case. These are PRIVATE, NESTED and FACTLESS on purpose — no runner ever
    /// collects them, so the shipped tree contains no actually-quarantined test (arb-rga.5 requires
    /// zero at cutover) while the rule is still proven to bite. Only their attribute rows are read.
    /// </summary>
    private static class Bait
    {
        /// <summary>The violation: quarantined at class level with no bead to track it.</summary>
        [Trait(CategoryTraitName, QuarantineCategory)]
        internal sealed class QuarantinedClassWithoutABead
        {
            // Carries the trait ITSELF, not merely by being inside the class above. That is what
            // makes the "not reported separately" assertion bite: a method with no trait of its own
            // is skipped at the IsQuarantined check whatever the dedup logic does, so asserting its
            // absence would prove nothing. This one reaches the dedup branch for real.
            [Trait(CategoryTraitName, QuarantineCategory)]
            internal void AMethodInheritingTheClassTrait()
            {
            }
        }

        /// <summary>The other violation shape: quarantined at method level with no bead.</summary>
        internal sealed class QuarantinedMethodWithoutABead
        {
            [Trait(CategoryTraitName, QuarantineCategory)]
            internal void AQuarantinedMethod()
            {
            }
        }

        /// <summary>The compliant case: both traits present, so it must never be reported.</summary>
        [Trait(CategoryTraitName, QuarantineCategory)]
        [Trait(BeadTraitName, "arb-rga")]
        internal sealed class QuarantinedWithABead
        {
        }
    }

    // The parent-walk that resolves a sibling test assembly's build output lives once, on
    // TestProcessGlobalStateTests.ResolveTestAssemblyPath — see its remarks for why the walk depth
    // and the configuration/TFM-preserving logic must not be duplicated here.
    private static string? ResolveTestAssemblyPath(string assemblyName) =>
        TestProcessGlobalStateTests.ResolveTestAssemblyPath(assemblyName);
}
