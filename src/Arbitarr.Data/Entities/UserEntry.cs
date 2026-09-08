namespace Arbitarr.Data.Entities;

/// <summary>
/// #44: one human operator account.
///
/// <para><b>THE SECRET RULE.</b> There is no column for a password, and deliberately no nullable
/// "Password"/"PlaintextPassword" property a future edit could start populating — the same
/// structural posture <see cref="ApiKeyEntry"/>, <see cref="Source"/> and <see cref="EventEntry"/>
/// take. Only <see cref="PasswordHash"/> exists, produced by
/// <c>Arbitarr.Core.Security.IPasswordHasher</c> (a real KDF with a per-password salt — NOT
/// <c>ApiKeyHasher</c>, whose no-stretching rationale applies only to random keys). The plaintext
/// exists for the duration of one request and is never written anywhere, so "show me the password"
/// is impossible by construction rather than by policy.</para>
///
/// <para><b>NO RECOVERY COLUMNS, DELIBERATELY.</b> There is no reset token, no security question,
/// and no recovery e-mail — plan §2 requires that decision to be explicit rather than absent, and
/// the decision is that Arbitarr has no password recovery. A homelab appliance with no mail
/// transport cannot send a reset, and a recovery mechanism reachable from the LAN would be a second
/// authentication path weaker than the first. The documented answer is: restore the config database
/// from a #56 backup, or delete rows from this table through the config bind mount. That is stated
/// in README.md and in the login surface's help text, because an undocumented "none" is
/// indistinguishable from an oversight.</para>
/// </summary>
public sealed class UserEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The operator's login name. Unique — and the uniqueness is not merely a data-hygiene rule:
    /// it is the mechanism that makes first-account creation atomic under concurrency. See
    /// <c>UserRepository.CreateFirstUserAsync</c>, which relies on the database rejecting the
    /// second of two racing inserts rather than on a check-then-write that has a window between
    /// the two halves.
    /// </summary>
    public required string Username { get; set; }

    /// <summary>
    /// The KDF output, including its embedded per-password salt and cost parameters — never a
    /// password. Opaque to everything except <c>IPasswordHasher.Verify</c>.
    /// </summary>
    public required string PasswordHash { get; set; }

    /// <summary>When the account was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this account last completed a successful login, or null if it never has.</summary>
    public DateTimeOffset? LastLoginAt { get; set; }
}
