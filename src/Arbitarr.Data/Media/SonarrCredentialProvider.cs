using Arbitarr.Core.Diagnostics;

namespace Arbitarr.Data.Media;

/// <summary>
/// A configured Sonarr instance's address together with the key that authenticates against it —
/// the one shape in which the stored credential leaves <see cref="ArrInstanceRepository"/>.
/// </summary>
/// <remarks>
/// <para><b>THIS TYPE IS NOT A WAY TO READ THE KEY, it is the value the single reader produces.</b>
/// It is handed only to code that is about to issue a request AT <see cref="BaseUrl"/>, which is
/// the same contract <c>ReadApiKeyForUpstreamRequestAsync</c> states for the raw value: the key
/// goes into one outbound request to the configured Sonarr and nowhere else. Never return it to a
/// caller, never log it, never interpolate it into an error message or a probe outcome.</para>
///
/// <para><see cref="BaseUrl"/> is a parsed <see cref="Uri"/> rather than a string because both
/// consumers need one and neither should re-parse: <see cref="SonarrCredentialProvider"/> has
/// already run <see cref="ArrInstanceRepository.ValidateBaseUrl"/>'s shape check by successfully
/// parsing it, so a consumer holding this record cannot be looking at an address that was never
/// validated.</para>
/// </remarks>
/// <param name="BaseUrl">The validated absolute base URL of the configured Sonarr instance.</param>
/// <param name="ApiKey">The key to send to that instance, and to nowhere else.</param>
public sealed record SonarrCredential(Uri BaseUrl, string ApiKey)
{
    /// <summary>
    /// Renders the address in full and the key as <see cref="CredentialPatterns.Replacement"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>OVERRIDDEN BECAUSE THE SYNTHESISED ONE PRINTS THE KEY.</b> A positional record's
    /// compiler-generated <c>ToString</c> renders every member by name and value, so the default here
    /// produced <c>SonarrCredential { BaseUrl = …, ApiKey = the-actual-key }</c> — and this type is
    /// handed to code that is about to make a network request, which is exactly the code most likely
    /// to reach a log line, an exception message, or a structured-logging argument that formats its
    /// operands. An interpolation is how a credential reaches a log line by accident:
    /// <c>$"probe failed for {credential}"</c> compiles, reads as harmless, and is a durable
    /// disclosure.</para>
    ///
    /// <para><b>WHY THE CLEANSER IS NOT ENOUGH, STATED PRECISELY</b> (CLAUDE.md §1).
    /// <c>IHttpClientFactory</c>'s redaction collapses an outbound request URI's QUERY STRING and
    /// nothing else, so it covers this shape not at all. <see cref="Logging.LogMessageCleanser"/> is
    /// the one worth being exact about: its <c>NamedCredential</c> arm matches a credential-shaped
    /// NAME followed by <c>:</c> or <c>=</c> anywhere in the text, so a rendered
    /// <c>ApiKey = …</c> IS scrubbed on its way into the log store. Do not conclude from that that
    /// this override is redundant — <b>the cleanser runs only in the LOG SINK.</b> A credential
    /// interpolated into an exception message, into console output, or into any surface that is not
    /// the SQLite sink never meets it, and <c>ToString</c> is where every one of those shapes is
    /// formed. Redacting at the source is the only layer that covers all of them.</para>
    ///
    /// <para>That the sink also covers the <c>ApiKey</c> spelling is incidental rather than
    /// structural: the same arm does NOT match <c>PlaintextKey = …</c>, which is why
    /// <c>Security.CreatedApiKey</c>'s override is load-bearing outright. Relying on a denylist to
    /// know a property's name is the arrangement this override exists to stop depending on.</para>
    ///
    /// <para>The base URL is still rendered in full: it is deliberately not a credential, and
    /// <see cref="ArrInstanceRepository.ValidateBaseUrl"/> rejecting userinfo is what keeps that true.
    /// Printing it is what makes this override useful for diagnostics rather than merely silent.</para>
    ///
    /// <para>The marker is <see cref="CredentialPatterns.Replacement"/> rather than a literal, so this
    /// redaction and the cleanser's can never drift apart into two spellings a search would have to
    /// know about separately.</para>
    /// </remarks>
    public override string ToString() =>
        $"{nameof(SonarrCredential)} {{ {nameof(BaseUrl)} = {BaseUrl}, {nameof(ApiKey)} = {CredentialPatterns.Replacement} }}";
}

/// <summary>
/// The single production reader of the stored Sonarr API key, and the reason
/// <c>ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync</c> still has exactly one caller
/// (CLAUDE.md §1; docs/standards/architecture.md's secrets-mechanisms list).
/// </summary>
/// <remarks>
/// <para><b>WHY THIS TYPE EXISTS AT ALL.</b> Two production paths need an authenticated request
/// against the configured Sonarr: the admin connectivity probe
/// (<c>Arbitarr.Api.Admin.AdminArrEndpoints</c>) and the search path's identity resolver
/// (<c>Arbitarr.Media.Providers.SeriesTitleResolver</c>). Before arb-u1c the probe was the only
/// one, and "exactly one caller" was true by there being exactly one consumer. Adding the second
/// consumer made the obvious wiring — each site calling the reader itself — produce TWO callers,
/// which is precisely the count the guarantee is made of. The guarantee IS the call-site count: a
/// second caller is a second place to audit, and no comment saying "don't add a third" recovers
/// what the second one cost.</para>
///
/// <para><b>WHY IT LIVES IN <c>Arbitarr.Data</c>.</b> It has to be reachable from both
/// <c>Arbitarr.Api</c> and <c>Arbitarr.Media</c> without either one referencing the other —
/// <c>Arbitarr.Api</c> must never reference <c>Arbitarr.Media</c> (ADR 0001: the composition root
/// is the only place that knows which identity implementation is in play). Both projects already
/// reference <c>Arbitarr.Data</c>, and this type's whole job is reading a stored value, so placing
/// it beside the repository whose reader it owns adds no project edge anywhere.</para>
///
/// <para><b>READ PER CALL, NOT CAPTURED AT STARTUP.</b> The address and key live in the database
/// and can be changed from the admin UI at any time, so a value captured at registration would go
/// stale — or be absent forever for anyone who configures Sonarr after boot, which is everyone on
/// a first run. Each call costs one settings read on a path that is about to make a network
/// request anyway.</para>
/// </remarks>
public sealed class SonarrCredentialProvider
{
    private readonly ArrInstanceRepository _repository;

    public SonarrCredentialProvider(ArrInstanceRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>
    /// The stored Sonarr credential, or <see langword="null"/> when the instance is not usably
    /// configured — no address, no key, or an address that does not parse.
    /// </summary>
    /// <remarks>
    /// <para>A base URL with no key is a HALF-CONFIGURED instance and is reported as null rather
    /// than as a credential with an empty key. Both consumers want the same thing from that state:
    /// the probe would send an unauthenticated request that Sonarr answers 401, recording a
    /// failure against a service that is not actually broken, and the resolver would do the same
    /// against its circuit breaker. Not asking is cheaper and honest about what is missing.</para>
    ///
    /// <para>This is the ONLY production call site of
    /// <see cref="ArrInstanceRepository.ReadApiKeyForUpstreamRequestAsync"/>. Consumers receive a
    /// <see cref="SonarrCredential"/> they may send upstream; none of them reads the key from the
    /// repository, and none of them should be given a way to.</para>
    /// </remarks>
    public async Task<SonarrCredential?> GetAsync(CancellationToken cancellationToken)
    {
        var baseUrl = await _repository.GetBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            return null;
        }

        var apiKey = await _repository.ReadApiKeyForUpstreamRequestAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new SonarrCredential(parsedBaseUrl, apiKey);
    }
}
