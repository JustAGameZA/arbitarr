namespace Arbitarr.Core.Security;

/// <summary>
/// #44: the single abstraction over password hashing, so the KDF can be replaced without any
/// caller changing (plan §4 step 3).
///
/// <para><b>WHY THIS IS NOT <see cref="ApiKeyHasher"/>.</b> That type is deliberately a plain
/// SHA-256 with no stretching and no salt, and its own doc explains why: an API key is 256 bits
/// minted from <c>RandomNumberGenerator</c>, so it is not guessable at any iteration count. A
/// PASSWORD is the opposite input — chosen by a human, low-entropy, and drawn from a distribution
/// an offline attacker can enumerate. Applying the API-key reasoning to a password would be the
/// single worst mistake available in this feature, so the two live behind different types with
/// different documentation rather than behind one "hash a secret" helper that would invite exactly
/// that substitution.</para>
///
/// <para><b>NO CUSTOM HASHING</b> (plan §3.3, and the issue's "no fast hashes"). The only
/// implementation wraps a well-known library primitive. There is no application-level pepper
/// invented for this project, no SHA-family password hashing, and no home-grown iteration loop.</para>
///
/// <para>Declared in Core so the Data and Api layers can both depend on it without either depending
/// on the other — the same placement <see cref="IApiKeyLastUsedRecorder"/> takes, and required by
/// <c>CoreIsolationTests</c>.</para>
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes <paramref name="password"/> into the opaque string stored in the users table. The
    /// result embeds its own per-password salt and cost parameters, so verification needs nothing
    /// else and a future cost increase does not invalidate existing rows.
    /// </summary>
    string Hash(string password);

    /// <summary>
    /// Verifies <paramref name="password"/> against a stored <paramref name="hash"/>.
    /// Returns false — never throws — for a malformed or unrecognised stored value, so a corrupted
    /// row denies access rather than turning every login attempt into a 500.
    /// </summary>
    bool Verify(string hash, string password);
}
