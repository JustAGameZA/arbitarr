using System.Security.Cryptography;
using System.Text;

namespace Arbitarr.Core.Security;

/// <summary>
/// #44: how a session token is minted and turned into the value actually stored.
///
/// <para>SHA-256 over the UTF-8 bytes, lowercase hex — the same treatment
/// <see cref="ApiKeyHasher"/> gives an API key, and for the same reason, which is worth stating
/// rather than leaving as an apparent inconsistency with <see cref="IPasswordHasher"/> two files
/// over: a session token is 256 bits from <see cref="RandomNumberGenerator"/>, so it is not
/// guessable and stretching would buy nothing while adding per-request cost to a hash computed on
/// the hot path of every cookie-authenticated call. A PASSWORD gets the KDF because a human chose
/// it; a token never is.</para>
///
/// <para><b>WHAT THE HASHING BUYS.</b> The sessions table holds no usable credential. A database
/// leak, or a #56 backup archive, yields hashes — not cookies an attacker can replay. The plaintext
/// exists in exactly two places: the <c>Set-Cookie</c> header that issued it, and the browser
/// holding it.</para>
///
/// <para>Deliberately a separate type from <see cref="ApiKeyHasher"/> rather than a shared "hash a
/// random secret" helper. The two have identical mechanics today and different reasons to change —
/// a session token's storage could reasonably grow a rotation scheme that an API key's must not —
/// and merging them would make one type's rationale silently govern the other.</para>
/// </summary>
public static class SessionToken
{
    /// <summary>Bytes of entropy in a generated token. 32 bytes = 256 bits.</summary>
    private const int TokenBytes = 32;

    /// <summary>Length of a <see cref="Hash"/> result: SHA-256 as lowercase hex.</summary>
    public const int HashLength = 64;

    /// <summary>
    /// Mints a new session token. Base64url (RFC 4648 §5) with no padding, so the value is safe in
    /// a cookie without escaping — a raw Base64 <c>+</c>, <c>/</c> or <c>=</c> is legal in a cookie
    /// value by RFC 6265 but is exactly the kind of character a proxy or client library has been
    /// known to re-encode, and a silently re-encoded token is a login that works everywhere except
    /// the one deployment that matters.
    /// </summary>
    public static string Generate()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>Hashes <paramref name="token"/> to the lowercase-hex form stored in the database.</summary>
    public static string Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }
}
