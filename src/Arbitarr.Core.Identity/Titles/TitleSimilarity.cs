using System.Text;

namespace Arbitarr.Core.Identity.Titles;

/// <summary>
/// Token-set (Jaccard) similarity between an extracted release series name and a
/// <see cref="SeriesIdentity"/>. Pure string logic with no metadata dependency, so it is the
/// fail-open ordering signal <see cref="Scoring.ReleaseRanker"/> falls back to when numbering
/// corroboration is unavailable (P1/P3).
/// </summary>
public static class TitleSimilarity
{
    /// <summary>Best 0..1 similarity of <paramref name="seriesName"/> against the primary and alternate titles.</summary>
    public static double Score(string seriesName, SeriesIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var best = Score(seriesName, identity.PrimaryTitle);
        foreach (var alternate in identity.AlternateTitles)
        {
            best = Math.Max(best, Score(seriesName, alternate));
        }

        return best;
    }

    /// <summary>Jaccard similarity of the two token sets (0 when either side has no tokens). Letter and digit runs are separate tokens.</summary>
    public static double Score(string left, string right)
    {
        var a = Tokenize(left);
        var b = Tokenize(right);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        // Letters and digits are separate tokens ("SAC2045" -> "sac", "2045") so a scene-style
        // compaction of a title still shares tokens with its spaced canonical form.
        var current = new StringBuilder();
        var currentIsDigit = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                var isDigit = char.IsDigit(ch);
                if (current.Length > 0 && isDigit != currentIsDigit)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                currentIsDigit = isDigit;
                current.Append(char.ToLowerInvariant(ch));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
