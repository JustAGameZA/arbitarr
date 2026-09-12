using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Data.Media;

/// <summary>
/// A configured Radarr instance's address together with the key that authenticates against it —
/// the one shape in which the stored credential leaves <see cref="RadarrInstanceRepository"/>.
/// </summary>
/// <remarks>
/// <para><b>THIS TYPE IS NOT A WAY TO READ THE KEY, it is the value the single reader produces.</b>
/// It is handed only to code that is about to issue a request AT <see cref="BaseUrl"/>, which is the
/// same contract <see cref="RadarrInstanceRepository.ReadApiKeyForUpstreamRequestAsync"/> states for
/// the raw value: the key goes into one outbound request to the configured Radarr and nowhere else.
/// Never return it to a caller, never log it, never interpolate it into an error message or a probe
/// outcome.</para>
///
/// <para><see cref="BaseUrl"/> is a parsed <see cref="Uri"/> rather than a string because consumers
/// need one and none should re-parse: <see cref="RadarrCredentialProvider"/> has already run
/// <see cref="RadarrInstanceRepository.ValidateBaseUrl"/>'s shape check by successfully parsing it,
/// so a consumer holding this record cannot be looking at an address that was never validated.</para>
/// </remarks>
/// <param name="BaseUrl">The validated absolute base URL of the configured Radarr instance.</param>
/// <param name="ApiKey">The key to send to that instance, and to nowhere else.</param>
public sealed record RadarrCredential(Uri BaseUrl, string ApiKey)
{
    /// <summary>
    /// <b>OVERRIDDEN BECAUSE THE SYNTHESISED ONE PRINTS THE KEY.</b> A positional record's
    /// compiler-generated <c>ToString</c> renders every positional member, so the default here would
    /// produce <c>RadarrCredential { BaseUrl = ..., ApiKey = the-actual-key }</c> — and this type is
    /// handed to code that is about to make a network request, which is exactly the code most likely
    /// to end up in a log line, an exception message, or a structured-logging argument that formats
    /// its operands.
    ///
    /// <para><b>NEITHER EXISTING LAYER WOULD CATCH IT</b> (CLAUDE.md §1). <c>IHttpClientFactory</c>'s
    /// redaction collapses a URI's QUERY STRING and nothing else, and <c>LogMessageCleanser</c> scrubs
    /// credentials in query strings but not in URL paths. A bare <c>ApiKey = value</c> inside a
    /// record's string form is in neither — it is not a URI at all — so the value would reach the log
    /// store verbatim. Redacting at the source is the only layer that covers this shape.</para>
    ///
    /// <para>The base URL is still rendered in full: it is deliberately not a credential, and
    /// <see cref="RadarrInstanceRepository.ValidateBaseUrl"/> rejecting userinfo is what keeps that
    /// true. Printing it is what makes this override useful for diagnostics rather than merely
    /// silent.</para>
    ///
    /// <para>The marker is <see cref="CredentialPatterns.Replacement"/> rather than a literal, so this
    /// redaction and the cleanser's can never drift apart into two spellings a search would have to
    /// know about separately.</para>
    /// </summary>
    public override string ToString() =>
        $"{nameof(RadarrCredential)} {{ {nameof(BaseUrl)} = {BaseUrl}, {nameof(ApiKey)} = {CredentialPatterns.Replacement} }}";
}

/// <summary>
/// The single production reader of the stored Radarr API key, and the reason
/// <see cref="RadarrInstanceRepository.ReadApiKeyForUpstreamRequestAsync"/> has exactly one caller
/// (CLAUDE.md §1; docs/standards/architecture.md's secrets-mechanisms list).
/// </summary>
/// <remarks>
/// <para><b>WHY THIS TYPE EXISTS EVEN THOUGH THERE IS CURRENTLY ONE CONSUMER.</b> Sonarr's
/// equivalent (<see cref="SonarrCredentialProvider"/>) was extracted only once a SECOND consumer
/// appeared, and its doc records what that cost: the obvious wiring — each site calling the reader
/// itself — produced two callers, which is precisely the count the guarantee is made of. Radarr's
/// provider is introduced up front so the same lesson is not re-learned. The admin connectivity probe
/// is the first consumer; the queue and movie-library readers that follow (arb-arrq beads 3 and 4)
/// take a <see cref="RadarrCredential"/> from here rather than reaching for the repository's reader,
/// and the reader's call-site count stays at one throughout.</para>
///
/// <para><b>WHY IT LIVES IN <c>Arbitarr.Data</c>.</b> The same reason
/// <see cref="SonarrCredentialProvider"/> does: it must be reachable from <c>Arbitarr.Api</c> and
/// from the client projects without either referencing the other — <c>Arbitarr.Api</c> must never
/// reference <c>Arbitarr.Media</c> (ADR 0001: the composition root is the only place that knows
/// which implementation is in play). Every project already references <c>Arbitarr.Data</c>, and this
/// type's whole job is reading a stored value, so placing it beside the repository whose reader it
/// owns adds no project edge anywhere.</para>
///
/// <para><b>READ PER CALL, NOT CAPTURED AT STARTUP.</b> The address and key live in the database and
/// can be changed from the admin UI at any time, so a value captured at registration would go stale —
/// or be absent forever for anyone who configures Radarr after boot, which is everyone on a first
/// run. Each call costs one settings read on a path that is about to make a network request
/// anyway.</para>
/// </remarks>
public sealed class RadarrCredentialProvider
{
    private readonly RadarrInstanceRepository _repository;

    public RadarrCredentialProvider(RadarrInstanceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// The stored Radarr credential, or <see langword="null"/> when the instance is not usably
    /// configured — no address, no key, or an address that does not parse.
    /// </summary>
    /// <remarks>
    /// <para>A base URL with no key is a HALF-CONFIGURED instance and is reported as null rather than
    /// as a credential with an empty key. Consumers want the same thing from that state: the probe
    /// would send an unauthenticated request that Radarr answers 401, recording a failure against a
    /// service that is not actually broken, and a library reader would render an error where "not
    /// configured" is the truthful answer. Not asking is cheaper and honest about what is
    /// missing.</para>
    ///
    /// <para>This is the ONLY production call site of
    /// <see cref="RadarrInstanceRepository.ReadApiKeyForUpstreamRequestAsync"/>. Consumers receive a
    /// <see cref="RadarrCredential"/> they may send upstream; none of them reads the key from the
    /// repository, and none of them should be given a way to.</para>
    /// </remarks>
    public async Task<RadarrCredential?> GetAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await _repository.GetBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            return null;
        }

        var apiKey = await _repository.ReadApiKeyForUpstreamRequestAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new RadarrCredential(parsedBaseUrl, apiKey);
    }
}
