namespace Arbitarr.Core.Sources;

/// <summary>
/// The result of a §3.3 connectivity test against a configured source, as a CLOSED set of
/// outcomes rather than a boolean.
///
/// <para><b>Why five outcomes and not "ok / failed".</b> The plan is explicit that a single red
/// "failed" makes the test button decorative: unreachable, a TLS failure, a rejected key and a
/// response that is not the expected API each have an entirely different fix (check the address /
/// check the certificate or scheme / rotate the key / you are pointed at the wrong service), and
/// an operator who cannot tell them apart learns nothing from a failure. Each member below is
/// therefore a distinct thing an operator would do next.</para>
///
/// <para><b>Why an enum and not a message string.</b> The probe handles a secret — it must send
/// the API key upstream to test it at all (see
/// <c>SourceRepository.ReadApiKeyForUpstreamRequestAsync</c>). Making the outcome a closed enum
/// means no probe result can carry free text derived from the key, the upstream body, or an
/// exception message, so §3.3's "never echo the submitted key back, in its response or its error
/// text" holds by construction rather than by remembering to sanitise each path. The human-readable
/// wording is chosen by the endpoint from this enum alone.</para>
/// </summary>
public enum SourceProbeOutcome
{
    /// <summary>The source answered, and the response was the expected API shape. The key works.</summary>
    Ok,

    /// <summary>
    /// No usable connection: DNS failure, connection refused, no route, or the request exceeded the
    /// probe's short timeout. The address is wrong or the service is down.
    /// </summary>
    Unreachable,

    /// <summary>
    /// A connection was made but the TLS handshake failed — an untrusted or expired certificate, a
    /// hostname mismatch, or https pointed at a plaintext port. Distinct from
    /// <see cref="Unreachable"/> because the address is right and the transport is the problem.
    /// </summary>
    TlsFailure,

    /// <summary>
    /// The source answered and rejected our credentials (401/403). The address is right, the
    /// service is right, the key is wrong or lacks permission.
    /// </summary>
    AuthenticationFailed,

    /// <summary>
    /// The source answered, did not reject the key, but the response was not the API expected — a
    /// 5xx, some other non-success status, an HTML error/login page, or a body that does not parse
    /// as the source's API. Usually means the base URL points at the wrong service or a reverse
    /// proxy in front of it.
    /// </summary>
    UnexpectedResponse,
}
