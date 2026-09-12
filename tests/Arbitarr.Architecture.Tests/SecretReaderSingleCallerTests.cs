using System.Text.RegularExpressions;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// CLAUDE.md §1 and docs/standards/architecture.md: each
/// <c>ReadApiKeyForUpstreamRequestAsync</c> has exactly ONE production caller. The guarantee IS
/// the call-site count — a second caller is a second place to audit — so this counts them.
/// </summary>
/// <remarks>
/// <para><b>WHY A SOURCE SCAN RATHER THAN A REFLECTION TEST.</b> The rule is about how many places
/// in the code invoke the reader, which is a property of the source text; IL analysis could find
/// the call sites too, but it would report them as compiler-shaped method names inside async state
/// machines, which is exactly the form a person auditing this cannot read. The scan reports the
/// file and line, so a failure names the new caller.</para>
///
/// <para><b>WHY IT IS WORTH A TEST AT ALL.</b> arb-u1c is the reason. The comments claiming "the
/// one place" stayed word for word correct-looking while a second caller was added beside them, and
/// three reviews read those comments rather than counting. A comment cannot fail; this can.</para>
///
/// <para>Test sources are excluded: a repository test legitimately calls the reader to prove it
/// returns what was stored, and that is not a path any request reaches.</para>
/// </remarks>
public class SecretReaderSingleCallerTests
{
    /// <summary>
    /// The complete set of production files allowed to invoke ANY
    /// <c>ReadApiKeyForUpstreamRequestAsync</c> — one file per reader, and one call inside it.
    /// </summary>
    /// <remarks>
    /// Held as one set rather than one assertion per reader because the readers share a method name,
    /// so a per-reader scan cannot tell whose call it is looking at without resolving the receiver's
    /// type — and a regex that guesses at that would fail open, which is the one thing a guard must
    /// never do. Counting every call site of the name and pinning the whole set is stricter than the
    /// rule requires and cannot be fooled by a rename.
    /// </remarks>
    private static readonly (string File, string Reader, string Why)[] AllowedCallSites =
    [
        // The Sonarr instance key. Since arb-u1c the sole caller is SonarrCredentialProvider, which
        // hands a credential to BOTH the admin connectivity probe and the search path's identity
        // resolver — neither of them reads the key itself. Wiring the resolver straight to the
        // repository is what made two callers, and is the defect this test exists to catch.
        ("src/Arbitarr.Data/Media/SonarrCredentialProvider.cs", "ArrInstanceRepository",
            "the single owner that hands a SonarrCredential to the probe and the resolver"),

        // arb-6l9b.1: the Radarr instance key. Its sole caller is RadarrCredentialProvider, which
        // exists at the FIRST consumer rather than waiting for the second — Sonarr's history above is
        // that the obvious wiring at the second consumer produces two callers, which is precisely the
        // count this guard is made of. The queue and movie-library readers that follow take a
        // RadarrCredential from the provider; none of them reads the key itself.
        ("src/Arbitarr.Data/Media/RadarrCredentialProvider.cs", "RadarrInstanceRepository",
            "the single owner that hands a RadarrCredential to the probe and the library readers"),

        // The per-source upstream key. Since arb-x7w8.3 the sole caller is SourceCredentialProvider,
        // not the connectivity probe that used to read it directly: N runtime-added indexers mean
        // the search path needs a per-source key too, and the startup-resolved
        // ResolvedSourceConfiguration cannot supply one, so wiring both consumers to the repository
        // would make two callers. This entry moved rather than gaining a sibling — the rule is one
        // caller per reader, and the provider is it (ADR 0018).
        ("src/Arbitarr.Data/Sources/SourceCredentialProvider.cs", "SourceRepository",
            "the single owner that hands a SourceCredential to the probe and the search path"),
    ];

    [Fact]
    public void Every_secret_reader_has_exactly_one_production_call_site()
    {
        var callSites = FindCallSites(FindRepositoryRoot());

        static string Normalise(string path) => path.Replace('\\', '/');

        // The POSITIVE CONTROL, and it is the whole point: an empty scan satisfies "no unexpected
        // callers" exactly as happily as a correct one. Each allowed call site must be FOUND before
        // any absence is asserted, so a scan that has stopped matching anything fails here rather
        // than passing silently.
        foreach (var allowed in AllowedCallSites)
        {
            Assert.True(
                callSites.Any(site => Normalise(site.File).EndsWith(allowed.File, StringComparison.Ordinal)),
                $"The scan found no call to ReadApiKeyForUpstreamRequestAsync in {allowed.File}, "
                + $"which is supposed to be {allowed.Reader}'s single caller ({allowed.Why}). "
                + "Either the call moved — update this table — or the scan is broken, in which case "
                + "every absence assertion below is vacuous.");
        }

        var unexpected = callSites
            .Where(site => !AllowedCallSites.Any(a => Normalise(site.File).EndsWith(a.File, StringComparison.Ordinal)))
            .Select(site => $"{Normalise(site.File)}:{site.Line}")
            .ToList();

        Assert.True(
            unexpected.Count == 0,
            "ReadApiKeyForUpstreamRequestAsync must have exactly one production caller per reader. "
            + $"Unexpected call sites:{Environment.NewLine}"
            + string.Join(Environment.NewLine, unexpected)
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "See CLAUDE.md section 1: the guarantee IS the call-site count. Route the new consumer "
            + "through the existing owner (as SonarrCredentialProvider does for two consumers) rather "
            + "than adding a caller.");

        // One call per allowed file, not merely one file: a second call inside the sanctioned file
        // is a second place to audit too, and would otherwise pass the set check above.
        foreach (var allowed in AllowedCallSites)
        {
            var inFile = callSites.Count(site => Normalise(site.File).EndsWith(allowed.File, StringComparison.Ordinal));
            Assert.True(
                inFile == 1,
                $"{allowed.File} calls ReadApiKeyForUpstreamRequestAsync {inFile} times; exactly one "
                + "call is allowed.");
        }
    }

    /// <summary>
    /// Call sites, excluding the declaration itself and every documentation comment mentioning the
    /// method by name — a <c>&lt;see cref&gt;</c> is not a call, and there are several.
    /// </summary>
    private static List<(string File, int Line)> FindCallSites(string repositoryRoot)
    {
        // An invocation is the name followed by "(" and preceded by a "." — which the declaration
        // ("public Task<string?> ReadApiKey...") is not, and a comment line is excluded outright.
        var invocation = new Regex(@"\.ReadApiKeyForUpstreamRequestAsync\s*\(", RegexOptions.Compiled);
        var results = new List<(string, int)>();

        var sourceRoot = Path.Combine(repositoryRoot, "src");
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Build output under bin/ and obj/ is a copy of sources already scanned.
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/bin/", StringComparison.Ordinal)
                || normalized.Contains("/obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (invocation.IsMatch(line))
                {
                    results.Add((file, i + 1));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Walks up from the test binaries to the directory holding <c>src</c>, so the scan does not
    /// depend on where the runner was invoked from.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (a directory containing both src/ and CLAUDE.md) "
            + $"walking up from {AppContext.BaseDirectory}.");
    }
}
