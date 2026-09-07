namespace Arbitarr.Core.Security;

/// <summary>
/// #58: the outcome of presenting a credential at the admin gate, as a closed set of cases the
/// filter switches on. It exists so the filter contains no key material and no matching logic — it
/// asks one question and renders the answer as a status code.
///
/// The three-way split is the point of the issue. Before #58 there were two outcomes (the key
/// matched, or it did not), so "wrong credential" and "right credential, wrong authority" were the
/// same 401 and an operator could not tell a typo from a scope mistake. Now:
///
///   <see cref="NotConfigured"/> -> 503 (or the #43 local-network bypass)
///   <see cref="Rejected"/>      -> 401 — unknown, revoked, or simply wrong
///   <see cref="InsufficientScope"/> -> 403 — a real, live key that is not allowed HERE
///   <see cref="Authorized"/>    -> the request proceeds
/// </summary>
public enum AdminKeyResolutionOutcome
{
    /// <summary>
    /// No credential exists on this deployment at all: no named keys and no legacy setting value.
    /// This is the fresh-install state #43's bootstrap bypass answers, and it is deliberately
    /// distinct from <see cref="Rejected"/> — an unconfigured gate is not a failed authentication.
    /// </summary>
    NotConfigured,

    /// <summary>The presented value matches no live key (absent, unknown, or revoked).</summary>
    Rejected,

    /// <summary>The presented value identifies a live key whose scope does not cover this route.</summary>
    InsufficientScope,

    /// <summary>The presented value identifies a live key with sufficient scope.</summary>
    Authorized,
}

/// <summary>
/// The outcome of a resolution, plus the identity of the key when one was matched.
/// </summary>
/// <param name="Outcome">Which of the four cases applies.</param>
/// <param name="KeyId">
/// The matched key's row id, or <c>null</c> for the legacy environment key (which has no row) and
/// for every non-matching outcome. Used only to record last-used; never rendered to a caller.
/// </param>
/// <param name="Label">
/// The matched key's operator-facing label, or <c>null</c> when nothing matched. Carried so the
/// gate can attribute a request in the log — the attribution half of #58 — WITHOUT the key value
/// ever leaving the resolver. A label is not a secret; the value it authenticated never appears
/// here, and there is deliberately no field on this type that could hold one.
/// </param>
/// <param name="Scope">The matched key's scope, or <c>null</c> when nothing matched.</param>
public sealed record AdminKeyResolution(
    AdminKeyResolutionOutcome Outcome,
    long? KeyId,
    string? Label,
    ApiKeyScope? Scope)
{
    public static AdminKeyResolution NotConfigured { get; } =
        new(AdminKeyResolutionOutcome.NotConfigured, null, null, null);

    public static AdminKeyResolution Rejected { get; } =
        new(AdminKeyResolutionOutcome.Rejected, null, null, null);
}

/// <summary>
/// #58: resolves a presented credential to an <see cref="AdminKeyResolution"/>.
///
/// This supersedes <see cref="IAdminApiKeyReader"/> AS THE GATE'S INPUT, but does not replace it:
/// that interface still exists and is still implemented, because the legacy single shared key is
/// still a valid full-scope credential (issue §Migration — "the existing single env-var key must
/// keep working"), and this resolver consults it through exactly that reader. Deleting it would
/// break every deployment on upgrade, which is the one failure mode this migration must not have.
/// </summary>
public interface IAdminKeyResolver
{
    /// <summary>
    /// Resolves <paramref name="presentedKey"/> against the required <paramref name="requiredScope"/>.
    /// </summary>
    /// <param name="presentedKey">
    /// The raw header value, which may be null or empty — an absent credential resolves to
    /// <see cref="AdminKeyResolutionOutcome.Rejected"/> when any credential exists, and to
    /// <see cref="AdminKeyResolutionOutcome.NotConfigured"/> when none does.
    /// </param>
    /// <param name="requiredScope">The scope the route being called demands.</param>
    Task<AdminKeyResolution> ResolveAsync(
        string? presentedKey,
        ApiKeyScope requiredScope,
        CancellationToken cancellationToken);
}
