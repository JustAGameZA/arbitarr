namespace Arbitarr.Ai.Normalization;

/// <summary>
/// Tokens that title normalization is explicitly permitted to strip/rewrite (e.g. noisy
/// bracketed uploader tags, tracker signatures, promotional boilerplate) — the inverse
/// control of <see cref="AllowList"/>. Checked case-insensitively as whole tokens.
/// </summary>
public sealed class DenyList
{
    private readonly HashSet<string> _tokens;

    public DenyList(IEnumerable<string>? tokens = null)
    {
        _tokens = new HashSet<string>(tokens ?? Default, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Default noise tokens normalization is allowed to remove.</summary>
    public static readonly IReadOnlyList<string> Default = new[]
    {
        "RARBG", "YIFY", "YTS", "EZTV",
    };

    /// <summary>Whether <paramref name="token"/> is explicitly permitted to be removed/rewritten.</summary>
    public bool Contains(string token) => _tokens.Contains(token);
}
