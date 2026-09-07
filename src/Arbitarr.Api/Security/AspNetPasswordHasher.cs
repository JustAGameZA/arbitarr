using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Identity;

namespace Arbitarr.Api.Security;

/// <summary>
/// #44: the one implementation of <see cref="IPasswordHasher"/>, wrapping ASP.NET Core Identity's
/// <see cref="PasswordHasher{TUser}"/>.
///
/// <para><b>WHY THIS AND NOT SOMETHING HAND-ROLLED.</b> Plan §3.3 forbids custom hashing outright,
/// and this is the well-known primitive already present in the runtime — it ships in the
/// <c>Microsoft.AspNetCore.App</c> shared framework this project already references, so adopting it
/// added no package and no supply-chain surface. Its v3 format is PBKDF2-HMAC-SHA256 with a
/// 128-bit per-password salt and 100,000 iterations, and the format marker, salt, and iteration
/// count are all embedded in the stored string — which is what lets the cost be raised later
/// without invalidating existing rows.</para>
///
/// <para><b>WHY NOT ARGON2ID</b>, which the plan lists first. Argon2id is the better primitive on
/// the merits: it is memory-hard, where PBKDF2 is not, so it resists GPU and ASIC attack far
/// better. It is not used here because no Argon2 implementation is referenced by this solution and
/// the brief is explicit that one may be adopted "only if it is already referenced". Pulling in a
/// new native-code dependency for a single-operator homelab appliance is a larger and less
/// reversible decision than this issue should make on its own. PBKDF2 at 100k iterations is a
/// salted, standardised, deliberately slow KDF and satisfies the acceptance criterion ("a salted
/// modern password hash", "no fast hashes"); the isolation behind <see cref="IPasswordHasher"/>
/// exists precisely so that swapping in Argon2id later touches this file and nothing else.</para>
///
/// <para><b>THE GENERIC PARAMETER IS UNUSED.</b> <see cref="PasswordHasher{TUser}"/> is generic
/// over a user type it never actually inspects — it hashes a string. <see cref="object"/> is passed
/// rather than dragging <c>UserEntry</c> across the layer boundary for a parameter the library
/// ignores.</para>
/// </summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<object> _hasher = new();

    /// <summary>
    /// The instance the library hashes "for". Never read by
    /// <see cref="PasswordHasher{TUser}.HashPassword"/> or
    /// <see cref="PasswordHasher{TUser}.VerifyHashedPassword"/> — both take it only to satisfy the
    /// generic signature — so one shared instance is correct rather than merely convenient.
    /// </summary>
    private static readonly object HashSubject = new();

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return _hasher.HashPassword(HashSubject, password);
    }

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            var result = _hasher.VerifyHashedPassword(HashSubject, hash, password);

            // SuccessRehashNeeded is a SUCCESS: it means the stored hash used an older format or a
            // lower iteration count than the current default. Treating it as failure would lock
            // every existing operator out on the upgrade that raised the cost — the exact class of
            // silent, delayed breakage this codebase keeps guarding against. Arbitarr does not
            // opportunistically rehash on login today; when it does, this is the branch that
            // triggers it.
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A stored value that is not a valid hash (a corrupted row, or one written by hand).
            // Denies access rather than turning every login attempt into a 500 — the contract
            // IPasswordHasher.Verify states.
            return false;
        }
    }
}
