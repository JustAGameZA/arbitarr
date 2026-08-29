using System.Text.RegularExpressions;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// M0 companion to <see cref="AssemblyNamingTests"/>. That test can only see assemblies already
/// copied alongside the test binaries via (possibly transitive) ProjectReference - currently
/// Arbitarr.Core, Arbitarr.Core.Identity, Arbitarr.Ai, Arbitarr.Media, and Arbitarr.Data. Four of
/// the eight src projects (Arbitarr.Api, Arbitarr.Host, Arbitarr.Sources.NzbHydra are three of
/// them) are never loaded that way, so a reflection-only check leaves them unreachable. This test
/// closes that gap by scanning the repo source tree as text instead: the solution's project
/// entries, every src/**/*.csproj filename, and any &lt;AssemblyName&gt;/&lt;RootNamespace&gt;
/// override inside those project files.
/// </summary>
public class SourceTreeNamingTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex AssemblyNameOverride = new(
        @"<AssemblyName>\s*(?<value>[^<]+?)\s*</AssemblyName>",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex RootNamespaceOverride = new(
        @"<RootNamespace>\s*(?<value>[^<]+?)\s*</RootNamespace>",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex SlnProjectEntry = new(
        @"^Project\(""\{[0-9A-Fa-f-]+\}""\)\s*=\s*""(?<name>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.Multiline,
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

    [Fact]
    public void No_Solution_Project_Or_Csproj_Name_Starts_With_ArrSearcher()
    {
        var repoRoot = FindRepoRoot();
        var violations = new List<string>();

        // 1. Solution project entries (covers project display names, including any not
        // discoverable via reflection because nothing else in the solution references them).
        var slnPath = Path.Combine(repoRoot, "Arbitarr.sln");
        Assert.True(File.Exists(slnPath), $"Expected to find {slnPath}.");

        var slnContent = File.ReadAllText(slnPath);
        foreach (Match match in SlnProjectEntry.Matches(slnContent))
        {
            var name = match.Groups["name"].Value;
            if (name.StartsWith("ArrSearcher", StringComparison.Ordinal))
            {
                violations.Add($"Arbitarr.sln project entry '{name}'");
            }
        }

        // 2. Every src/**/*.csproj filename.
        var srcDir = Path.Combine(repoRoot, "src");
        Assert.True(Directory.Exists(srcDir), $"Expected to find {srcDir}.");

        var csprojPaths = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories);
        Assert.True(csprojPaths.Length > 0, $"Expected to find at least one .csproj under {srcDir}.");

        foreach (var csprojPath in csprojPaths)
        {
            var fileName = Path.GetFileNameWithoutExtension(csprojPath);
            if (fileName.StartsWith("ArrSearcher", StringComparison.Ordinal))
            {
                violations.Add($"csproj file '{Path.GetFileName(csprojPath)}'");
            }

            // 3. Any <AssemblyName>/<RootNamespace> override inside the project file - these
            // silently rename the built assembly/namespace independent of the file/folder name.
            var content = File.ReadAllText(csprojPath);

            foreach (Match match in AssemblyNameOverride.Matches(content))
            {
                var value = match.Groups["value"].Value;
                if (value.StartsWith("ArrSearcher", StringComparison.Ordinal))
                {
                    violations.Add($"<AssemblyName> override '{value}' in {Path.GetFileName(csprojPath)}");
                }
            }

            foreach (Match match in RootNamespaceOverride.Matches(content))
            {
                var value = match.Groups["value"].Value;
                if (value.StartsWith("ArrSearcher", StringComparison.Ordinal))
                {
                    violations.Add($"<RootNamespace> override '{value}' in {Path.GetFileName(csprojPath)}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "No solution project, csproj file, <AssemblyName>, or <RootNamespace> may start with " +
            $"the old working name 'ArrSearcher', but found: {string.Join("; ", violations)}");
    }
}
