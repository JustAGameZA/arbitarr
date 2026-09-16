using System.Text.RegularExpressions;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// arb-016s: the root cause behind arb-a7ll and arb-ddhn, rather than either instance of it.
/// <c>Arbitarr.Ai.ClassificationPrompt.Build</c> and
/// <c>Arbitarr.Core.Filtering.VerdictCacheKey.Compute</c> each enumerate candidate fields by hand,
/// and until this test nothing coupled the two lists. A field added to the prompt alone is rendered
/// to the model without entering the key, so two releases the model was asked DIFFERENT questions
/// about share one cache entry and the second is served the first one's verdict — twice now
/// (arb-a7ll for <c>Poster</c>/<c>UsenetGroup</c>, arb-ddhn for <c>Category</c>, <c>Files</c>,
/// <c>PasswordProtected</c> and <c>Grabs</c>), each time invisible until someone re-read both
/// methods side by side. This asserts every <c>candidate.&lt;Member&gt;</c> that <c>Build</c> reads
/// is also read by <c>Compute</c>, so the third occurrence fails CI instead.
///
/// <para><b>The direction is one-way, deliberately.</b> Only prompt-to-key is asserted, never the
/// reverse. <c>Build</c>'s rendering is the question the model answered and the key follows it, not
/// the other way round: a key FINER than the prompt is correct (<c>Compute</c> separates a null
/// poster from an empty one where <c>Build</c>'s <see cref="string.IsNullOrWhiteSpace(string)"/>
/// gate renders both as no line at all — see <c>VerdictCacheKey.Compute</c>'s arb-ddhn remark for
/// why that finer encoding must not be relaxed to match), whereas a key COARSER than the prompt is
/// the bug. Asserting the converse would flag that correct case as a violation and pressure whoever
/// hit it into coarsening the key, which is exactly backwards.</para>
///
/// <para><b>Source text, not reflection or IL.</b> The precedent here is
/// <see cref="DemotedDocCrefResolutionTests"/>, which greps <c>src/**/*.cs</c> for the same reason:
/// the object under test is which members a METHOD BODY mentions, and reflection over a compiled
/// type sees the members that exist, not which of them a given method reads. Cecil could read the
/// IL, but a property read compiles to a <c>callvirt</c> on a getter whose name must then be
/// un-mangled back to the member, and the two methods live in assemblies this project references
/// anyway — the source scan is the smaller mechanism for the same guarantee.</para>
///
/// <para><b>Scoped to the method body, not the file.</b> Both files carry long doc comments naming
/// these members, and <c>VerdictCacheKey</c>'s private encode helpers take plain parameters rather
/// than the candidate. A whole-file scan would therefore pass on a member that appears only in a
/// remark, which is precisely the silent-drift shape this test exists to catch, so each scan is
/// bounded to its method's body by brace matching from the signature.</para>
///
/// <para><b>One <c>[Fact]</c> per direction, not a <c>[Theory]</c> per member.</b> Same reasoning
/// as <see cref="DemotedDocCrefResolutionTests"/>'s class remark: a per-member <c>[Theory]</c> makes
/// the executed test count a function of how many fields the prompt happens to render, so
/// legitimately dropping a prompt line would shrink the count and trip CI's test-count ratchet,
/// which has no mechanism for an intentional decrease. The count here is fixed at three regardless
/// of what either method reads.</para>
/// </summary>
public class PromptAndCacheKeyCandidateFieldTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Matches a read of a member off the candidate parameter. Both methods name that parameter
    /// <c>candidate</c>; a rename would empty the scan, which the non-vacuity guard in
    /// <see cref="CandidateMembersRead"/> turns into a loud failure rather than a silent pass.
    /// </summary>
    private static readonly Regex CandidateMemberRead = new(
        @"\bcandidate\.(?<member>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.None,
        RegexTimeout);

    private const string PromptFile = "src/Arbitarr.Ai/ClassificationPrompt.cs";
    private const string CacheKeyFile = "src/Arbitarr.Core/Filtering/VerdictCacheKey.cs";

    private const string BuildSignature = "public static IReadOnlyList<OllamaChatMessage> Build(ReleaseCandidate candidate)";
    private const string ComputeSignature = "public static string Compute(";

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
    /// Returns the text of the method whose signature starts at <paramref name="signature"/>,
    /// from the first <c>{</c> after it to its matching <c>}</c>. Brace counting is deliberately
    /// plain: a brace inside a string literal or comment within either body would skew it, and
    /// neither body contains one today — a skew would end the body early and DROP members from the
    /// scanned set, which fails this test loudly rather than passing it vacuously.
    /// </summary>
    private static string MethodBody(string relativePath, string signature)
    {
        var path = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Expected to find {relativePath} under the repository root.");

        var text = File.ReadAllText(path);
        var signatureIndex = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(
            signatureIndex >= 0,
            $"Could not find the signature \"{signature}\" in {relativePath}. The method was renamed " +
            "or its signature reshaped; update this ratchet rather than deleting it.");

        var open = text.IndexOf('{', signatureIndex + signature.Length);
        Assert.True(open >= 0, $"Could not find the opening brace of {signature} in {relativePath}.");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException(
            $"Unbalanced braces while reading the body of {signature} in {relativePath}.");
    }

    /// <summary>
    /// Every distinct <c>candidate.&lt;Member&gt;</c> read inside the given method body, sorted for
    /// a stable failure message. The non-vacuity guard (CLAUDE.md section 4) is here rather than at
    /// the call site: a scan that finds nothing means the parameter was renamed or the body was
    /// mis-bounded, and an empty set trivially satisfies every subset assertion below.
    /// </summary>
    private static SortedSet<string> CandidateMembersRead(string body, string description)
    {
        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match match in CandidateMemberRead.Matches(body))
        {
            members.Add(match.Groups["member"].Value);
        }

        Assert.True(
            members.Count > 0,
            $"Found no candidate member reads in {description}. An empty set satisfies the subset " +
            "assertion in this file vacuously, so this is the scan being broken (a renamed parameter, " +
            "or a mis-bounded method body), not the methods agreeing.");

        return members;
    }

    private static SortedSet<string> PromptMembers() =>
        CandidateMembersRead(MethodBody(PromptFile, BuildSignature), "ClassificationPrompt.Build");

    private static SortedSet<string> CacheKeyMembers() =>
        CandidateMembersRead(MethodBody(CacheKeyFile, ComputeSignature), "VerdictCacheKey.Compute");

    /// <summary>
    /// The ratchet itself. Every member the prompt renders must also be a key component, so a field
    /// added to <c>Build</c> alone fails the build instead of silently reusing verdicts formed under
    /// different metadata. Collects every missing member rather than failing on the first, so one
    /// run reports the whole gap.
    /// </summary>
    [Fact]
    public void Every_Candidate_Member_Rendered_By_The_Prompt_Is_Also_A_Cache_Key_Component()
    {
        var promptMembers = PromptMembers();
        var cacheKeyMembers = CacheKeyMembers();

        var missing = promptMembers.Where(member => !cacheKeyMembers.Contains(member)).ToList();

        Assert.True(
            missing.Count == 0,
            $"ClassificationPrompt.Build reads {promptMembers.Count} candidate member(s) and " +
            $"VerdictCacheKey.Compute reads {cacheKeyMembers.Count}. The following are rendered to " +
            "the model but are NOT part of the verdict cache key, so two releases the model would " +
            "answer differently about share one cache entry and the second is served the first's " +
            "verdict (arb-a7ll, arb-ddhn): " + string.Join(", ", missing) +
            ". Add a key component for each in VerdictCacheKey.Compute — null, empty and " +
            "\"not reported\" must stay distinguishable — and record the resulting one-off cache " +
            "invalidation in docs/adr/0001-separate-ai-and-media.md.");
    }

    /// <summary>
    /// Non-vacuity, the half the subset assertion above cannot show for itself (CLAUDE.md section 4):
    /// it would pass just as happily if the scan found nothing on the prompt side. This names the
    /// members both methods are known to handle today — the four arb-ddhn added and the two arb-a7ll
    /// did — and asserts the scan actually FINDS them, so the ratchet is demonstrably looking at the
    /// real bodies. It is a floor, not an inventory: a new field is expected to appear in both scans
    /// without being listed here, and only a member vanishing from one of them fails this.
    /// </summary>
    [Fact]
    public void The_Scan_Finds_The_Members_Both_Methods_Are_Known_To_Handle()
    {
        var promptMembers = PromptMembers();
        var cacheKeyMembers = CacheKeyMembers();

        string[] known =
        [
            "Category",
            "Files",
            "Grabs",
            "PasswordProtected",
            "Poster",
            "Protocol",
            "Size",
            "UsenetGroup",
        ];

        foreach (var member in known)
        {
            Assert.True(promptMembers.Contains(member), $"Expected ClassificationPrompt.Build to read candidate.{member}.");
            Assert.True(cacheKeyMembers.Contains(member), $"Expected VerdictCacheKey.Compute to read candidate.{member}.");
        }
    }

    /// <summary>
    /// Positive control for the ratchet's comparison, on the pattern of
    /// <see cref="DemotedDocCrefResolutionTests.Resolver_Fails_On_A_Planted_Nonexistent_Name"/>:
    /// proves the subset check reports a member present on the prompt side and absent from the key
    /// side, rather than always finding nothing missing. Planted against the REAL scanned key-side
    /// set, so it also fails if that set is somehow universal.
    /// </summary>
    [Fact]
    public void The_Comparison_Reports_A_Planted_Prompt_Only_Member()
    {
        var cacheKeyMembers = CacheKeyMembers();
        var promptMembersWithPlant = new SortedSet<string>(PromptMembers(), StringComparer.Ordinal)
        {
            "NotAKeyComponent",
        };

        var missing = promptMembersWithPlant.Where(member => !cacheKeyMembers.Contains(member)).ToList();

        Assert.Equal(["NotAKeyComponent"], missing);
    }
}
