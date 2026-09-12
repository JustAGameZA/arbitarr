using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-n21: inside <c>Arbitarr.Data</c>, a SQLite connection string may be BUILT in only a handful
/// of named types. Everywhere else must take its string from
/// <c>Arbitarr.Data.DatabaseConnectionStrings</c>.
///
/// <para><b>Two bans, two allow-lists, on purpose.</b> Building a string
/// (<c>SqliteConnectionStringBuilder</c>) and opening one (<c>SqliteConnection</c>) are exempted
/// SEPARATELY — see <see cref="TypesAllowedToBuildAConnectionString"/> and
/// <see cref="TypesAllowedToConstructAConnection"/>. A single combined list would exempt each named
/// type from both, so a type listed only because it legitimately OPENS connections
/// (<c>BackupService</c>, <c>SqlitePoolCleaner</c>, <c>SqliteConnectionFactory</c>) could start
/// formatting its own string with nothing to report it — the gap this test exists to close,
/// reopened for exactly the types it was about. Trusting a type to open a connection is not
/// trusting it to invent the string.</para>
///
/// <para><b>What this protects.</b> Microsoft.Data.Sqlite keys its connection pools by the FULL
/// connection string, not by the file, so two strings naming one database are two independent
/// pools. Before it swaps <c>arbitarr.db</c>, a restore must drop EVERY pooled handle on that file,
/// and <c>SqlitePoolCleaner</c> can only clear the pools it can enumerate — the ones
/// <c>DatabaseConnectionStrings.ForDatabase</c> produces. A string formatted at some other call
/// site creates a pool nothing clears, and the consequence is silent on Linux (which is where CI
/// runs): the swap SUCCEEDS and the stale handle keeps serving the REPLACED INODE, so the process
/// reads the old database while the restored one sits on disk looking applied.</para>
///
/// <para><b>Why this test exists as a test.</b> <c>SqlitePoolCleaner</c> and
/// <c>DatabaseConnectionStrings</c> both state in their own remarks that a new inline call site
/// cannot be added without failing this scan. Without the scan those remarks describe an intention,
/// and the closure is a fact about today's source rather than a rule — the next connection opened
/// in Arbitarr.Data reaches for <c>new SqliteConnectionStringBuilder { ... }</c> exactly as readily
/// as the last one did.</para>
///
/// <para><b>Why IL, not source.</b> A grep over source is defeated by an alias, a fully-qualified
/// name, a <c>var</c>, or a helper; reflection can see the types a method mentions but not the
/// objects its body constructs. Cecil sees the actual <c>newobj</c> instruction whatever the source
/// spelled it as.</para>
///
/// <para><b>Scope: Arbitarr.Data only.</b> That is the assembly that owns the application database
/// and the restore, and the only one with a <c>DatabaseConnectionStrings</c> to funnel through. No
/// other production assembly opens SQLite directly today; if one starts to, it should do so through
/// Arbitarr.Data rather than by widening this list.</para>
/// </summary>
public class NoInlineDatabaseConnectionStringsTests
{
    private const string ScannedAssemblyName = "Arbitarr.Data";

    /// <summary>
    /// Building a connection string with the builder. This is the STRICTER of the two bans: a type
    /// that constructs one is deciding for itself what shape names a database, which is precisely
    /// the decision <c>DatabaseConnectionStrings</c> exists to hold.
    /// </summary>
    private const string BuilderConstructor =
        "Microsoft.Data.Sqlite.SqliteConnectionStringBuilder::.ctor";

    /// <summary>
    /// Constructing a connection from a string. Weaker, because the string may well have come from
    /// <c>DatabaseConnectionStrings</c> — the IL cannot tell — so this ban catches only the types
    /// that have no business opening SQLite at all.
    /// </summary>
    private const string ConnectionConstructor =
        "Microsoft.Data.Sqlite.SqliteConnection::.ctor";

    /// <summary>
    /// The two ways a connection string enters Microsoft.Data.Sqlite. Both are <c>newobj</c>
    /// targets, which is why this scan walks constructors rather than reusing
    /// <see cref="TestProcessGlobalStateTests.FindBannedCalls(string, string[])"/> — that walk reads
    /// <c>call</c>/<c>callvirt</c> and would see neither.
    /// </summary>
    private static readonly string[] BannedConstructors =
    [
        BuilderConstructor,
        ConnectionConstructor,
    ];

    /// <summary>
    /// The ONLY types in <c>Arbitarr.Data</c> allowed to BUILD a connection string.
    ///
    /// <para><b>Why this list is separate from
    /// <see cref="TypesAllowedToConstructAConnection"/>, and must stay separate.</b> A single list
    /// covering both constructors would exempt each named type from BOTH — so
    /// <c>BackupService</c>, <c>SqlitePoolCleaner</c> and <c>SqliteConnectionFactory</c>, which are
    /// listed there only because they legitimately OPEN connections, could each start formatting
    /// their own connection string and this scan would report nothing. That is HIGH-1's gap
    /// reopened for the very types it was about. The exemption is therefore per BANNED MEMBER, not
    /// per type: a type may be trusted to open a connection without being trusted to invent the
    /// string it opens.</para>
    ///
    /// <para>Listed by exact full name with a reason each — never a wildcard, a namespace prefix,
    /// or a "contains" match, because a pattern silently grows to cover the next type that happens
    /// to match it, and the whole value of this ban is that widening it is a deliberate, reviewed
    /// act.</para>
    ///
    /// <list type="bullet">
    /// <item><c>DatabaseConnectionStrings</c> — the single builder itself. This is the type every
    /// other site is required to delegate to, so of course it builds.</item>
    /// <item><c>Logging.LogStore</c> — genuinely builds one inline, and is exempt BY NAME with the
    /// CLAUDE.md section 1 reason: it owns the SECOND database (<c>arbitarr-logs.db</c>,
    /// <c>LogStore.DatabaseFileName</c>), deliberately separate from <c>arbitarr.db</c>. A restore
    /// never replaces it, and clearing its pool alongside the application's would be
    /// <c>ClearAllPools</c> under a new name — the over-reach that produced arb-cbc/arb-5ba.</item>
    /// </list>
    ///
    /// <para><c>BackupService</c> is deliberately ABSENT: its snapshot destination string is built
    /// by <c>DatabaseConnectionStrings.SnapshotDestination</c> precisely so that this list does not
    /// have to name it.</para>
    ///
    /// <para><c>BackupArchiveValidator</c> is ABSENT TOO, and used not to be (arb-zupt). It built a
    /// ReadOnly, unpooled string inline for the staged file it inspects; that shape now lives in
    /// <c>DatabaseConnectionStrings.StagedUpload</c>, shared with the migration-id read of the SAME
    /// staged file in <c>BackupService</c> — which is what made the two agree on pooling instead of
    /// only the first one being unpooled. Shrinking this list is the point: an exemption removed is
    /// a shape that can no longer drift.</para>
    /// </summary>
    private static readonly string[] TypesAllowedToBuildAConnectionString =
    [
        "Arbitarr.Data.DatabaseConnectionStrings",
        "Arbitarr.Data.Logging.LogStore",
    ];

    /// <summary>
    /// The ONLY types in <c>Arbitarr.Data</c> allowed to construct a <c>SqliteConnection</c> from a
    /// string. A weaker ban than the builder one above — the IL cannot tell whether the string came
    /// from <c>DatabaseConnectionStrings</c> — so this catches the types that have no business
    /// opening SQLite at all, while the builder list is what keeps the STRING SHAPES closed.
    ///
    /// <list type="bullet">
    /// <item><c>SqliteConnectionFactory</c> — opens the application connection from
    /// <c>SqliteConnectionOptions.ToConnectionString()</c>, which delegates to
    /// <c>DatabaseConnectionStrings.Application</c>.</item>
    /// <item><c>SqlitePoolCleaner</c> — constructs a throwaway connection per string as the ADDRESS
    /// of the pool to clear (<c>ClearPool</c> selects by the connection's own string). Every string
    /// it uses comes from <c>DatabaseConnectionStrings.ForDatabase</c>, and it never opens the
    /// connection.</item>
    /// <item><c>BackupService</c> — its connections (snapshot source, snapshot destination, and the
    /// migration-id reader in both its pooled and its staged-upload form) all take their strings
    /// from <c>DatabaseConnectionStrings</c>. It is trusted to OPEN, and — being absent from the
    /// builder list — still cannot invent a string.</item>
    /// <item><c>BackupArchiveValidator</c> — opens the staged file it is inspecting, with the
    /// ReadOnly unpooled string from <c>DatabaseConnectionStrings.StagedUpload</c>. On this list
    /// only: since arb-zupt it no longer builds that string itself.</item>
    /// <item><c>Logging.LogStore</c> — opens the log database, per its entry above.</item>
    /// </list>
    ///
    /// <para><c>ArbitarrDbContextDesignTimeFactory</c> is deliberately in NEITHER list. It passes a
    /// literal <c>"Data Source=design-time.db"</c> to <c>UseSqlite</c> and constructs neither banned
    /// type, so it is outside this scan's reach rather than exempted by it. Listing it would be a
    /// standing exemption for a type that does not need one — and would silently cover it if it
    /// ever did start building a real string.</para>
    /// </summary>
    private static readonly string[] TypesAllowedToConstructAConnection =
    [
        "Arbitarr.Data.SqliteConnectionFactory",
        "Arbitarr.Data.Backup.SqlitePoolCleaner",
        "Arbitarr.Data.Backup.BackupService",
        "Arbitarr.Data.Backup.BackupArchiveValidator",
        "Arbitarr.Data.Logging.LogStore",
    ];

    /// <summary>
    /// The allow-list that applies to <paramref name="bannedConstructor"/> — the mapping that makes
    /// the exemption per MEMBER rather than per type. An unrecognised member is a programming error
    /// here, not something to wave through: defaulting to "allowed" would silently disable the ban
    /// for any constructor added to <see cref="BannedConstructors"/> without a list.
    /// </summary>
    private static string[] AllowedTypesFor(string bannedConstructor) => bannedConstructor switch
    {
        BuilderConstructor => TypesAllowedToBuildAConnectionString,
        ConnectionConstructor => TypesAllowedToConstructAConnection,
        _ => throw new InvalidOperationException(
            $"No allow-list is defined for the banned constructor '{bannedConstructor}'. Add one " +
            "rather than letting the ban pass silently."),
    };

    [Fact]
    public void Only_the_named_types_build_a_sqlite_connection_string()
    {
        var path = TestProcessGlobalStateTests.ResolveAssemblyPath("src", ScannedAssemblyName);

        // Loud failure, not a vacuous pass: an assembly the scan cannot open is not an assembly it
        // has found to be clean (CLAUDE.md section 4).
        Assert.True(
            path is not null,
            $"Could not locate the built assembly for {ScannedAssemblyName}. This scan reads its IL " +
            "from the project's build output, so the solution must be built before " +
            "Arbitarr.Architecture.Tests runs. Run 'dotnet build' at the solution level first; in " +
            "CI, the job must build the solution before running this project.");

        // Each finding is checked against the allow-list for ITS OWN banned member, so a type
        // trusted to open a connection is not thereby trusted to build the string it opens.
        var violations = FindConstructions(path!)
            .Where(v => !AllowedTypesFor(v.BannedConstructor)
                .Contains(v.DeclaringType, StringComparer.Ordinal))
            .Select(v => v.Description)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "A SQLite connection string naming the application database must be built only by " +
            "Arbitarr.Data.DatabaseConnectionStrings — pools are keyed by the full string, so a " +
            "string built anywhere else is a pool SqlitePoolCleaner cannot clear before a restore " +
            "swaps the file (arb-n21). Take the string from DatabaseConnectionStrings, or, if the " +
            "site genuinely names a file a restore never replaces, add its type to the allow-list " +
            "for that specific constructor (TypesAllowedToBuildAConnectionString or " +
            "TypesAllowedToConstructAConnection) with the reason — never to both to make a failure " +
            "go away. Found:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Proves this scan is NON-VACUOUS: the same walk, over an assembly that DOES construct both
    /// banned types, must report both. Without it the assertion above would pass just as happily if
    /// the IL walk were broken, if it read <c>call</c> instead of <c>newobj</c> and so matched
    /// nothing, or if either name were misspelled — the exact shape CLAUDE.md section 4 records as
    /// having shipped three times.
    ///
    /// <para>The bait is <see cref="Bait"/> in THIS assembly, whose methods construct both banned
    /// types from an inline string. It is never executed; only its IL is read. It lives here rather
    /// than in <c>src/</c> because a bait inside Arbitarr.Data would be a real inline builder in
    /// the very assembly the ban protects — the thing being forbidden. This scan reads Arbitarr.Data
    /// and nothing else, so the bait cannot leak into the real assertion.</para>
    ///
    /// <para>Both constructors are asserted separately. "Something was found" would still pass with
    /// one of the two patterns dead.</para>
    /// </summary>
    [Fact]
    public void The_scan_detects_an_inline_connection_string_when_one_is_present()
    {
        var baitAssembly = TestProcessGlobalStateTests.ResolveAssemblyPath(
            "tests", "Arbitarr.Architecture.Tests");
        Assert.NotNull(baitAssembly);

        // The bait type as CECIL spells it. Reflection renders a nested type with '+'
        // (Outer+Bait) while Cecil's TypeDefinition.FullName uses '/', so comparing a reflected
        // name against a scanned one silently never matches. Derived from typeof rather than
        // written out so a rename cannot leave this behind.
        var baitTypeName = typeof(Bait).FullName!.Replace('+', '/');

        // Matched on the DESCRIPTION, which names the instruction's own declaring type, not on
        // DeclaringType — that field deliberately reports the OUTERMOST type so an exemption can be
        // written as a plain name, and for a nested bait it would read as this test class.
        var found = FindConstructions(baitAssembly!)
            .Select(v => v.Description)
            .Where(d => d.StartsWith(baitTypeName + ".", StringComparison.Ordinal))
            .ToArray();

        foreach (var banned in BannedConstructors)
        {
            Assert.Contains(found, v => v.EndsWith(banned, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Proves the two lists are actually SEPARATE — the property the whole per-member split exists
    /// to provide, and the one a combined list would silently lose.
    ///
    /// <para>Takes a type that IS allowed to construct a connection and is NOT allowed to build a
    /// string (<c>BackupService</c> is the live example), and requires that a builder attributed to
    /// it would be reported. Without this fact, collapsing the two lists back into one would leave
    /// every other test in this file green: the main scan would still pass (nothing in
    /// <c>src/</c> violates it either way), the bait would still be detected, and the exemptions
    /// would still be exercised. Only this one goes red.</para>
    ///
    /// <para>Asserted against the FILTER rather than by planting a real builder in
    /// <c>BackupService</c> — the planted-code version is what the throwaway mutation project did,
    /// per CLAUDE.md section 4's "never put vulnerable code in the repository".</para>
    /// </summary>
    [Fact]
    public void A_type_trusted_to_open_a_connection_is_not_trusted_to_build_the_string()
    {
        const string connectionOnlyType = "Arbitarr.Data.Backup.BackupService";

        // The premise: this type is on one list and not the other. If a future edit adds it to the
        // builder list, this fails here rather than silently widening the ban.
        Assert.Contains(connectionOnlyType, TypesAllowedToConstructAConnection);
        Assert.DoesNotContain(connectionOnlyType, TypesAllowedToBuildAConnectionString);

        // Opening a connection: permitted, so not a violation.
        Assert.Contains(
            connectionOnlyType,
            AllowedTypesFor(ConnectionConstructor));

        // Building a string from that same type: a violation, because the lists are separate. A
        // combined allow-list would make this pass and the ban would be gone for this type.
        Assert.DoesNotContain(
            connectionOnlyType,
            AllowedTypesFor(BuilderConstructor));
    }

    /// <summary>
    /// Proves the ALLOW-LIST is non-vacuous too: it must be the list, and not an empty scan, that
    /// keeps the real assertion green. If <see cref="FindConstructions"/> ever stopped finding
    /// anything in Arbitarr.Data, the main test would pass for the wrong reason and no exemption
    /// would ever be exercised — so require that the scan does in fact see the sanctioned sites,
    /// and that they are the ones the list names.
    /// </summary>
    [Fact]
    public void The_allow_list_is_exercised_by_types_the_scan_can_actually_see()
    {
        var path = TestProcessGlobalStateTests.ResolveAssemblyPath("src", ScannedAssemblyName);
        Assert.NotNull(path);

        var found = FindConstructions(path!).ToArray();

        // The single builder must be visible to the walk, or the walk is not reading what it claims.
        Assert.Contains(
            found,
            v => v.DeclaringType == "Arbitarr.Data.DatabaseConnectionStrings" &&
                 v.BannedConstructor == BuilderConstructor);

        // Nothing may sit in either allow-list that the scan can no longer see FOR THAT MEMBER: a
        // stale exemption is an exemption nobody is reviewing, and it would silently re-cover the
        // type if it started constructing again. Checked per member, so an entry cannot stay alive
        // in the builder list on the strength of the type merely opening a connection — which is
        // exactly the conflation the two lists exist to prevent.
        var unused = BannedConstructors
            .SelectMany(banned => AllowedTypesFor(banned)
                .Where(t => !found.Any(v =>
                    v.DeclaringType == t && v.BannedConstructor == banned))
                .Select(t => $"{t} (exempted for {banned})"))
            .ToArray();

        Assert.True(
            unused.Length == 0,
            "These exemptions are dead — the type no longer constructs the member it is exempted " +
            "for, so the entry should be removed. Leaving it in place would silently re-cover the " +
            "type if it started constructing again:\n  " + string.Join("\n  ", unused));
    }

    /// <summary>
    /// Every construction of a banned type in the assembly, as the declaring type plus a
    /// "&lt;type&gt;.&lt;method&gt; constructs &lt;banned&gt;" description.
    ///
    /// <para>Walks <c>newobj</c>, not <c>call</c>: both banned members are instance constructors, so
    /// the C# <c>new</c> compiles to <c>newobj</c> and the shared
    /// <see cref="TestProcessGlobalStateTests.FindBannedCalls(string, string[])"/> — which reads
    /// <c>call</c>/<c>callvirt</c> — would find none of them. Matching on
    /// <c>DeclaringType::.ctor</c> catches every overload, so a different constructor signature is
    /// not an escape.</para>
    /// </summary>
    private static IEnumerable<(string DeclaringType, string BannedConstructor, string Description)>
        FindConstructions(string assemblyPath)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode != OpCodes.Newobj)
                    {
                        continue;
                    }

                    if (instruction.Operand is not MethodReference constructed)
                    {
                        continue;
                    }

                    var fullName = $"{constructed.DeclaringType.FullName}::{constructed.Name}";
                    if (!BannedConstructors.Contains(fullName, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    // Nested and compiler-generated bodies (lambdas, iterator state machines,
                    // local functions) live in their own types, whose FullName carries the '/'
                    // separator. Attributing them to the OUTERMOST type is what stops a site
                    // escaping the ban by moving into a lambda — and what lets an exemption be
                    // written as the plain type name a reader recognises.
                    var owner = type;
                    while (owner.DeclaringType is not null)
                    {
                        owner = owner.DeclaringType;
                    }

                    yield return (
                        owner.FullName,
                        fullName,
                        $"{type.FullName}.{method.Name} constructs {fullName}");
                }
            }
        }
    }

    /// <summary>
    /// Bait for <see cref="The_scan_detects_an_inline_connection_string_when_one_is_present"/>: the
    /// only sanctioned inline connection string anywhere the scan looks, present so the walk can be
    /// proven to see one. NEVER CALLED — the always-false guard makes that structural rather than a
    /// promise, so this cannot be mistaken for a usable helper.
    /// </summary>
    private static class Bait
    {
        public static void BuildsAConnectionStringInline()
        {
            if (AlwaysFalse)
            {
                _ = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = "arbitarr-never-opened.db",
                }.ToString();
            }
        }

        public static void ConstructsAConnectionFromAnInlineString()
        {
            if (AlwaysFalse)
            {
                using var connection =
                    new Microsoft.Data.Sqlite.SqliteConnection("Data Source=arbitarr-never-opened.db");
            }
        }

        // Deliberately not a const: a const would let the compiler drop the bodies above, and the
        // bait's IL would vanish along with the proof it exists to provide.
        private static bool AlwaysFalse => bool.Parse(bool.FalseString);
    }
}
