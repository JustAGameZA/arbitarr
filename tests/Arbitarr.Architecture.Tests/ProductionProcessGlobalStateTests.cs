using Mono.Cecil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-n21: no PRODUCTION assembly may call <c>SqliteConnection.ClearAllPools()</c>.
///
/// <para><b>Why this is a separate ban from the test-assembly one.</b>
/// <see cref="TestProcessGlobalStateTests"/> bans the same call in TEST IL, and its class remarks
/// used to record production's restore path as a deliberate, permitted exception — it force-closed
/// every pooled connection in the process because it had to drop every handle on
/// <c>arbitarr.db</c> before swapping the file. That exception is now closed:
/// <c>Arbitarr.Data.Backup.SqlitePoolCleaner</c> clears exactly the pools that name the database
/// being replaced, so the restore no longer needs a process-global hammer — and a test that drives
/// the restore path no longer reaches one at runtime.</para>
///
/// <para>Without this test that closure is a fact about today's source, not a rule. The next
/// restore-adjacent change reaches for <c>ClearAllPools</c> exactly as readily as the last one did,
/// and the test-side ban cannot see it: it scans test IL only. The over-broad clear is what
/// produced arb-cbc/arb-5ba, and it re-enters the process through production just as easily as
/// through a test.</para>
///
/// <para><b>Scope is deliberately narrow.</b> Only <c>ClearAllPools</c>, and only <c>src/</c>.
/// <c>Environment.SetEnvironmentVariable</c> is NOT banned here — production configuration code has
/// legitimate reasons to set process environment, and the test-side ban exists because two hosts
/// starting concurrently in ONE process overwrite each other, which is a test-host problem.</para>
///
/// <para><b>Adjacent, and easy to lose in a rename: the release-GUID secret is redacted by NAME.</b>
/// <c>Arbitarr:ReleaseGuidSecret</c> (read at <c>Program.cs</c>, allow-listed below as
/// <c>ReleaseGuid._hmacKey</c>) is scrubbed out of logs and <c>/api/status</c> only because
/// <c>CredentialPatterns.NamedCredential</c>'s prefix alternation contains the literal
/// <c>secret</c> — it matches the SETTING NAME, not the value, which is ordinary base64 with no
/// distinguishing shape. Renaming the setting to anything that does not contain one of that
/// alternation's words (<c>api_key</c>, <c>apikey</c>, <c>token</c>, <c>passkey</c>,
/// <c>password</c>, <c>secret</c>) silently drops the coverage: nothing fails, and the secret
/// begins appearing verbatim in the log store. A rename must either keep a matching word or add an
/// arm in the same change.</para>
///
/// <para><b>The assembly list</b> now lives on <see cref="BuiltAssemblies.ProductionAssemblyNames"/>
/// (arb-hxa). It is DELIBERATELY INDEPENDENT of build-test.yml's own <c>TEST_ASSEMBLIES</c> token —
/// that workflow's guards job diffs its own list against this project's reference graph as a
/// separate oracle, and the two are not merged into one source of truth on purpose (a drift between
/// them is exactly what that guard exists to catch). If the workflow's guard step targets this file
/// by name or line shape, and this array has since moved, the guard's extraction must be repointed
/// at <see cref="BuiltAssemblies"/> in the same change or it fails to find the array at all.</para>
/// </summary>
public class ProductionProcessGlobalStateTests
{
    private const string BannedMethod = "Microsoft.Data.Sqlite.SqliteConnection::ClearAllPools";

    [Fact]
    public void No_production_assembly_clears_every_connection_pool_in_the_process()
    {
        var assemblyPaths = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => (Name: name, Path: TestProcessGlobalStateTests.ResolveAssemblyPath("src", name)))
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

        var violations = assemblyPaths
            .SelectMany(a => TestProcessGlobalStateTests
                .FindBannedCalls(a.Path!, BannedMethod)
                .Select(v => $"{a.Name}: {v}"))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "No production assembly may call SqliteConnection.ClearAllPools(): it force-closes " +
            "every pooled connection in the process, including the log store's and any other " +
            "database's, which is what produced arb-cbc/arb-5ba. Clear the pools for one database " +
            "with Arbitarr.Data.Backup.SqlitePoolCleaner.ClearPoolsFor(databasePath) instead. " +
            "Found:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves this scan is NON-VACUOUS: the same walk, over an assembly that DOES contain the call,
    /// must report it. Without this, the assertion above would pass just as happily if the IL walk
    /// were broken, if the banned name were misspelled, or if every path resolved to something with
    /// no method bodies — the shape CLAUDE.md section 4 records as having shipped three times.
    ///
    /// <para>The bait is <c>TestProcessGlobalStateTests.Bait</c>, which already exists for the
    /// test-side ban and calls <c>ClearAllPools</c> from a never-executed body. Reused rather than
    /// duplicated into a production assembly, because a second bait would mean shipping a real call
    /// to the banned member inside <c>src/</c> — the exact thing this test forbids. The bait lives
    /// in a test assembly and this scan never looks at test assemblies, so the two cannot collide.
    /// </para>
    /// </summary>
    [Fact]
    public void The_scan_detects_the_banned_call_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = TestProcessGlobalStateTests.FindBannedCalls(baitAssembly!, BannedMethod).ToArray();

        Assert.Contains(found, v => v.EndsWith(BannedMethod, StringComparison.Ordinal));
    }

    /// <summary>
    /// Every mutable (non-<c>readonly</c>, non-<c>const</c>) static field in a production assembly
    /// must be named here with a reason (arb-0hd0).
    ///
    /// <para><b>Why this exists.</b> <c>ReleaseGuid._hmacKey</c> was exactly such a field —
    /// rewritten by <c>ReleaseGuid.Configure</c> on every host build — and it produced arb-agh: an
    /// intermittent 404 on an issued download link, no exception, no log line, misdiagnosed twice
    /// and surviving one merged fix before being traced. Nothing was looking for it. The audit that
    /// declared the integration assembly safe to parallelise (arb-rga.4) enumerated two globals and
    /// reasoned from that list; the list was simply incomplete, and no mechanism could tell.</para>
    ///
    /// <para><b>An allow-list rather than a ban</b>, because some of these are legitimate and the
    /// point is not to forbid them — it is that adding one should be a decision somebody wrote down,
    /// not something that slips in and is discovered by a flake three months later. A new entry
    /// costs one line and a sentence saying why concurrent mutation is safe; that sentence is the
    /// whole value of the test.</para>
    /// </summary>
    [Fact]
    public void Every_mutable_static_in_production_is_allow_listed_with_a_reason()
    {
        var assemblyPaths = BuiltAssemblies.ProductionAssemblyNames
            .Select(name => (Name: name, Path: TestProcessGlobalStateTests.ResolveAssemblyPath("src", name)))
            .ToArray();

        var missing = assemblyPaths.Where(a => a.Path is null).Select(a => a.Name).ToArray();
        Assert.True(
            missing.Length == 0,
            $"Could not locate the built assembly for: {string.Join(", ", missing)}. " +
            "This scan reads IL from each production project's build output, so the solution must " +
            "be built before Arbitarr.Architecture.Tests runs.");

        var found = assemblyPaths
            .SelectMany(a => FindMutableStatics(a.Path!).Select(f => $"{a.Name}: {f}"))
            .ToArray();

        var unexpected = found.Where(f => !AllowedMutableStatics.ContainsKey(f)).ToArray();

        Assert.True(
            unexpected.Length == 0,
            "A mutable static field is process-global state: with several hosts or requests live in " +
            "one process, whoever writes it last wins, and the damage surfaces far from the write. " +
            "ReleaseGuid._hmacKey cost four CI failures that way (arb-agh/arb-0hd0). Make it " +
            "readonly, move it behind an injected dependency, or add it to AllowedMutableStatics " +
            "with a sentence saying why concurrent mutation is safe. Found:\n  " +
            string.Join("\n  ", unexpected));

        // The allow-list must not outlive what it describes: a stale entry is a claim about code
        // that no longer exists, and it would silently cover a DIFFERENT field that later takes the
        // same name.
        var stale = AllowedMutableStatics.Keys.Where(k => !found.Contains(k)).ToArray();
        Assert.True(
            stale.Length == 0,
            "AllowedMutableStatics names fields that no longer exist. Remove them; an entry that " +
            "describes nothing would silently pre-approve a future field of the same name:\n  " +
            string.Join("\n  ", stale));
    }

    /// <summary>
    /// Proves the mutable-static scan is NON-VACUOUS, in the same spirit as the bait test above: an
    /// assembly known to contain such a field must be reported. Without this, the assertion above
    /// passes just as happily if the field walk were broken or every path resolved to something with
    /// no fields — the vacuous-assertion shape CLAUDE.md section 4 records as having shipped three
    /// times. The bait is this test assembly's own <see cref="MutableStaticBait"/>.
    /// </summary>
    [Fact]
    public void The_mutable_static_scan_detects_one_when_it_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = FindMutableStatics(baitAssembly!).ToArray();

        Assert.Contains(found, f => f.EndsWith("MutableStaticBait.Counter", StringComparison.Ordinal));
    }

    /// <summary>
    /// Bait for <see cref="The_mutable_static_scan_detects_one_when_it_is_present"/> — a mutable
    /// static that exists only so the scan has something to find. It lives in a TEST assembly, which
    /// this scan never walks when checking production, so it cannot collide with the real assertion.
    /// </summary>
    internal static class MutableStaticBait
    {
        // Assigned (rather than left at its default) only to keep CS0649 quiet: the build runs at
        // zero warnings. The initialiser does not make it readonly, which is what the scan looks for.
        internal static int Counter = 1;

        /// <summary>
        /// The arb-02cc bait: a settable static auto-property, whose
        /// <c>&lt;Setting&gt;k__BackingField</c> the scan used to skip along with every other
        /// <c>&lt;</c>-prefixed field. Pinned by
        /// <see cref="The_mutable_static_scan_detects_a_settable_static_auto_property"/>, which
        /// fails if that blanket skip ever returns.
        /// </summary>
        internal static int Setting { get; set; }
    }

    /// <summary>
    /// arb-02cc: a settable static auto-property is process-global mutable state, and the scan must
    /// see it. It reaches the IL as <c>&lt;Setting&gt;k__BackingField</c>; a blanket skip of
    /// <c>&lt;</c>-prefixed fields dropped it, so the shape most likely to be reached for by someone
    /// who believed a property was safer than a field was the one shape the audit could not see.
    ///
    /// <para>Asserted under the PROPERTY name, not the backing field's: that is what the author
    /// wrote, what a failure message has to say to be actionable, and what the allow-list would name.
    /// </para>
    /// </summary>
    [Fact]
    public void The_mutable_static_scan_detects_a_settable_static_auto_property()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath("tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        var found = FindMutableStatics(baitAssembly!).ToArray();

        Assert.Contains(found, f => f.EndsWith("MutableStaticBait.Setting", StringComparison.Ordinal));

        // The backing field's raw name must not leak into the report: it would send a reader looking
        // for a field that does not appear in any source file.
        Assert.DoesNotContain(found, f => f.Contains("k__BackingField", StringComparison.Ordinal));
    }

    /// <summary>
    /// Mutable statics in production that are known and accepted, each with the reason concurrent
    /// mutation is safe. The value is documentation for a human reading a failure, not an assertion.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedMutableStatics = new(StringComparer.Ordinal)
    {
        ["Arbitarr.Api: Arbitarr.Api.Rendering.ReleaseGuid._hmacKey"] =
            "The SEC-L2 proxy-guid HMAC secret. Must stay a mutable static: it is configured once " +
            "at startup before any request is handled, and it has to be reachable from " +
            "ReleaseGuid.Compute, which is a static called from five places in the render path. " +
            "The arb-agh race it caused is closed on the READER side instead — " +
            "RenderedRelease.ProxyGuid is materialised once per instance, so the evaluations within " +
            "one request agree even if the secret changes between them.",
    };

    /// <summary>
    /// Static fields that are neither <c>readonly</c> nor <c>const</c>.
    ///
    /// <para><b>Auto-property backing fields are IN SCOPE, and that is the point (arb-02cc).</b>
    /// <c>public static T Name { get; set; }</c> is process-global mutable state exactly as much as
    /// a bare static field is — the caller cannot tell the two apart, and whoever sets it last still
    /// wins. The compiler emits it as <c>&lt;Name&gt;k__BackingField</c>, so a blanket "skip every
    /// field whose name starts with <c>&lt;</c>" made the one shape most likely to be written by
    /// someone who thought a property was safer than a field completely invisible to this scan.
    /// Such fields are reported under the PROPERTY name, which is what the author wrote and what the
    /// allow-list should name.</para>
    ///
    /// <para><b>What is still skipped, and why each is not a decision anyone made.</b> Compiler
    /// caches on the closure types the compiler owns outright: <c>&lt;&gt;c</c> (the cached
    /// singleton holding lambda delegates) and <c>&lt;&gt;o__</c> (call-site caches). These hold
    /// delegates and dynamic call sites, never program state; nothing an author can write ends up
    /// in them, and their mutation is the compiler's own memoisation. The type-level <c>&lt;</c>
    /// skip is what excludes them, so no name filter on the FIELD is needed beyond recognising the
    /// backing-field shape.</para>
    /// </summary>
    private static IEnumerable<string> FindMutableStatics(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            // Skips the compiler's own closure/call-site types (`<>c`, `<>o__`) wholesale. Their
            // static fields are delegate and call-site caches, not program state.
            if (type.Name.StartsWith('<') || type.Namespace.StartsWith("System.", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var field in type.Fields)
            {
                if (!field.IsStatic || field.IsInitOnly || field.IsLiteral)
                {
                    continue;
                }

                var name = AsAutoPropertyName(field.Name);

                // Any OTHER `<`-named field on an author-written type — a shape no current compiler
                // output produces here. Skipped rather than reported under an unreadable name; if a
                // future shape appears, admitting it is a deliberate edit to AsAutoPropertyName
                // rather than something this loop guesses at.
                if (name is null)
                {
                    continue;
                }

                yield return $"{type.FullName}.{name}";
            }
        }
    }

    /// <summary>
    /// Maps <c>&lt;Name&gt;k__BackingField</c> to <c>Name</c>, passes an ordinary field name
    /// through unchanged, and returns <see langword="null"/> for any other compiler-generated name.
    /// </summary>
    private static string? AsAutoPropertyName(string fieldName)
    {
        const string Suffix = ">k__BackingField";

        if (!fieldName.StartsWith('<'))
        {
            return fieldName;
        }

        return fieldName.EndsWith(Suffix, StringComparison.Ordinal)
            ? fieldName[1..^Suffix.Length]
            : null;
    }
}
