using System.Reflection;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-x7w8.2: a source adapter depends on Core and on nothing else of ours.
///
/// <para><b>What the rule buys.</b> The upstream contract, the wire parser and the refusal
/// exceptions all live in Core; an adapter that additionally referenced Data would couple a
/// wire-protocol implementation to the persistence schema, so adding a column nothing in the
/// adapter reads could still force it to rebuild — and, worse, would make it tempting to read the
/// <c>Sources</c> row directly rather than through the options record the registry (arb-x7w8.4)
/// projects. An adapter that referenced Api or Host would invert the dependency direction outright.
/// A reference to a SIBLING adapter is banned for its own reason: it would make every future direct
/// indexer depend on the NZBHydra2 aggregator's project, which is why the shared rate limiter and
/// parser were moved into Core rather than referenced across.</para>
/// </summary>
public class SourceAdapterIsolationTests
{
    /// <summary>
    /// The only Arbitarr assembly a source adapter may reference. Spelled out rather than
    /// expressed as a deny-list so a NEW Arbitarr project is refused by default: a deny-list would
    /// silently admit anything nobody thought to name.
    /// </summary>
    private const string PermittedReference = "Arbitarr.Core";

    private static Assembly LoadAdapter(string assemblyName) =>
        Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));

    private static string[] ArbitarrReferencesOf(string assemblyName) =>
        LoadAdapter(assemblyName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null && name.StartsWith("Arbitarr.", StringComparison.Ordinal))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void Newznab_Adapter_References_Only_Core()
    {
        Assert.Equal(new[] { PermittedReference }, ArbitarrReferencesOf("Arbitarr.Sources.Newznab"));
    }

    [Fact]
    public void NzbHydra_Adapter_References_Only_Core()
    {
        Assert.Equal(new[] { PermittedReference }, ArbitarrReferencesOf("Arbitarr.Sources.NzbHydra"));
    }

    /// <summary>
    /// Non-vacuity control for the two assertions above. They are equality assertions against a
    /// ONE-element list built by a filter, and a filter that matched nothing would satisfy neither
    /// — but a filter that could only ever yield one name would satisfy both while proving nothing
    /// about the isolation. This shows the same extraction DOES report several names when an
    /// assembly has several: Arbitarr.Media references Core, Core.Identity and Data.
    ///
    /// <para>The assertion is on the COUNT, not on Media's exact reference set. Pinning the set
    /// here would make an unrelated change to Media's references fail this file, which would say
    /// nothing about source-adapter isolation — the property the control needs is only that more
    /// than one name can come back.</para>
    /// </summary>
    [Fact]
    public void ReferenceExtraction_Observes_MultipleArbitarrReferences_ForAnAssemblyThatHasThem()
    {
        var mediaReferences = ArbitarrReferencesOf("Arbitarr.Media");

        Assert.True(
            mediaReferences.Length > 1,
            "Expected the reference extraction to report more than one Arbitarr reference for " +
            $"Arbitarr.Media, but it reported: {string.Join(", ", mediaReferences)}. If this is " +
            "empty or singular the extraction is broken, and the isolation assertions above are " +
            "passing vacuously rather than because the adapters are isolated.");
    }
}
