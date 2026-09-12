using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-cz6: the optional ratchet from the #203 (arb-ul4) architectural review. arb-ul4 demoted 19
/// unresolved/ambiguous <c>&lt;see cref&gt;</c> doc comments to plain <c>&lt;c&gt;Name&lt;/c&gt;</c>
/// text — the correct fix at the time, since a cref to a private member, a namespace, or a type in
/// a project this one may not reference cannot be made to resolve without widening accessibility or
/// adding a forbidden project reference (see the root <c>Directory.Build.props</c> comment, which
/// states the repoint-vs-demote rule this test ratchets). But <c>&lt;c&gt;</c> text is inert: once a
/// referent is renamed or deleted, CS1574 can no longer catch it, and the doc comment silently
/// starts pointing at nothing.
///
/// <para><b>What this scans.</b> Every fully qualified <c>Arbitarr.</c>-prefixed dotted name
/// inside a code (<c>c</c>) tag in every <c>src/**/*.cs</c> and <c>tests/**/*.cs</c> doc comment,
/// source-grepped rather than reflected — the same reasoning as
/// <see cref="StandardsQuoteSourceTests"/>: this scan is about what the COMMENT TEXT says, which
/// reflection over compiled members cannot see. Each name is then resolved by REFLECTION over the
/// assemblies named in <see cref="BuiltAssemblies.ProductionAssemblyNames"/> and
/// <see cref="BuiltAssemblies.TestAssemblyNames"/>, plus <c>Arbitarr.TestSupport</c> (referenced by
/// name in <see cref="TestProcessGlobalStateTests"/>'s own remarks but absent from both lists,
/// since it is not itself a test project — see <see cref="BuiltAssemblies"/>).</para>
///
/// <para><b>Namespaces, assembly names, and file names are excluded, not resolved.</b> A name that
/// is EXACTLY a loaded assembly's name (e.g. <c>Arbitarr.Core</c>), a NAMESPACE with no type of its
/// own name (e.g. <c>Arbitarr.Core.Filtering</c>, which names a folder of types rather than one),
/// or ends in <c>.csproj</c> (a project FILE, not a symbol) names something other than a type or
/// member — reflection has nothing to resolve any of those to, and demoting a cref to name a
/// project, a namespace, or a file was never the concern arb-ul4's ratchet was for. Anything else is
/// asserted to resolve to either a TYPE (the whole dotted name) or a MEMBER (declaring type = all
/// but the last segment, member name = the last segment) in one of the loaded assemblies.</para>
///
/// <para><b>Why this can't just require a cref.</b> Some of these names are deliberately NOT
/// resolvable in the source language sense even though the type exists — e.g. a member that exists
/// but is unreachable from a project that cannot reference it, which is exactly the case arb-ul4
/// demoted. This test does not re-litigate that decision; it only checks that the plain-text name
/// still names something real ANYWHERE in the loaded assemblies, which is the weaker, permanent
/// property <c>&lt;c&gt;</c> text can still promise.</para>
///
/// <para><b>One <c>[Fact]</c>, deliberately not a <c>[Theory]</c> with one case per discovered
/// name.</b> A per-name <c>[Theory]</c> makes the EXECUTED TEST COUNT a function of doc comment
/// text: the day someone legitimately reworks or deletes a comment that happened to mention an
/// <c>Arbitarr.X</c> name, the discovered set shrinks, the executed count drops, and CI's
/// test-count ratchet (see process.md's Test-count floors) has no mechanism for an intentional
/// decrease (that is owner-gated, arb-dq7l) — so a wholly unrelated, correct doc edit would block
/// the PR. Collecting every unresolved name into ONE assertion keeps the executed count fixed at
/// exactly two (this fact plus the non-vacuity fact below) no matter how many names are discovered,
/// so the ratchet only ever reacts to a REAL regression (a resolver bug, or a genuinely broken
/// reference), never to the size of the discovered set. Do not "simplify" this back into a
/// <c>[Theory]</c> — that reintroduces the coupling this remark exists to rule out.</para>
/// </summary>
public class DemotedDocCrefResolutionTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex FullyQualifiedNameInCTag = new(
        @"<c>(?<name>Arbitarr\.[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+)</c>",
        RegexOptions.None,
        RegexTimeout);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Arbitarr.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate Arbitarr.sln by walking up from {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// Every fully qualified "Arbitarr.Xxx.Yyy"-shaped name found inside a <c>&lt;c&gt;</c> tag,
    /// across every <c>.cs</c> file under <c>src/</c> and <c>tests/</c>, paired with the relative
    /// path of the file it came from (for a readable failure message). Read as source text, not
    /// compiled IL or reflection, because the object under test is the doc COMMENT, which a
    /// resolved member cannot point back to. Returned as a materialised list (not yielded lazily)
    /// so the one <c>[Fact]</c> below can iterate it once to build a single failure message —
    /// see the class remarks for why this is a <c>[Fact]</c> and not a <c>[Theory]</c>.
    /// </summary>
    private static List<(string Name, string RelativePath)> DemotedNames()
    {
        var repoRoot = FindRepoRoot();
        var found = new SortedSet<(string Name, string RelativePath)>(
            Comparer<(string Name, string RelativePath)>.Create(
                (a, b) =>
                {
                    var byName = string.CompareOrdinal(a.Name, b.Name);
                    return byName != 0 ? byName : string.CompareOrdinal(a.RelativePath, b.RelativePath);
                }));

        foreach (var root in new[] { "src", "tests" })
        {
            var rootDir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(rootDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories))
            {
                // bin/obj carry copies of source via generated files in some SDKs; skip them so a
                // stale build artifact cannot introduce or hide a finding this scan is meant to
                // catch from the real source tree.
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (Match match in FullyQualifiedNameInCTag.Matches(text))
                {
                    found.Add((match.Groups["name"].Value, Path.GetRelativePath(repoRoot, file)));
                }
            }
        }

        // Non-vacuity guard (CLAUDE.md section 4): if the scan ever found nothing, that is the scan
        // being broken, not the tree being clean of demoted names — arb-ul4 alone demoted 19.
        Assert.True(found.Count > 0, "Expected to find at least one \"Arbitarr.Xxx.Yyy\"-shaped doc comment name.");

        return found.ToList();
    }

    /// <summary>
    /// The assemblies this test can resolve a name against, loaded once per test run via
    /// <see cref="Lazy{T}"/> rather than once per name checked.
    /// </summary>
    private static readonly Lazy<Assembly[]> LoadedAssemblies = new(LoadAssemblies);

    private static Assembly[] LoadAssemblies()
    {
        var namesToLoad = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => ("src", name))
            .Concat(BuiltAssemblies.TestAssemblyNames.Select(name => ("tests", name)))
            .Append(("tests", "Arbitarr.TestSupport"));

        var assemblies = new List<Assembly>();
        var missing = new List<string>();

        foreach (var (root, name) in namesToLoad)
        {
            var path = BuiltAssemblies.ResolveAssemblyPath(root, name);
            if (path is null)
            {
                missing.Add($"{root}/{name}");
                continue;
            }

            assemblies.Add(Assembly.LoadFrom(path));
        }

        // Loud failure, not a vacuous pass (CLAUDE.md section 4): a name this scan cannot check
        // because its assembly failed to load must not be silently treated as resolved.
        Assert.True(
            missing.Count == 0,
            "Could not locate the built assembly for: " + string.Join(", ", missing) +
            ". This scan reads compiled output, so the solution must be built first.");

        return assemblies.ToArray();
    }

    /// <summary>
    /// Resolves <paramref name="fullyQualifiedName"/> against <see cref="LoadedAssemblies"/> as
    /// either a type (the whole name) or a member (declaring type = all but the last segment,
    /// member = the last segment). A bare assembly name, a namespace with no type of the same name,
    /// or a <c>.csproj</c> file reference is treated as OUT OF SCOPE, not a failure — see the class
    /// remarks for why. The type and member lookups run BEFORE the namespace check, so a name that
    /// is both a namespace and a type (e.g. a type later moved so its own name now also names a
    /// namespace) still resolves via the type branch instead of being silently excluded; only a
    /// name that resolves to neither falls through to the namespace check.
    ///
    /// <para><b>Not a Roslyn analyzer.</b> A Roslyn <see cref="Microsoft.CodeAnalysis"/> semantic
    /// model would resolve these names with the compiler's own binding rules instead of
    /// approximating them by reflection, but that means a new analyzer project plus per-csproj
    /// packaging to wire it into every project that carries a demoted name — a structural cost this
    /// one ratchet does not justify, and one with no precedent elsewhere in the repo.</para>
    ///
    /// <para><b>DeclaredOnly and other resolver gaps.</b> <c>BindingFlags.DeclaredOnly</c> below
    /// means an INHERITED member — e.g. <c>SettingsValidationException.Message</c>, which today
    /// stays a <c>&lt;see cref&gt;</c> rather than being demoted (see <see cref="BuiltAssemblies"/>'s
    /// remarks) — would resolve as a false FAILURE if it were ever demoted to <c>&lt;c&gt;</c> text,
    /// since the member lookup only sees members declared directly on the named type, not ones
    /// inherited from a base class. Generic types cited without their backtick arity (e.g.
    /// <c>List</c> instead of <c>List\`1</c>) and doubly-nested types likewise fail to resolve. Zero
    /// names in the current demoted set hit either gap; a loud failure is the right direction for a
    /// ratchet that only ever needs to catch a real regression, not to model every corner of the
    /// resolver it approximates.</para>
    /// </summary>
    private static bool ResolvesToTypeOrMember(string fullyQualifiedName, out bool isOutOfScope)
    {
        isOutOfScope = LoadedAssemblies.Value.Any(a =>
            string.Equals(a.GetName().Name, fullyQualifiedName, StringComparison.Ordinal));
        if (isOutOfScope)
        {
            return true;
        }

        if (fullyQualifiedName.EndsWith(".csproj", StringComparison.Ordinal))
        {
            isOutOfScope = true; // out-of-scope for the same reason: not a code symbol.
            return true;
        }

        foreach (var assembly in LoadedAssemblies.Value)
        {
            // Whole name as a type (including a nested type spelled with '.', which .NET's own
            // GetType requires as '+' — tried below as the member fallback instead).
            if (assembly.GetType(fullyQualifiedName, throwOnError: false) is not null)
            {
                return true;
            }
        }

        var lastDot = fullyQualifiedName.LastIndexOf('.');
        if (lastDot >= 0)
        {
            var declaringTypeName = fullyQualifiedName[..lastDot];
            var memberName = fullyQualifiedName[(lastDot + 1)..];

            foreach (var assembly in LoadedAssemblies.Value)
            {
                var declaringType = assembly.GetType(declaringTypeName, throwOnError: false);
                if (declaringType is null)
                {
                    continue;
                }

                const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

                if (declaringType.GetMember(memberName, AllMembers).Length > 0)
                {
                    return true;
                }

                // Enum values surface as static fields, already covered by GetMember above, but a
                // nested type spelled with '.' (Outer.Inner) needs '+' for Type.GetType, which the
                // whole-name attempt above could not have tried.
                if (assembly.GetType(declaringTypeName + "+" + memberName, throwOnError: false) is not null)
                {
                    return true;
                }
            }
        }

        // Reached only once the type and member lookups above have both failed, so a name that is
        // both a namespace and a type resolves via the type branch above, never here.
        if (LoadedAssemblies.Value.Any(a => a.GetTypes().Any(t =>
                string.Equals(t.Namespace, fullyQualifiedName, StringComparison.Ordinal))))
        {
            isOutOfScope = true; // a namespace, not a type or member — see class remarks.
            return true;
        }

        return false;
    }

    /// <summary>
    /// A single <c>[Fact]</c> over the WHOLE discovered set — see the class remarks for why this is
    /// not a <c>[Theory]</c> with one case per name. Collects every unresolved name, rather than
    /// failing on the first one, so a single test run reports every finding at once.
    /// </summary>
    [Fact]
    public void Every_Demoted_Name_Still_Resolves_To_A_Type_Or_Member()
    {
        var names = DemotedNames();
        var outOfScopeCount = 0;
        var unresolved = new List<string>();

        foreach (var entry in names)
        {
            if (!ResolvesToTypeOrMember(entry.Name, out var isOutOfScope))
            {
                unresolved.Add($"'{entry.Name}' (cited in {entry.RelativePath})");
            }
            else if (isOutOfScope)
            {
                outOfScopeCount++;
            }
        }

        Assert.True(
            unresolved.Count == 0,
            $"Checked {names.Count} demoted name(s), {outOfScopeCount} out of scope (namespace, " +
            "assembly name, or .csproj file). The following no longer resolve to a type or member " +
            "in any loaded Arbitarr.* assembly. Each was written as plain text specifically because " +
            "arb-ul4 could not make it a <see cref> (see Directory.Build.props), which means CS1574 " +
            "can no longer catch it going stale — the referent was likely renamed, deleted, or moved. " +
            "Update the doc comment; do not silently delete this finding.\n  " +
            string.Join("\n  ", unresolved));
    }

    /// <summary>
    /// Positive control (CLAUDE.md section 4): proves <see cref="ResolvesToTypeOrMember"/> actually
    /// fails on a name that does not exist, so <see cref="Every_Demoted_Name_Still_Resolves_To_A_Type_Or_Member"/>
    /// is not passing vacuously because the resolver always returns true.
    /// </summary>
    [Fact]
    public void Resolver_Fails_On_A_Planted_Nonexistent_Name()
    {
        var resolved = ResolvesToTypeOrMember("Arbitarr.Nope.Missing", out var isOutOfScope);

        Assert.False(isOutOfScope);
        Assert.False(resolved);
    }
}
