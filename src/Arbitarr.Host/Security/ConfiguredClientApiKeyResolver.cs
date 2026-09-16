using System.Security.Cryptography;
using System.Text;
using Arbitarr.Core.Diagnostics;
using Arbitarr.Core.Security;

namespace Arbitarr.Host.Security;

/// <summary>
/// Config-backed <see cref="IClientApiKeyResolver"/>: reads a list of named client keys from
/// <c>Arbitarr:ClientApiKeys</c> (each entry has a <c>Name</c> and a <c>Key</c>). A single legacy
/// <c>Arbitarr:ApiKey</c> value (no name) collapses to one named key, <c>"default"</c>, for
/// backward compatibility with the M1-9 single-key configuration shape.
///
/// Comparison against every configured key is fixed-time (<see cref="CryptographicOperations.FixedTimeEquals"/>)
/// so response timing cannot be used to narrow down a valid key.
///
/// <para>Since #97 this is no longer the resolver the endpoints see: it is the environment half of
/// <c>DbClientApiKeyResolver</c>, which consults minted keys first and falls back to this. It stays
/// a separate type rather than being folded in because the environment keys are a composition-root
/// concern (they are bound from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> at
/// startup) and the minted keys are a data-layer one.</para>
/// </summary>
public sealed class ConfiguredClientApiKeyResolver : IClientApiKeyResolver
{
    private readonly IReadOnlyList<(string Name, byte[] KeyBytes)> _keys;

    public ConfiguredClientApiKeyResolver(IReadOnlyList<NamedClientApiKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys
            .Where(k => !string.IsNullOrEmpty(k.Key))
            .Select(k => (k.Name, Encoding.UTF8.GetBytes(k.Key)))
            .ToArray();
    }

    /// <summary>
    /// Resolves against the configured keys. Synchronous work behind an async signature — there is
    /// nothing to await here, and <see cref="Task.FromResult{TResult}"/> keeps that honest rather
    /// than spending a state machine on it.
    /// </summary>
    public Task<ClientKeyContext?> ResolveAsync(string? apikey, CancellationToken cancellationToken) =>
        Task.FromResult(Resolve(apikey));

    /// <summary>
    /// The comparison itself, exposed synchronously so <c>DbClientApiKeyResolver</c> can call it
    /// without an await on a path that never had one.
    /// </summary>
    public ClientKeyContext? Resolve(string? apikey)
    {
        if (string.IsNullOrEmpty(apikey) || _keys.Count == 0)
        {
            return null;
        }

        var providedBytes = Encoding.UTF8.GetBytes(apikey);
        ClientKeyContext? match = null;

        // Iterate every configured key (never short-circuit on the first match) so the total
        // comparison time does not vary with which key (if any) matched.
        foreach (var (name, keyBytes) in _keys)
        {
            var isMatch = providedBytes.Length == keyBytes.Length
                && CryptographicOperations.FixedTimeEquals(providedBytes, keyBytes);
            if (isMatch)
            {
                // KeyId null: an environment key has no row to stamp a last-used time onto.
                match = new ClientKeyContext(name);
            }
        }

        return match;
    }
}

/// <summary>A single named client apikey, as bound from configuration.</summary>
/// <param name="Name">The key's configured name (surfaced to future M4 filter-profile association).</param>
/// <param name="Key">The literal key value clients must present.</param>
public sealed record NamedClientApiKey(string Name, string Key)
{
    /// <summary>
    /// Renders the key's NAME in full and the key itself as
    /// <see cref="CredentialPatterns.Replacement"/> (arb-1ox9, #346 review).
    /// </summary>
    /// <remarks>
    /// <para><b>OVERRIDDEN BECAUSE THE SYNTHESISED ONE PRINTS THE KEY.</b> A positional record's
    /// compiler-generated <c>ToString</c> renders every member by name and value, so the default here
    /// produced <c>NamedClientApiKey { Name = default, Key = the-actual-client-key }</c>. This record
    /// is bound at startup from <c>Arbitarr:ClientApiKeys</c> and is exactly the shape a
    /// configuration-binding diagnostic ("bound N client keys: …") would format.
    /// </para>
    ///
    /// <para><b>Neither existing mechanism covers it</b> (CLAUDE.md §1).
    /// <c>IHttpClientFactory</c>'s URI redaction is scoped to an outbound request's query string, and
    /// this is not a URI. <c>LogMessageCleanser</c> would scrub a rendered <c>Key = …</c> via its
    /// <c>NamedCredential</c> arm, but <b>it runs only in the LOG SINK</b>: an exception message or
    /// console line carrying this record never meets it, and startup binding is precisely where
    /// console output happens.</para>
    ///
    /// <para><b>The redaction is UNCONDITIONAL, matching <c>SonarrCredential</c>'s posture</b> and
    /// deliberately NOT the presence-bit shape the admin request bodies use
    /// (<c>Arbitarr.Api.Admin.UpdateArrConfigRequest</c> and its siblings). Those bodies render a
    /// null key distinctly because there a null means "the operator did not submit one", which is a
    /// real diagnostic distinction. <see cref="Key"/> here is non-nullable and always present, so
    /// there is no such bit to carry.</para>
    ///
    /// <para><see cref="Name"/> is rendered in full: it is a configured label, not a credential, and
    /// printing it is what makes this override useful for diagnostics rather than merely silent. The
    /// marker is <see cref="CredentialPatterns.Replacement"/> rather than a literal, so this
    /// redaction and the cleanser's can never drift into two spellings a search would have to know
    /// about separately.</para>
    /// </remarks>
    public override string ToString() =>
        $"{nameof(NamedClientApiKey)} {{ {nameof(Name)} = {Name}, "
        + $"{nameof(Key)} = {CredentialPatterns.Replacement} }}";
}
