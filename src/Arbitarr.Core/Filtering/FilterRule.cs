using System.Text.RegularExpressions;
using Arbitarr.Core.Releases;

namespace Arbitarr.Core.Filtering;

/// <summary>
/// A deterministic allow/deny filter rule: matches <see cref="ReleaseCandidate.Title"/> against a
/// regular expression. Concrete <see cref="IFilterRule"/> implementation for Step 5's rule engine;
/// persistence (the row shape) is owned by <c>Arbitarr.Data.Entities.FilterRuleEntry</c> — this
/// type never references that project (AC6: Core has zero references to other Arbitarr.*
/// projects), so mapping between the two happens at the repository boundary outside Core.
/// </summary>
public sealed class FilterRule : IFilterRule
{
    /// <summary>
    /// Upper bound on how long a single title match may run before it is treated as a
    /// catastrophic-backtracking hazard and skipped (R11). A search must never stall on a single
    /// hostile pattern (P1: fail open) — 250ms is generous for any legitimate title match while
    /// still bounding the worst case tightly per candidate.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _pattern;

    /// <summary>
    /// Constructs a rule from a name, allow/deny disposition, precedence, and a regex pattern
    /// matched against the release title. Throws <see cref="ArgumentException"/> (via
    /// <see cref="Regex"/>'s own parsing) if <paramref name="pattern"/> is not a valid regex —
    /// rejecting the bad value at construction rather than swallowing it silently.
    ///
    /// R11: prefers <see cref="RegexOptions.NonBacktracking"/> (linear-time matching, immune to
    /// catastrophic backtracking) and always sets <see cref="MatchTimeout"/> as a hard backstop.
    /// <see cref="RegexOptions.NonBacktracking"/> rejects some constructs (e.g. backreferences,
    /// lookaround) that <see cref="Regex"/>'s default backtracking engine allows; when the pattern
    /// uses one of those, this falls back to the backtracking engine with the same
    /// <see cref="MatchTimeout"/> still enforced, so even an accepted-but-hostile pattern can never
    /// stall the pipeline (P1: fail open) — see <see cref="Evaluate"/>.
    /// </summary>
    public FilterRule(string name, bool isAllow, Precedence precedence, string pattern)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Rule name must not be blank.", nameof(name));
        }

        Name = name;
        IsAllow = isAllow;
        Precedence = precedence;
        PatternText = pattern;

        try
        {
            _pattern = new Regex(
                pattern,
                RegexOptions.NonBacktracking | RegexOptions.IgnoreCase,
                MatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Pattern uses a construct NonBacktracking rejects (backreferences, lookaround, ...).
            // Fall back to the backtracking engine; MatchTimeout is the only ReDoS guard for these.
            _pattern = new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase,
                MatchTimeout);
        }
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsAllow { get; }

    /// <summary>The raw regex pattern text this rule was constructed from.</summary>
    public string PatternText { get; }

    /// <inheritdoc />
    public Precedence Precedence { get; }

    /// <inheritdoc />
    public Verdict Evaluate(ReleaseCandidate candidate)
    {
        bool isMatch;
        try
        {
            isMatch = _pattern.IsMatch(candidate.Title);
        }
        catch (RegexMatchTimeoutException)
        {
            // R11/P1: a catastrophic-backtracking (or otherwise pathological) pattern must never
            // stall the pipeline. Treat a timed-out match as "this rule did not match" so the
            // search still returns results; the hazard is skipped, not surfaced as an error.
            return Verdict.Unknown;
        }

        if (!isMatch)
        {
            return Verdict.Unknown;
        }

        return IsAllow ? Verdict.Accept : Verdict.Reject;
    }
}
