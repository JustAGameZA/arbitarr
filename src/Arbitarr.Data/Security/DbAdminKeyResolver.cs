using Arbitarr.Core.Security;

namespace Arbitarr.Data.Security;

/// <summary>
/// #58: the single resolution point the admin gate consults. It answers one question — "may this
/// presented value do this?" — and every branch of the answer lives here rather than in the filter,
/// so the filter holds no key material and no matching logic.
///
/// <para><b>TWO CREDENTIAL SOURCES, ONE MODEL.</b> A presented value is checked against the named
/// key table first, then against the legacy <c>SettingKey.AdminApiKey</c> value read through
/// <see cref="IAdminApiKeyReader"/>. The legacy key resolves as an implicit, unrevokable
/// <see cref="ApiKeyScope.Admin"/> key labelled <see cref="LegacyKeyLabel"/> — the issue's
/// migration requirement, and non-negotiable: an upgrade that silently invalidates the credential
/// every caller on a homelab box is currently using, while the operator is not watching, is the
/// worst available failure. It is NOT auto-revoked and NOT auto-migrated into a row; it stays
/// exactly where it was, writable through the same <c>PUT /api/admin/security/admin-key</c> as
/// before.</para>
///
/// <para><b>ORDER MATTERS.</b> Named keys are checked first so that an operator who mints a named
/// key whose value happens to equal the legacy one still gets that key's identity and scope, rather
/// than being silently upgraded to full authority by the legacy branch. In practice the values
/// never collide — <see cref="ApiKeyHasher.Generate"/> produces 256 random bits — but ordering the
/// checks by which answer is more specific costs nothing and removes the question.</para>
///
/// <para><b>WHY <see cref="IAdminApiKeyReader"/> SURVIVES #58.</b> It was the whole gate before this
/// issue and is now one of two inputs to it. It is not deprecated and must not be deleted: it is
/// how the legacy key keeps working, and <c>AdminSecurityEndpoints</c> still writes the value it
/// reads.</para>
/// </summary>
public sealed class DbAdminKeyResolver : IAdminKeyResolver
{
    /// <summary>
    /// The label under which the pre-#58 shared key is presented in the UI. The issue asks for it to
    /// be "visible in the list so it is not invisible authority" — an unlisted credential that still
    /// opens every door is exactly the state #58 exists to end, and it is the one key an operator is
    /// most likely to have forgotten is still live.
    /// </summary>
    public const string LegacyKeyLabel = "legacy environment key";

    private readonly ApiKeyRepository _repository;
    private readonly IAdminApiKeyReader _legacyKeyReader;

    public DbAdminKeyResolver(ApiKeyRepository repository, IAdminApiKeyReader legacyKeyReader)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _legacyKeyReader = legacyKeyReader ?? throw new ArgumentNullException(nameof(legacyKeyReader));
    }

    public async Task<AdminKeyResolution> ResolveAsync(
        string? presentedKey,
        ApiKeyScope requiredScope,
        CancellationToken cancellationToken)
    {
        var legacyKey = await _legacyKeyReader.GetCurrentKeyAsync(cancellationToken);
        var anyNamedKey = await _repository.AnyLiveKeyAsync(cancellationToken);

        // "Nothing is configured" is decided BEFORE looking at what was presented, and is the union
        // of both credential sources: a deployment with named keys but no legacy value is configured,
        // and so is one with only the legacy value. Only a deployment with neither is unconfigured,
        // and only then does #43's bootstrap bypass apply — a box that HAS keys must never fall back
        // to admitting an unkeyed local request just because the caller sent nothing.
        if (string.IsNullOrEmpty(legacyKey) && !anyNamedKey)
        {
            return AdminKeyResolution.NotConfigured;
        }

        if (string.IsNullOrEmpty(presentedKey))
        {
            return AdminKeyResolution.Rejected;
        }

        var named = await _repository.FindLiveByPresentedKeyAsync(presentedKey, cancellationToken);
        if (named is not null)
        {
            return Authorize(named.Id, named.Label, named.Scope, requiredScope);
        }

        if (!string.IsNullOrEmpty(legacyKey) && ApiKeyHasher.HashesMatch(
                ApiKeyHasher.Hash(presentedKey),
                ApiKeyHasher.Hash(legacyKey)))
        {
            // KeyId null: the legacy key has no row, so there is nothing to stamp a last-used time
            // onto. That is a real gap in attribution for this one credential, and it is the honest
            // representation of it — inventing a synthetic row would make a value the operator set
            // by hand look like one Arbitarr minted, and would offer a revoke button that cannot work.
            return Authorize(null, LegacyKeyLabel, ApiKeyScope.Admin, requiredScope);
        }

        return AdminKeyResolution.Rejected;
    }

    /// <summary>
    /// Scope containment. <see cref="ApiKeyScope.Admin"/> covers everything;
    /// <see cref="ApiKeyScope.ReadOnly"/> covers only a read-only requirement. Expressed as a
    /// comparison over the enum's declared order rather than a switch, so adding a scope between the
    /// two would inherit the ordering rather than silently falling through a missing case.
    /// </summary>
    private static AdminKeyResolution Authorize(long? keyId, string label, ApiKeyScope scope, ApiKeyScope requiredScope) =>
        scope >= requiredScope
            ? new AdminKeyResolution(AdminKeyResolutionOutcome.Authorized, keyId, label, scope)
            : new AdminKeyResolution(AdminKeyResolutionOutcome.InsufficientScope, keyId, label, scope);
}
