using Arbitarr.Core.Pipeline;
using Xunit;

namespace Arbitarr.Core.Tests;

/// <summary>
/// Pins <see cref="DedupNormalizer"/>, which is the WHOLE surface of ADR 0019's title rule — what
/// it folds decides which releases merge, so broadening or narrowing it is a policy change, not a
/// tidy-up. These tests exist so such a change cannot land silently.
/// </summary>
public class DedupNormalizerTests
{
    [Theory]
    [InlineData("Some Show S01E01", "some show s01e01")]
    [InlineData("SOME SHOW S01E01", "some show s01e01")]
    public void Titles_are_case_folded(string input, string expected) =>
        Assert.Equal(expected, DedupNormalizer.Normalize(input));

    [Theory]
    [InlineData("Some.Show.S01E01")]
    [InlineData("Some_Show_S01E01")]
    [InlineData("Some  Show   S01E01")]
    [InlineData("Some - Show -- S01E01")]
    [InlineData("  Some Show S01E01  ")]
    public void Whitespace_and_punctuation_runs_all_collapse_to_one_space(string input) =>
        Assert.Equal("some show s01e01", DedupNormalizer.Normalize(input));

    /// <summary>
    /// Separators collapse to a space rather than vanishing. Deleting them would make
    /// <c>"ShowS01E01"</c> equal <c>"Show S01E01"</c>, merging two titles whose only evidence of
    /// being one release is that removing characters made them agree — the false merge ADR 0019
    /// treats as the expensive error.
    /// </summary>
    [Fact]
    public void Separators_collapse_to_a_space_rather_than_being_deleted() =>
        Assert.NotEqual(DedupNormalizer.Normalize("Some Show"), DedupNormalizer.Normalize("SomeShow"));

    /// <summary>
    /// Symbols are not punctuation in Unicode's classification and carry meaning in real release
    /// names, so folding them would merge titles that differ in content.
    /// </summary>
    [Fact]
    public void Symbols_are_left_alone() =>
        Assert.NotEqual(DedupNormalizer.Normalize("Show Disney+"), DedupNormalizer.Normalize("Show Disney"));

    /// <summary>
    /// Not deobfuscation: an obfuscated Usenet title is normalised as the literal text it is, so
    /// two indexers carrying the same post merge only if they spell it identically. ADR 0019
    /// records the resulting split as acceptable.
    /// </summary>
    [Fact]
    public void An_obfuscated_title_is_normalised_as_literal_text_not_decoded() =>
        Assert.Equal(
            "a7f3b91c 2e8d 4f60",
            DedupNormalizer.Normalize("A7F3B91C-2E8D-4F60"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...---...")]
    public void A_title_with_no_substantive_characters_normalises_to_empty(string? input) =>
        Assert.Equal(string.Empty, DedupNormalizer.Normalize(input));

    /// <summary>
    /// <b>P12 (security-x7w8-adapter-audit): release titles are attacker-influenced, so the
    /// normaliser must not be a ReDoS surface.</b> It uses no <c>Regex</c> — one linear pass over
    /// the characters — so the repo's 250ms <c>matchTimeout</c> precedent
    /// (<c>CredentialPatterns.cs</c>) has nothing to apply to. This is the control for that claim:
    /// the shapes that make a backtracking engine blow up are linear here, so a pathological title
    /// cannot hang the search path. The bound is deliberately loose (a second for inputs a
    /// backtracking engine would spend minutes on) so the test pins the COMPLEXITY CLASS and not
    /// the speed of whatever machine runs it — a quadratic implementation fails it by orders of
    /// magnitude, while a slow CI box does not.
    /// </summary>
    [Theory]
    [InlineData(50_000)]
    [InlineData(200_000)]
    public void A_pathological_title_normalises_without_catastrophic_backtracking(int length)
    {
        var pathological = new string('.', length) + new string('a', length) + new string(' ', length);

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var normalized = DedupNormalizer.Normalize(pathological);
        elapsed.Stop();

        Assert.Equal(new string('a', length), normalized);
        Assert.True(elapsed.ElapsedMilliseconds < 1000, $"took {elapsed.ElapsedMilliseconds}ms for length {length}");
    }

    /// <summary>
    /// Idempotent: normalising an already-normalised title is a no-op. A normaliser that kept
    /// changing its own output would make merge decisions depend on how many times it had run.
    /// </summary>
    [Fact]
    public void Normalization_is_idempotent()
    {
        var once = DedupNormalizer.Normalize("Some. Show -- S01E01  ");

        Assert.Equal(once, DedupNormalizer.Normalize(once));
    }
}
