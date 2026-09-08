using Arbitarr.Core.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Arbitarr.Api.Security;

/// <summary>
/// #44: the one implementation of <see cref="IPasswordHasher"/>, wrapping ASP.NET Core Identity's
/// <see cref="PasswordHasher{TUser}"/>.
///
/// <para><b>WHY THIS AND NOT SOMETHING HAND-ROLLED.</b> Plan §3.3 forbids custom hashing outright,
/// and this is the well-known primitive already present in the runtime — it ships in the
/// <c>Microsoft.AspNetCore.App</c> shared framework this project already references, so adopting it
/// added no package and no supply-chain surface. Its v3 format is PBKDF2-HMAC-SHA256 with a
/// 128-bit per-password salt, and the format marker, salt, and iteration count are all embedded in
/// the stored string — which is what lets the cost be raised without invalidating existing rows.</para>
///
/// <para><b>THE ITERATION COUNT IS SET EXPLICITLY TO <see cref="IterationCount"/>, NOT INHERITED.</b>
/// The library's default is 100,000, which is below OWASP's current figure for PBKDF2-HMAC-SHA256,
/// so the cost is configured through <see cref="PasswordHasherOptions"/> rather than left at the
/// default. Because the v3 format embeds the count in every stored hash, this is NOT a migration:
/// a hash written at 100,000 still verifies, and <see cref="Verify"/> already treats
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> as success precisely so that
/// raising the cost cannot lock an existing operator out. A test verifies a hash produced at the
/// old count, so that safety property is asserted rather than assumed.</para>
///
/// <para><b>WHY NOT ARGON2ID</b>, which the plan lists first. Argon2id is the better primitive on
/// the merits: it is memory-hard, where PBKDF2 is not, so it resists GPU and ASIC attack far
/// better. It is not used here because no Argon2 implementation is referenced by this solution and
/// the brief is explicit that one may be adopted "only if it is already referenced". Pulling in a
/// new native-code dependency for a single-operator homelab appliance is a larger and less
/// reversible decision than this issue should make on its own. PBKDF2 at this cost is a salted,
/// standardised, deliberately slow KDF and satisfies the acceptance criterion ("a salted modern
/// password hash", "no fast hashes"); the isolation behind <see cref="IPasswordHasher"/> exists
/// precisely so that swapping in Argon2id later touches this file and nothing else.</para>
///
/// <para><b>THE GENERIC PARAMETER IS UNUSED.</b> <see cref="PasswordHasher{TUser}"/> is generic
/// over a user type it never actually inspects — it hashes a string. <see cref="object"/> is passed
/// rather than dragging <c>UserEntry</c> across the layer boundary for a parameter the library
/// ignores.</para>
/// </summary>
public sealed class AspNetPasswordHasher : IPasswordHasher
{
    /// <summary>
    /// PBKDF2-HMAC-SHA256 iterations, per OWASP's current figure for this KDF — six times the
    /// library's 100,000 default.
    ///
    /// <para>Raising this later is safe and requires no migration: the count is embedded in each
    /// stored hash, so old rows keep verifying at their own cost and are reported as
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>, which
    /// <see cref="Verify"/> deliberately treats as success. Lowering it is equally safe for
    /// verification but weakens every hash written afterwards.</para>
    /// </summary>
    public const int IterationCount = 600_000;

    private readonly PasswordHasher<object> _hasher;

    /// <summary>
    /// Takes its cost from the injected <see cref="PasswordHasherOptions"/>, which
    /// <c>Program.cs</c> configures to <see cref="IterationCount"/>.
    ///
    /// <para><b>SUPPLIED THROUGH DI RATHER THAN CONSTRUCTED HERE</b>, so the cost is a
    /// composition-root decision like every other tunable in this application and can be lowered in
    /// a test that would otherwise pay 600,000 iterations per hash. The parameter is optional only
    /// so direct construction still works; when it is omitted the class configures
    /// <see cref="IterationCount"/> itself, so a caller that forgets to register the options can
    /// never silently fall back to the library's weaker 100,000 default.</para>
    /// </summary>
    public AspNetPasswordHasher(IOptions<PasswordHasherOptions>? options = null)
    {
        _hasher = new PasswordHasher<object>(
            options ?? Options.Create(new PasswordHasherOptions { IterationCount = IterationCount }));
    }

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
