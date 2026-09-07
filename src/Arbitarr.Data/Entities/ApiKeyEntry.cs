using Arbitarr.Core.Security;

namespace Arbitarr.Data.Entities;

/// <summary>
/// #58: one named, scoped API key. The row a machine caller (Sonarr, Radarr, a script) authenticates
/// as, replacing the single shared admin secret every caller used to present.
///
/// <para><b>THE SECRET RULE.</b> There is no column for the key value, and deliberately no nullable
/// "Value"/"Secret" property a future edit could start populating — the same structural posture
/// <see cref="Source"/> and <see cref="EventEntry"/> take. Only <see cref="KeyHash"/> exists, and it
/// is a SHA-256 digest (see <see cref="ApiKeyHasher"/>). The plaintext exists exactly once, in the
/// response to the create call that minted it, and is never written anywhere. "Show me that key
/// again" is therefore impossible by construction rather than by policy, and the UI says so at
/// creation time because there is no later screen that could.</para>
///
/// <para>Distinct from <see cref="ApiKeyProfileEntry"/>, which maps a Torznab/Newznab CLIENT apikey
/// to a filter profile — a different credential for a different surface. This type governs the
/// admin gate.</para>
/// </summary>
public sealed class ApiKeyEntry
{
    /// <summary>Surrogate primary key.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The operator-facing name ("sonarr", "radarr", "monitoring"). This is the attribution half of
    /// the issue: it is what appears in the log line for a gated request, so traffic can be told
    /// apart by caller. Unique, so two keys cannot be indistinguishable in exactly the place the
    /// feature exists to make them distinguishable.
    /// </summary>
    public required string Label { get; set; }

    /// <summary>
    /// SHA-256 of the key value, lowercase hex — never the value itself. Unique and indexed: it is
    /// the lookup column, because verification hashes the presented value and finds the row.
    /// </summary>
    public required string KeyHash { get; set; }

    /// <summary>What this key may do. See <see cref="ApiKeyScope"/>.</summary>
    public ApiKeyScope Scope { get; set; }

    /// <summary>When the key was minted.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this key last authenticated a request, or null if it never has.
    ///
    /// This is the field that makes revocation safe rather than a guess: it is how an operator
    /// answers "is anything still using this?" before pulling it. Written through a throttle
    /// (<c>ApiKeyLastUsedRecorder</c>), never synchronously per request.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>
    /// When the key was revoked, or null while it is live. Revocation is a tombstone rather than a
    /// DELETE so a revoked key's label and last-used time survive the revocation — an operator
    /// investigating "what was "monitoring" and when did it last work?" after revoking it still has
    /// an answer, and a leaked key's hash can never be silently re-minted onto a fresh row.
    /// </summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
