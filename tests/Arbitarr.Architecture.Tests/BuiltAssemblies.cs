namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-hxa: the single definition of "which assemblies this project's IL scans read", shared by
/// <see cref="TestProcessGlobalStateTests"/>, <see cref="QuarantineTraitTests"/>,
/// <see cref="ProductionProcessGlobalStateTests"/> and <see cref="NoInlineDatabaseConnectionStringsTests"/>.
/// Before this class, the ten-name test-assembly list was spelled out twice
/// (<see cref="TestProcessGlobalStateTests"/> and <see cref="QuarantineTraitTests"/>) and the
/// path-resolution walk lived on <see cref="TestProcessGlobalStateTests"/> with four out-of-class
/// consumers — two things that could silently drift apart instead of one definition each.
///
/// <para><b>Why the lists are SPELLED OUT and not discovered from the tree.</b> A glob over
/// whatever happens to be on disk would silently skip a project that had not been built yet, and a
/// silently skipped project is a vacuous pass, not a clean scan — a count assertion cannot detect
/// an unbuilt project. Naming every assembly makes a new one a deliberate addition here.</para>
///
/// <para><b>Independent of build-test.yml's TEST_ASSEMBLIES.</b> The CI workflow's
/// <c>TEST_ASSEMBLIES</c> token is a deliberate INDEPENDENT oracle: its guards job diffs
/// <c>TEST_ASSEMBLIES</c> itself against the tests/ tree, diffs <see cref="TestAssemblyNames"/>
/// against <c>TEST_ASSEMBLIES</c> (arb-hbt), and separately diffs <see cref="ProductionAssemblyNames"/>
/// against the Architecture.Tests project-reference graph (arb-9ae). The three are intentionally not
/// merged into one source of truth — a drift between any pair is exactly what each guard exists to
/// catch, so collapsing them would remove a check rather than simplify it. If you rename or move
/// either array here, the workflow's extraction (a narrow sed range keyed to this class's shape, not
/// a C# parser) must be repointed at the new location in the same change, or its guard goes vacuous.</para>
///
/// <para><b>Why this walk is not on Arbitarr.TestSupport.</b> The walk in
/// <see cref="ResolveAssemblyPath"/> is anchored on THIS assembly's own
/// <see cref="AppContext.BaseDirectory"/>, so the parent depth it climbs is only correct for a
/// project that sits at <c>tests/&lt;Project&gt;/bin/&lt;Configuration&gt;/&lt;tfm&gt;/</c> — moving
/// it to a shared helper would make the depth depend on where the CALLER's own output directory
/// happens to sit, not on this one. Arbitarr.TestSupport is also not that caller: its csproj sets
/// <c>IsTestProject=false</c> and hosts no test run (so it has no bin output rooted the way this walk
/// assumes), and it references only <c>Microsoft.Data.Sqlite</c> — deliberately no Arbitarr project
/// names — so a scan helper that names every Arbitarr assembly by convention does not belong there.</para>
/// </summary>
internal static class BuiltAssemblies
{
    /// <summary>
    /// Every test assembly in the solution, by project name. Spelled out rather than globbed so
    /// that a NEW test project is a deliberate addition here: a glob over whatever happens to be on
    /// disk would silently skip a project that had not been built, which is the vacuous pass this
    /// list exists to avoid. Arbitarr.TestSupport is absent on purpose — it is not a test project
    /// (IsTestProject=false) and it is where the sanctioned scoped replacements live.
    /// </summary>
    internal static readonly string[] TestAssemblyNames =
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

    /// <summary>
    /// Every production assembly, by project name. Spelled out for the same reason as
    /// <see cref="TestAssemblyNames"/>: a glob over whatever happens to be on disk silently skips a
    /// project that was not built, and a silently skipped project is a vacuous pass. Arbitarr.Web is
    /// absent because it is the React frontend, which builds no managed assembly.
    ///
    /// <para><b>build-test.yml's guards job parses this declaration by LINE SHAPE</b> (arb-9ae): a
    /// narrow sed address range from the opening <c>internal static readonly string[]
    /// ProductionAssemblyNames =</c> line (nothing else on that line) to the closing bare
    /// <c>];</c> line, reading only <c>"Name",</c> entries in between. Do not reformat this
    /// declaration (a different indent, a trailing comment, an inline initializer, an
    /// <c>ImmutableArray</c>, a visibility change, etc.) without also checking that guards step
    /// still finds all eight names — a shape change makes the extraction find ZERO names, which
    /// fails the guard loudly rather than silently, but still blocks CI until the extraction is
    /// updated to match.</para>
    /// </summary>
    internal static readonly string[] ProductionAssemblyNames =
    [
        "Arbitarr.Ai",
        "Arbitarr.Api",
        "Arbitarr.Core",
        "Arbitarr.Core.Identity",
        "Arbitarr.Data",
        "Arbitarr.Host",
        "Arbitarr.Media",
        "Arbitarr.Sources.NzbHydra",
    ];

    /// <summary>
    /// Finds a test project's built assembly by walking from this assembly's own output directory
    /// to the sibling project's, preserving the configuration and target-framework segments — so
    /// the scan works unchanged under <c>-c Release</c>, which CI uses.
    /// </summary>
    internal static string? ResolveTestAssemblyPath(string assemblyName) =>
        ResolveAssemblyPath("tests", assemblyName);

    /// <summary>
    /// Number of parent directories between <see cref="AppContext.BaseDirectory"/> and the
    /// repository root, for a project shaped <c>&lt;root&gt;/tests/&lt;Project&gt;/bin/
    /// &lt;Configuration&gt;/&lt;tfm&gt;/</c> — five segments below root: tfm, Configuration, bin,
    /// Project, tests.
    /// </summary>
    private const int ParentsFromAssemblyDirectoryToRepositoryRoot = 5;

    /// <summary>
    /// Finds a built assembly under <paramref name="rootDirectoryName"/> (<c>tests</c> or
    /// <c>src</c>), preserving this assembly's own configuration and target-framework segments.
    ///
    /// <para>Shared rather than duplicated per scan: two copies of this walk would drift, and the
    /// one that drifted would start returning null and fail loudly — or worse, silently scan
    /// nothing if a caller ever treated null as "clean". The five-parent depth and the
    /// configuration-from-parent-name logic exist exactly once, here.</para>
    ///
    /// <para><b>Why the assemblies are found by PATH.</b> Adding the test or production projects as
    /// ProjectReferences here fails with NU1605 for at least one of them (Arbitarr.Host), so every
    /// scan that needs Host opens the build output directly with
    /// <see cref="Mono.Cecil.ModuleDefinition.ReadModule(string)"/> instead of via a reference. That
    /// makes every scan depend on the referenced assemblies having been BUILT before this project's
    /// tests run — <c>dotnet build</c> at the solution level does that, and the CI job must keep
    /// doing so. A missing assembly therefore FAILS the scan loudly rather than passing vacuously
    /// over an empty set.</para>
    /// </summary>
    internal static string? ResolveAssemblyPath(string rootDirectoryName, string assemblyName)
    {
        // .../tests/Arbitarr.Architecture.Tests/bin/<configuration>/<tfm>/
        var startDirectory = AppContext.BaseDirectory;
        var here = new DirectoryInfo(startDirectory);
        var targetFramework = here.Name;
        var configuration = here.Parent?.Name;

        var repositoryRoot = here;
        for (var i = 0; i < ParentsFromAssemblyDirectoryToRepositoryRoot && repositoryRoot is not null; i++)
        {
            repositoryRoot = repositoryRoot.Parent;
        }

        if (configuration is null || repositoryRoot is null)
        {
            return null;
        }

        if (!File.Exists(Path.Combine(repositoryRoot.FullName, "Arbitarr.sln")))
        {
            throw new InvalidOperationException(
                $"BuiltAssemblies.ResolveAssemblyPath walked {ParentsFromAssemblyDirectoryToRepositoryRoot} " +
                "parent directories up from " +
                $"AppContext.BaseDirectory ('{startDirectory}') and landed on " +
                $"'{repositoryRoot.FullName}', which does not contain Arbitarr.sln. This means the walk " +
                "depth no longer matches this assembly's output directory shape, not that the target " +
                "assembly was not built — fix ParentsFromAssemblyDirectoryToRepositoryRoot.");
        }

        var candidate = Path.Combine(
            repositoryRoot.FullName,
            rootDirectoryName,
            assemblyName,
            "bin",
            configuration,
            targetFramework,
            assemblyName + ".dll");

        return File.Exists(candidate) ? candidate : null;
    }
}
