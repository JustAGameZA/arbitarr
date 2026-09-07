using Arbitarr.Core.Security;
using Arbitarr.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Arbitarr.Data.Security;

/// <summary>
/// The plaintext of a newly minted key, returned from <see cref="ApiKeyRepository.CreateAsync"/>
/// and from nowhere else, ever again.
///
/// This type is the ONE place in the codebase where a live admin credential is carried in a shape
/// that can reach a caller, and it exists only so the create path can hand the operator the value
/// once. It is not stored, not cached, and not reachable from any read method — <see cref="Entry"/>
/// has no key column to read it back from (see <see cref="ApiKeyEntry"/>). A future "resend the
/// key" feature is not a small change to this file; it is impossible without changing what is
/// stored, which is the property the issue asks for.
/// </summary>
/// <param name="Entry">The persisted row, carrying no secret.</param>
/// <param name="PlaintextKey">The generated key value. Shown once, then gone.</param>
public sealed record CreatedApiKey(ApiKeyEntry Entry, string PlaintextKey);

/// <summary>
/// #58: persistence and verification for named, scoped admin API keys, with the same
/// validate-at-the-repository-boundary posture as <see cref="Sources.SourceRepository"/> and
/// <see cref="Settings.SettingsRepository"/> (AC24 — reject malformed input, never clamp it).
///
/// <para><b>WHAT NEVER LEAVES.</b> No method on this type returns a stored key value, because none
/// is stored. <see cref="CreateAsync"/> returns the plaintext it just generated (once), and every
/// other method deals in <see cref="ApiKeyEntry"/>, which has no field capable of holding one.
/// The single verification path, <see cref="FindLiveByPresentedKeyAsync"/>, takes a presented value
/// and returns a row — it never travels in the other direction.</para>
///
/// <para><b>THE LOCKOUT REFUSAL.</b> <see cref="RevokeAsync"/> refuses to revoke the last live
/// <see cref="ApiKeyScope.Admin"/> key (AC5). Locking the operator out of their own box is the
/// failure this area of the codebase keeps producing — it is what #43 existed to undo — and a
/// confirmation dialog is not a mitigation, because the operator clicking it does not know it is
/// the last one. The refusal counts only NAMED keys, deliberately: see that method's note on why
/// the legacy key is not counted as a rescue.</para>
/// </summary>
public sealed class ApiKeyRepository
{
    /// <summary>Upper bound on a label, matching the entity's <c>HasMaxLength(128)</c>.</summary>
    public const int MaxLabelLength = 128;

    private readonly ArbitarrDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public ApiKeyRepository(ArbitarrDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Mints a key: generates the value, stores only its hash, and returns the plaintext once.
    /// Rejects an empty, over-long, or colliding label (AC24 rejections, not clamps).
    /// </summary>
    public async Task<CreatedApiKey> CreateAsync(string label, ApiKeyScope scope, CancellationToken cancellationToken)
    {
        var trimmed = (label ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ApiKeyValidationException("An API key label must not be empty.");
        }

        if (trimmed.Length > MaxLabelLength)
        {
            throw new ApiKeyValidationException(
                $"An API key label must be {MaxLabelLength} characters or fewer.");
        }

        if (!Enum.IsDefined(scope))
        {
            throw new ApiKeyValidationException($"'{scope}' is not a valid API key scope.");
        }

        var collides = await _dbContext.ApiKeys.AsNoTracking()
            .AnyAsync(k => k.Label.ToLower() == trimmed.ToLower(), cancellationToken);

        if (collides)
        {
            throw new ApiKeyValidationException($"An API key labelled '{trimmed}' already exists.");
        }

        var plaintext = ApiKeyHasher.Generate();
        var entry = new ApiKeyEntry
        {
            Label = trimmed,
            KeyHash = ApiKeyHasher.Hash(plaintext),
            Scope = scope,
            CreatedAt = _timeProvider.GetUtcNow(),
        };

        _dbContext.ApiKeys.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CreatedApiKey(entry, plaintext);
    }

    /// <summary>
    /// Every key, revoked ones included, oldest first. Revoked rows are listed rather than hidden
    /// because a revoked key with a recent last-used time is exactly what an operator needs to see
    /// after revoking one and finding something broke.
    /// </summary>
    public Task<List<ApiKeyEntry>> GetAllAsync(CancellationToken cancellationToken) =>
        _dbContext.ApiKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(cancellationToken);

    /// <summary>
    /// Revokes one key by id, without touching any other (the issue's "revoking one key does not
    /// affect any other" criterion — this is a single-row UPDATE, so there is no path by which it
    /// could).
    ///
    /// <para>Returns false for an unknown id so the endpoint answers 404 rather than reporting a
    /// revocation that did not happen. Revoking an already-revoked key is idempotent: the original
    /// <see cref="ApiKeyEntry.RevokedAt"/> stands, because when it stopped working is a fact about
    /// the past and a second call should not rewrite it.</para>
    ///
    /// <para><b>AC5.</b> Refuses when this is the last live <see cref="ApiKeyScope.Admin"/> key.
    /// The count deliberately ignores the legacy environment key even though that key would in fact
    /// still admit the operator: relying on it as the rescue path means an operator who has migrated
    /// off it — the whole point of #58 — gets no refusal at all, and the check would silently stop
    /// protecting precisely the deployments furthest along. Refusing on named keys alone is the
    /// conservative direction: the worst case is an operator being told to mint a replacement first.</para>
    /// </summary>
    public async Task<bool> RevokeAsync(long id, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (entry is null)
        {
            return false;
        }

        if (entry.RevokedAt is not null)
        {
            return true;
        }

        if (entry.Scope == ApiKeyScope.Admin)
        {
            var otherLiveAdminKeys = await _dbContext.ApiKeys.AsNoTracking()
                .CountAsync(
                    k => k.Id != id && k.Scope == ApiKeyScope.Admin && k.RevokedAt == null,
                    cancellationToken);

            if (otherLiveAdminKeys == 0)
            {
                throw new ApiKeyValidationException(
                    $"'{entry.Label}' is the last API key with admin scope. Revoking it would leave no " +
                    "key able to administer this instance. Create a replacement admin-scope key first, " +
                    "then revoke this one.");
            }
        }

        entry.RevokedAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// The verification lookup: hashes <paramref name="presentedKey"/> and returns the matching LIVE
    /// row, or null when there is none (unknown or revoked — the caller cannot tell those apart, and
    /// deliberately: telling an attacker that a key was once valid is information they should not
    /// get for free).
    ///
    /// <para>The comparison is done in the database as an equality on the indexed hash column, which
    /// is what makes this an index seek rather than a scan over every key. That is not the
    /// fixed-time comparison <see cref="ApiKeyHasher.HashesMatch"/> provides, and the difference is
    /// intended: what leaks from a timing difference here is at most whether a hash PREFIX exists,
    /// and the hash is a SHA-256 digest of 256 random bits, so an attacker who could extract it
    /// entirely still holds a value they cannot invert into the key the gate actually wants.</para>
    /// </summary>
    public Task<ApiKeyEntry?> FindLiveByPresentedKeyAsync(string presentedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return Task.FromResult<ApiKeyEntry?>(null);
        }

        var hash = ApiKeyHasher.Hash(presentedKey);

        return _dbContext.ApiKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, cancellationToken);
    }

    /// <summary>Whether any live key exists at all — the "is this deployment configured?" question.</summary>
    public Task<bool> AnyLiveKeyAsync(CancellationToken cancellationToken) =>
        _dbContext.ApiKeys.AsNoTracking().AnyAsync(k => k.RevokedAt == null, cancellationToken);

    /// <summary>
    /// Stamps <paramref name="lastUsedAt"/> onto one key. Called only from
    /// <c>ApiKeyLastUsedRecorder</c>, which throttles it — see that type for why this must not
    /// become a synchronous write on every gated request.
    ///
    /// A row revoked between verification and this write is left alone: the stamp is not worth
    /// resurrecting a tombstoned row's mtime for, and a revoked key's last-used time should record
    /// when it last WORKED.
    /// </summary>
    public async Task RecordLastUsedAsync(long id, DateTimeOffset lastUsedAt, CancellationToken cancellationToken)
    {
        var entry = await _dbContext.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (entry is null || entry.RevokedAt is not null)
        {
            return;
        }

        entry.LastUsedAt = lastUsedAt;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
