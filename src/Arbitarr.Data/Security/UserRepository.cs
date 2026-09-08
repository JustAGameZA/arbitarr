using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Security;

/// <summary>
/// #44: persistence and verification for human operator accounts, with the same
/// validate-at-the-repository-boundary posture as <see cref="ApiKeyRepository"/> and
/// <see cref="Settings.SettingsRepository"/>.
///
/// <para><b>WHAT NEVER LEAVES.</b> No method here returns a password or a hash. Verification
/// (<see cref="VerifyCredentialsAsync"/>) takes a candidate password and returns a user — it never
/// travels in the other direction, and <see cref="UserEntry"/> has no field capable of holding a
/// plaintext even if a future caller tried.</para>
///
/// <para><b>THE BOOTSTRAP IS THE HARD PART.</b> Everything interesting in this type is in
/// <see cref="CreateFirstUserAsync"/>. Read its notes before changing anything here.</para>
/// </summary>
public sealed class UserRepository
{
    /// <summary>Upper bound on a username, matching the entity's <c>HasMaxLength</c>.</summary>
    public const int MaxUsernameLength = 128;

    /// <summary>
    /// Minimum password length. Twelve rather than the more familiar eight, because this is the
    /// ONLY credential quality control in the product: there is no expiry, no complexity rule, no
    /// breach-list check, and — by design (see <see cref="UserEntry"/>) — no recovery path if the
    /// operator locks themselves out. A single generous length floor is the control that survives
    /// having no others, and it is the one users comply with by choosing a passphrase rather than
    /// by decorating a short word with punctuation.
    /// </summary>
    public const int MinPasswordLength = 12;

    /// <summary>
    /// Upper bound on a password. Present so an unauthenticated caller cannot hand the KDF a
    /// megabyte of input and turn the login route into a CPU exhaustion primitive — the hashing
    /// cost is the point of the KDF, and an unbounded input makes that cost attacker-chosen.
    /// </summary>
    public const int MaxPasswordLength = 1024;

    private readonly ArbitarrDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly TimeProvider _timeProvider;

    public UserRepository(ArbitarrDbContext dbContext, IPasswordHasher passwordHasher, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Whether any account exists — the "has this instance been claimed?" question.</summary>
    public Task<bool> AnyUserAsync(CancellationToken cancellationToken) =>
        _dbContext.Users.AsNoTracking().AnyAsync(cancellationToken);

    /// <summary>
    /// Creates the FIRST account, and only ever the first. Returns null when one already exists.
    ///
    /// <para><b>ATOMICITY (AC4), AND WHY IT IS NOT A CHECK-THEN-WRITE.</b> The obvious shape —
    /// "if (await AnyUserAsync()) return null; else insert" — is wrong, and wrong in the way that
    /// matters: two clients racing on a fresh install both evaluate the check before either
    /// inserts, and both create an account. The plan calls that "a full compromise", because the
    /// second account is an attacker's and is indistinguishable from the operator's.</para>
    ///
    /// <para>The guarantee here is the DATABASE's, not this method's. The unique index on
    /// <see cref="UserEntry.Username"/> plus a transaction-wrapped re-check means the losing racer
    /// either sees the winner's row in its re-check or is rejected by the index — there is no
    /// interleaving in which both commit. SQLite serialises writers, so the second transaction's
    /// re-check genuinely observes the first's committed row. A same-username race is caught by the
    /// index; a different-username race is caught by the in-transaction count. Both are tested,
    /// concurrently, by <c>AuthEndpointsTests.Concurrent_first_run_setup_creates_exactly_one_account</c>
    /// — which drives real parallel requests rather than asserting the shape of this code, and uses
    /// DISTINCT usernames on purpose so it exercises the in-transaction re-check rather than the
    /// unique index, which is the easier half.</para>
    ///
    /// <para><b>THIS IS NOT THE WHOLE GATE.</b> This method answers "are there zero users?"; it
    /// does NOT answer "is this caller allowed to claim the instance?" The setup ROUTE additionally
    /// requires the #43 trusted-network predicate (<c>TrustedNetwork.IsTrusted</c>), so a remote
    /// client cannot claim a fresh install even in the instant before the operator does. Those are
    /// two independent conditions and both are required — see <c>AuthEndpoints</c>.</para>
    /// </summary>
    public async Task<UserEntry?> CreateFirstUserAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var trimmed = ValidateUsername(username);
        ValidatePassword(password);

        // Hash BEFORE opening the transaction. The KDF is deliberately slow (that is its whole
        // function), and holding SQLite's write lock across it would serialise every other writer
        // in the application behind one login-setup call.
        var passwordHash = _passwordHasher.Hash(password);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // The re-check INSIDE the transaction is what closes the different-username race; the
        // unique index closes the same-username one. Neither alone is sufficient.
        if (await _dbContext.Users.AnyAsync(cancellationToken))
        {
            return null;
        }

        var entry = new UserEntry
        {
            Username = trimmed,
            PasswordHash = passwordHash,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        _dbContext.Users.Add(entry);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The unique index rejected this insert: another request created the first account
            // between our re-check and our write. Reported as "already claimed" (null), exactly as
            // if we had lost the re-check — the caller must not be able to tell which arm of the
            // race it lost, and there is nothing for it to retry either way.
            //
            // The tracked entity is detached so this scoped DbContext is not left holding a
            // failed insert that a later SaveChangesAsync in the same request would retry.
            _dbContext.Entry(entry).State = EntityState.Detached;
            return null;
        }

        return entry;
    }

    /// <summary>
    /// Verifies a username/password pair, returning the account on success and null on any failure.
    ///
    /// <para><b>ONE ANSWER FOR EVERY FAILURE.</b> An unknown username and a wrong password both
    /// return null, and the caller renders one message for both. Distinguishing them turns the
    /// login route into a username oracle, which is worth more to an attacker than it is to an
    /// operator who mistyped.</para>
    ///
    /// <para>A missing user still runs the KDF against a dummy hash, so the two failures cost the
    /// same wall-clock time. Without that, response timing distinguishes them as reliably as
    /// separate messages would, and the careful message wording above would be decoration.</para>
    /// </summary>
    public async Task<UserEntry?> VerifyCredentialsAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        var candidate = (username ?? string.Empty).Trim();
        var secret = password ?? string.Empty;

        // Bounded before it reaches the KDF, for the reason MaxPasswordLength documents. Rejected
        // silently as a failed login rather than as a validation error: this route answers
        // unauthenticated callers, and "your password was too long to check" is a distinction it
        // does not need to draw.
        if (secret.Length is 0 or > MaxPasswordLength || candidate.Length is 0 or > MaxUsernameLength)
        {
            return null;
        }

        var user = await _dbContext.Users
            .FirstOrDefaultAsync(u => u.Username == candidate, cancellationToken);

        if (user is null)
        {
            // Equalises timing against the found-user path: verification does the same KDF work it
            // would for a live account, and the result is discarded. The decoy hash comes from the
            // INJECTED hasher, so it always matches whatever KDF and cost the real rows use — a
            // hardcoded constant would silently stop equalising the moment the KDF changed, which
            // is exactly when nobody would think to check.
            _passwordHasher.Verify(GetDecoyHash(), secret);
            return null;
        }

        if (!_passwordHasher.Verify(user.PasswordHash, secret))
        {
            return null;
        }

        user.LastLoginAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return user;
    }

    /// <summary>Looks an account up by id, for rendering the signed-in identity.</summary>
    public Task<UserEntry?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

    /// <summary>
    /// Rejects an empty or over-long username and returns the trimmed form (AC24: reject, never
    /// clamp — a silently shortened username is one the operator cannot then log in with).
    /// </summary>
    private static string ValidateUsername(string? username)
    {
        var trimmed = (username ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new UserValidationException("A username must not be empty.");
        }

        if (trimmed.Length > MaxUsernameLength)
        {
            throw new UserValidationException(
                $"A username must be {MaxUsernameLength} characters or fewer.");
        }

        return trimmed;
    }

    private static void ValidatePassword(string? password)
    {
        var secret = password ?? string.Empty;

        if (secret.Length < MinPasswordLength)
        {
            throw new UserValidationException(
                $"A password must be at least {MinPasswordLength} characters. " +
                "A passphrase of a few words is easier to remember and stronger than a short, decorated word.");
        }

        if (secret.Length > MaxPasswordLength)
        {
            throw new UserValidationException(
                $"A password must be {MaxPasswordLength} characters or fewer.");
        }
    }

    /// <summary>
    /// A hash of a random throwaway value, used only to equalise the timing of a failed lookup in
    /// <see cref="VerifyCredentialsAsync"/>. It authenticates nothing: no account holds it, and the
    /// value it was derived from is discarded here and never recorded, so no input verifies against
    /// it.
    ///
    /// <para>Computed once per repository instance rather than once per process, because the
    /// repository is scoped and a static cache would have to be thread-safe for no benefit — one
    /// KDF invocation on the failed-login path is the cost this method exists to pay.</para>
    /// </summary>
    private string GetDecoyHash() => _decoyHash ??= _passwordHasher.Hash(SessionToken.Generate());

    private string? _decoyHash;
}
