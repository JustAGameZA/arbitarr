using System.Security.Cryptography;
using System.Text;

namespace Arbitarr.Core.Security;

/// <summary>
/// #58: how a named API key is turned into the value actually stored.
///
/// SHA-256 over the UTF-8 bytes, lowercase hex. NOT a password KDF (bcrypt/argon2/PBKDF2), and the
/// reason is that the input is not a password: <see cref="Generate"/> mints 256 bits from
/// <see cref="RandomNumberGenerator"/>, so the search space is not guessable at any iteration count
/// and stretching would buy nothing against an offline attacker while adding per-request cost to a
/// hash computed on the hot path of every gated call. Stretching is what you do when the input is
/// low-entropy and human-chosen; this one never is, because the operator does not choose it.
///
/// No per-key salt for the same reason plus one more: the lookup is BY hash. A salted scheme cannot
/// look a key up — it must load every row and test each one — and a 256-bit random input has no
/// rainbow table to defend against. The property that matters here is the one the issue asks for:
/// a database leak (or a #56 backup archive) yields hashes, not working credentials.
/// </summary>
public static class ApiKeyHasher
{
    /// <summary>Bytes of entropy in a generated key. 32 bytes = 256 bits.</summary>
    private const int KeyBytes = 32;

    /// <summary>Length of a <see cref="Hash"/> result: SHA-256 as lowercase hex.</summary>
    public const int HashLength = 64;

    /// <summary>
    /// Mints a new key value. Base64url (RFC 4648 §5) so the result is safe in a header, a shell
    /// argument, and a *arr configuration field without escaping, and carries no padding for a
    /// caller to trim off and then wonder why the key stopped working.
    /// </summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(KeyBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Hashes <paramref name="key"/> to the lowercase-hex form stored in the database.</summary>
    public static string Hash(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    /// <summary>
    /// Fixed-time comparison of two stored hashes, matching the convention
    /// <c>AdminApiKeyFilter</c> and <c>ConfiguredClientApiKeyResolver</c> already use.
    ///
    /// Comparing HASHES rather than key values is what makes the timing question mostly moot — an
    /// attacker who could learn a hash byte-by-byte still could not invert it — but the fixed-time
    /// form costs nothing and keeps one convention across the three comparison sites rather than
    /// leaving a reader to work out why this one is different.
    /// </summary>
    public static bool HashesMatch(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        var left = Encoding.UTF8.GetBytes(a);
        var right = Encoding.UTF8.GetBytes(b);

        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
