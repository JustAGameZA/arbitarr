namespace Arbitarr.Data.Sources;

/// <summary>
/// A configured source's address together with the key that authenticates against it — the one
/// shape in which a stored per-source credential leaves <see cref="SourceRepository"/>.
/// </summary>
/// <remarks>
/// <para><b>THIS TYPE IS NOT A WAY TO READ THE KEY, it is the value the single reader produces.</b>
/// It is handed only to code that is about to issue a request AT <see cref="BaseUrl"/>, which is
/// the same contract <c>ReadApiKeyForUpstreamRequestAsync</c> states for the raw value: the key
/// goes into one outbound request to that source and nowhere else. Never return it to a caller,
/// never log it, never interpolate it into an error message or a probe outcome.</para>
///
/// <para><see cref="BaseUrl"/> stays a <see cref="string"/> rather than a parsed <see cref="Uri"/>
/// — the divergence from <see cref="Media.SonarrCredential"/>, and deliberate. Its consumers
/// (<c>SourceConnectivityProber</c> today, the Newznab/Torznab adapters next) build their upstream
/// URI from a base plus a per-source API path, so they compose the string rather than consume a
/// parsed absolute address, and <see cref="SourceRepository.ValidateBaseUrl"/> has already rejected
/// anything that is not an absolute http(s) URL at write time. Parsing here to re-stringify there
/// would add a shape check the row has already passed and drop a trailing-slash distinction the
/// composer cares about.</para>
/// </remarks>
/// <param name="BaseUrl">The validated absolute base URL of the configured source.</param>
/// <param name="ApiKey">The key to send to that source, and to nowhere else.</param>
public sealed record SourceCredential(string BaseUrl, string ApiKey);

/// <summary>
/// The single production reader of a stored per-source API key, and the reason
/// <see cref="SourceRepository.ReadApiKeyForUpstreamRequestAsync"/> still has exactly one caller
/// (CLAUDE.md §1; docs/standards/architecture.md's secrets-mechanisms list;
/// docs/adr/0018-one-credential-provider-per-secret-family.md).
/// </summary>
/// <remarks>
/// <para><b>WHY THIS TYPE EXISTS AT ALL.</b> Until arb-x7w8.3 the admin connectivity probe was the
/// only consumer, and "exactly one caller" was true by there being exactly one consumer: the search
/// path did not read the key at all, because <c>SourceSeeder</c> resolved the single configured
/// source's key ONCE at startup into <c>ResolvedSourceConfiguration</c>. That trick does not survive
/// N runtime-added indexers — adding an indexer cannot mean "restart to pick up the key" — so a
/// second consumer is unavoidable, and wiring it the obvious way (each site calling the reader
/// itself) would produce TWO callers, which is precisely the count the guarantee is made of. The
/// guarantee IS the call-site count: a second caller is a second place to audit, and no comment
/// saying "don't add a third" recovers what the second one cost. This type is the same answer
/// <see cref="Media.SonarrCredentialProvider"/> gave for the Sonarr key, generalised: one
/// credential-provider type per secret family, and the provider is the one caller.</para>
///
/// <para><b>WHY IT LIVES IN <c>Arbitarr.Data</c>.</b> Beside the repository whose reader it owns,
/// and reachable from every project that already references <c>Arbitarr.Data</c> — the admin
/// surface in <c>Arbitarr.Api</c> today, the source adapters next — without adding a project edge
/// anywhere. The same placement, and the same reason, as
/// <see cref="Media.SonarrCredentialProvider"/>.</para>
///
/// <para><b>READ PER CALL, NOT CAPTURED AT STARTUP.</b> A source's address and key live in the
/// database and can be changed, added or removed from the admin UI at any time, so a value captured
/// at registration would go stale — or be absent forever for a source configured after boot, which
/// is every source an operator adds. Each call costs one settings read on a path that is about to
/// make a network request anyway.</para>
/// </remarks>
public sealed class SourceCredentialProvider
{
    private readonly SourceRepository _repository;

    public SourceCredentialProvider(SourceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// The stored credential for <paramref name="sourceId"/>, or <see langword="null"/> when that
    /// source is not usably configured — no such row, no base URL, or no key.
    /// </summary>
    /// <remarks>
    /// <para>A base URL with no key is a HALF-CONFIGURED source and is reported as null rather than
    /// as a credential with an empty key. Every consumer wants the same thing from that state: the
    /// probe would send an unauthenticated request the source answers 401, recording an
    /// authentication failure against a source whose key was simply never entered, and a search
    /// would do the same against its circuit breaker. Not asking is cheaper and honest about what
    /// is missing.</para>
    ///
    /// <para>This is the ONLY production call site of
    /// <see cref="SourceRepository.ReadApiKeyForUpstreamRequestAsync"/>. Consumers receive a
    /// <see cref="SourceCredential"/> they may send upstream; none of them reads the key from the
    /// repository, and none of them should be given a way to.</para>
    /// </remarks>
    public async Task<SourceCredential?> GetAsync(long sourceId, CancellationToken cancellationToken)
    {
        var source = await _repository.GetAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null || string.IsNullOrWhiteSpace(source.BaseUrl))
        {
            return null;
        }

        var apiKey = await _repository.ReadApiKeyForUpstreamRequestAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new SourceCredential(source.BaseUrl, apiKey);
    }
}
