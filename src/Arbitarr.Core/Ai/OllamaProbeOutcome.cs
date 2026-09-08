namespace Arbitarr.Core.Ai;

/// <summary>
/// #89: the result of a connectivity test against the configured Ollama instance, as a CLOSED set
/// of outcomes rather than a boolean.
///
/// <para><b>Why distinct outcomes and not "ok / failed".</b> The same reasoning
/// <see cref="Arbitarr.Core.Sources.SourceProbeOutcome"/> records for sources: unreachable, a TLS
/// failure and an answer that is not Ollama's API each have an entirely different fix (check the
/// address and that the service is running / check the certificate or the scheme / you are pointed
/// at something that is not Ollama), and an operator who cannot tell them apart learns nothing from
/// a red button.</para>
///
/// <para><b>Why FOUR here and five for a source.</b> There is deliberately no
/// <c>AuthenticationFailed</c> member: Ollama ships with no authentication and Arbitarr sends it no
/// credential, so a "key rejected" outcome could never be produced and offering it would invite an
/// operator to go hunting for a key that does not exist. The outcome set describes what can
/// actually happen against THIS backend rather than mirroring the source set for symmetry.</para>
///
/// <para><b>Why an enum and not a message string.</b> So no probe result can carry free text
/// derived from the upstream body or an exception message. That matters less here than for a source
/// (no credential is in play) but the reason it still holds is the base URL itself: an exception
/// message from <c>HttpClient</c> routinely contains the address it failed to reach, and a
/// free-text field would put whatever the operator typed — including a mistyped address pointing at
/// an unrelated host on their network — back on screen as though it were a server fact. The
/// human-readable wording is chosen by the endpoint from this enum alone.</para>
/// </summary>
public enum OllamaProbeOutcome
{
    /// <summary>Ollama answered <c>GET /api/tags</c> with its own model-list shape. The address is right.</summary>
    Ok,

    /// <summary>
    /// No usable connection: DNS failure, connection refused, no route, or the request exceeded the
    /// probe's short timeout. The address is wrong or Ollama is not running.
    /// </summary>
    Unreachable,

    /// <summary>
    /// A connection was made but the TLS handshake failed — an untrusted or expired certificate, a
    /// hostname mismatch, or https pointed at a plaintext port. Distinct from
    /// <see cref="Unreachable"/> because the address is right and the transport is the problem.
    /// </summary>
    TlsFailure,

    /// <summary>
    /// Something answered, but not Ollama's API — a non-success status, an HTML page, or a body
    /// that does not carry the <c>models</c> array <c>/api/tags</c> always returns. Usually means
    /// the base URL points at a different service or at a reverse proxy in front of one.
    /// </summary>
    UnexpectedResponse,
}
