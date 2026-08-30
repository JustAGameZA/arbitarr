namespace Arbitarr.Ai.Normalization;

/// <summary>
/// AC26: asserts that normalization did not remove or alter any allow-listed (identity-relevant)
/// token between the original and normalized title. <see cref="Arbitarr.Ai"/> cannot depend on
/// <see cref="Arbitarr.Media"/> (AC6a) to run the real *arr-compatible parser both before and after
/// normalization, so this guard instead performs a structural check: every allow-listed token
/// present in the original title must still be present, verbatim, in the normalized title. A
/// violation means normalization would have silently broken what a downstream parser relies on —
/// callers must reject the normalized result and fall back to the original title rather than
/// serve a title with lost identity information.
/// </summary>
public static class DifferentialParseGuard
{
    /// <summary>
    /// Returns <see langword="true"/> when every <paramref name="allowList"/> token found in
    /// <paramref name="originalTitle"/> is still present (case-insensitively) in
    /// <paramref name="normalizedTitle"/>. Returns <see langword="false"/> on any drop, signaling
    /// the caller to discard the normalized result.
    /// </summary>
    public static bool Passes(string originalTitle, string normalizedTitle, AllowList allowList)
    {
        ArgumentNullException.ThrowIfNull(originalTitle);
        ArgumentNullException.ThrowIfNull(normalizedTitle);
        ArgumentNullException.ThrowIfNull(allowList);

        var originalTokens = Tokenize(originalTitle);
        var normalizedTokens = new HashSet<string>(Tokenize(normalizedTitle), StringComparer.OrdinalIgnoreCase);

        foreach (var token in originalTokens)
        {
            if (allowList.Contains(token) && !normalizedTokens.Contains(token))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> Tokenize(string title) =>
        title.Split(
            new[] { ' ', '.', '-', '_', '[', ']', '(', ')' },
            StringSplitOptions.RemoveEmptyEntries);
}
