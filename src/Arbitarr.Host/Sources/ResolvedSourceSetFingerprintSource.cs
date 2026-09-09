using System.Security.Cryptography;
using System.Text;
using Arbitarr.Api.Search;

namespace Arbitarr.Host.Sources;

/// <summary>
/// arb-b5z: the live <see cref="ISourceSetFingerprintSource"/>, deriving the fingerprint from the
/// <see cref="ResolvedSourceConfiguration"/> this process resolved at startup.
/// </summary>
/// <remarks>
/// <para><b>THE VALUE IS CONSTANT FOR THE PROCESS, AND THAT IS THE POINT.</b>
/// <see cref="ResolvedSourceConfiguration"/> is written once by <c>SourceSeeder</c> before the first
/// request, so this returns the same string for the life of the host. It is not trying to notice a
/// source change while running — it cannot, and nothing here should try. It exists so that the NEXT
/// process, started after a source edit (which requires a restart today), computes a DIFFERENT
/// snapshot token and therefore cannot be served the SQLite-persisted snapshot rows the previous one
/// left behind. See <see cref="ISourceSetFingerprintSource"/> for the full sequence.</para>
///
/// <para><b>THE API KEY IS NOT IN THE FINGERPRINT, deliberately.</b> The question this answers is
/// "which sources produced that result set", and a base URL plus display name settles it: rotating a
/// key does not change which upstream is queried or what it returns, so it must not discard every
/// snapshot. Excluding it also keeps the secret out of a value that is hashed, cached, and reasoned
/// about in logs and tests — the fingerprint is not a secret-bearing surface and must not become
/// one. Note that a key going from absent to present DOES change the fingerprint, because
/// <see cref="ResolvedSourceConfiguration"/> resolves nothing at all for a disabled or keyless
/// source: the base URL is null in that state, so first-time configuration is caught by the URL, not
/// by the key.</para>
///
/// <para>Hashed rather than concatenated so the token component is fixed-width and carries no
/// operator-supplied text, and computed once at construction because the inputs cannot change.</para>
/// </remarks>
public sealed class ResolvedSourceSetFingerprintSource : ISourceSetFingerprintSource
{
    private readonly string _fingerprint;

    public ResolvedSourceSetFingerprintSource(ResolvedSourceConfiguration resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);

        // The unit separator matches the snapshot token's own convention, so two different
        // (url, name) pairs cannot concatenate into one fingerprint.
        var raw = $"{resolved.BaseUrl}{resolved.SourceName}";
        _fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public ValueTask<string> GetAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_fingerprint);
}
