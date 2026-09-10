using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Arbitarr.Architecture.Tests;

/// <summary>
/// Guards two coupled sites named in arb-hez: <c>docs/standards/architecture.md</c>'s "Error
/// handling in background and maintenance work" section quotes a sentence verbatim from
/// <c>src/Arbitarr.Host/Backup/StagingSweepService.cs</c>'s catch-block comment to justify why
/// that service's swallow-and-continue is not the per-item convention the rest of the section
/// describes. Nothing previously detected the two drifting apart - a rename or rewording on
/// either side would silently break the citation. This test extracts the quoted sentence FROM
/// the doc at run time (never hard-coded here, or the test would decouple from the doc it is
/// meant to guard) and asserts it is present, byte-for-byte, in the source comment.
///
/// It also guards the four sibling comments (RefreshWorker, StagingSweepService,
/// MaintenanceHostedService, NotificationHostedService) that all cite the same doc and section
/// by name, so a doc rename or section-heading rewrite is caught rather than leaving four dead
/// citations behind.
/// </summary>
public class StandardsQuoteSourceTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private const string SectionHeading = "Error handling in background and maintenance work";

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
    /// Extracts the sentence architecture.md quotes (in parentheses, double-quoted) immediately
    /// after "`StagingSweepService` draws the same line from its own side" in the section named by
    /// <see cref="SectionHeading"/>. Returns an empty string if the section, the anchor phrase, or
    /// the quoted text cannot be found - callers must not treat that as success.
    /// </summary>
    private static string ExtractQuotedStagingSweepSentence(string architectureDoc)
    {
        var sectionIndex = architectureDoc.IndexOf(
            "## " + SectionHeading, StringComparison.Ordinal);
        if (sectionIndex < 0)
        {
            return string.Empty;
        }

        // Bound the slice at the next "## " heading so a later section's own parenthetical
        // quote can never satisfy this extraction (arb-hez review fix).
        var sectionText = architectureDoc[sectionIndex..];
        var nextHeadingIndex = sectionText.IndexOf("\n## ", 1, StringComparison.Ordinal);
        if (nextHeadingIndex >= 0)
        {
            sectionText = sectionText[..nextHeadingIndex];
        }

        var anchorIndex = sectionText.IndexOf(
            "`StagingSweepService` draws the same line from its own side", StringComparison.Ordinal);
        if (anchorIndex < 0)
        {
            return string.Empty;
        }

        var afterAnchor = sectionText[anchorIndex..];

        var match = Regex.Match(
            afterAnchor,
            "\\(\"(?<quote>[^\"]+)\"\\)",
            RegexOptions.None,
            RegexTimeout);

        return match.Success ? NormalizeWhitespace(match.Groups["quote"].Value) : string.Empty;
    }

    /// <summary>
    /// Collapses markdown/comment line-wrapping (arbitrary runs of whitespace, including
    /// newlines) to single spaces so the same prose sentence compares equal regardless of where
    /// the doc and the source comment each happen to wrap their lines.
    /// </summary>
    private static string NormalizeWhitespace(string text) =>
        Regex.Replace(text, "\\s+", " ", RegexOptions.None, RegexTimeout).Trim();

    [Fact]
    public void Extracted_Sentence_Is_Present_Verbatim_In_StagingSweepService()
    {
        var repoRoot = FindRepoRoot();
        var docPath = Path.Combine(repoRoot, "docs", "standards", "architecture.md");
        Assert.True(File.Exists(docPath), $"Expected to find {docPath}.");

        var docText = File.ReadAllText(docPath);
        var quoted = ExtractQuotedStagingSweepSentence(docText);

        // Non-vacuity guards (CLAUDE.md section 4): an extraction that silently returns empty
        // must not let the later Contains assertion pass by never having anything to check.
        Assert.False(string.IsNullOrWhiteSpace(quoted), "Extraction returned no quoted sentence.");
        Assert.True(
            quoted.Length > 40,
            $"Extracted sentence is suspiciously short ({quoted.Length} chars): '{quoted}'.");

        // Positive control: prove the assertion below would actually fail on a mismatch, by
        // checking a deliberately perturbed copy is correctly reported as NOT found first.
        var perturbed = quoted.Replace(
            "one more run", "one more attempt", StringComparison.Ordinal);
        Assert.NotEqual(quoted, perturbed);

        var sourcePath = Path.Combine(
            repoRoot, "src", "Arbitarr.Host", "Backup", "StagingSweepService.cs");
        Assert.True(File.Exists(sourcePath), $"Expected to find {sourcePath}.");

        // Restrict the haystack to the "//" comment lines only (not the whole stripped source
        // file), so this test fails if the cited sentence is deleted from the comment even when
        // the same prose survives elsewhere, e.g. in a string literal (arb-hez review fix).
        var rawSourceText = File.ReadAllText(sourcePath);
        var commentLines = rawSourceText
            .Split('\n')
            .Select(line => line.TrimEnd('\r').TrimStart(' ', '\t'))
            .Where(line => line.StartsWith("//", StringComparison.Ordinal))
            .Select(line => line[2..]);
        var sourceText = NormalizeWhitespace(string.Join(" ", commentLines));

        Assert.DoesNotContain(perturbed, sourceText, StringComparison.Ordinal);

        // Now the real assertion: the doc's quoted sentence, unperturbed, is in the source
        // comment lines (both normalized to single-spaced prose so line-wrap position cannot
        // cause a false mismatch).
        Assert.Contains(quoted, sourceText, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> CommentsCitingTheStandard()
    {
        yield return new object[]
        {
            Path.Combine("src", "Arbitarr.Core", "Caching", "RefreshWorker.cs"),
        };
        yield return new object[]
        {
            Path.Combine("src", "Arbitarr.Host", "Backup", "StagingSweepService.cs"),
        };
        yield return new object[]
        {
            Path.Combine("src", "Arbitarr.Host", "Maintenance", "MaintenanceHostedService.cs"),
        };
        yield return new object[]
        {
            Path.Combine("src", "Arbitarr.Host", "Notifications", "NotificationHostedService.cs"),
        };
    }

    [Theory]
    [MemberData(nameof(CommentsCitingTheStandard))]
    public void Comment_Citation_And_Doc_Section_Still_Exist(string relativeSourcePath)
    {
        var repoRoot = FindRepoRoot();

        var sourcePath = Path.Combine(repoRoot, relativeSourcePath);
        Assert.True(File.Exists(sourcePath), $"Expected to find {sourcePath}.");
        var sourceText = File.ReadAllText(sourcePath);

        Assert.Contains("docs/standards/architecture.md", sourceText, StringComparison.Ordinal);

        var docPath = Path.Combine(repoRoot, "docs", "standards", "architecture.md");
        Assert.True(File.Exists(docPath), $"Expected to find {docPath}.");
        var docText = File.ReadAllText(docPath);

        Assert.Contains("## " + SectionHeading, docText, StringComparison.Ordinal);
    }
}
