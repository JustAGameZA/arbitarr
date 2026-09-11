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
/// <para><b>Why SIX here and five for a source.</b> There is deliberately no
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

    /// <summary>
    /// arb-1rr: the address is right and Ollama answered the model list, but it REJECTED the
    /// classification request itself — a non-2xx from <c>/api/chat</c>. The connectivity half is
    /// healthy and the working half is not, which is precisely the state that used to report a
    /// green "Connected successfully" while every classification failed.
    ///
    /// <para>Distinct from <see cref="UnexpectedResponse"/> because the fix is different: nothing
    /// is wrong with the address, so sending the operator to check it wastes their time. The reason
    /// lives in <see cref="OllamaProbeResult.ChatError"/>, which is why this outcome — unlike the
    /// five above — is reported beside a message rather than alone.</para>
    /// </summary>
    ChatRejected,

    /// <summary>
    /// arb-1rr: <c>/api/tags</c> answered, but no model is configured, so the <c>/api/chat</c> half
    /// of the probe was not attempted. Reported as its own outcome rather than folded into
    /// <see cref="Ok"/> so the button cannot claim more than it tested: the address is confirmed,
    /// classification is not.
    /// </summary>
    OkNoModelConfigured,
}

/// <summary>
/// #112: what a probe learned — the closed <see cref="OllamaProbeOutcome"/> plus, on
/// <see cref="OllamaProbeOutcome.Ok"/>, the model NAMES the instance reported.
///
/// <para><b>The names are a SEPARATE FIELD, and that is the whole design.</b> #89's guarantee is
/// that no free text derived from the upstream body reaches the operator-facing wording, and that
/// guarantee is enforced by <see cref="OllamaProbeOutcome"/> having no string member at all. Adding
/// a fifth outcome, or a message field, to carry the model list would have destroyed it. Instead the
/// enum is untouched and the names travel beside it, so the endpoint still derives its sentence from
/// the enum alone and the names are only ever rendered as list items the operator picks from.</para>
///
/// <para><b>Empty is not a failure.</b> A healthy instance that has pulled nothing answers
/// <c>{"models":[]}</c>, which is <see cref="OllamaProbeOutcome.Ok"/> with no names — see
/// <c>OllamaConnectivityProber.LooksLikeTagsResponse</c>. Every non-Ok outcome carries an empty
/// list, because there was no model list to read.</para>
/// </summary>
/// <param name="Outcome">The classification. Closed, and the only thing the wording is derived from.</param>
/// <param name="Models">
/// The <c>name</c> of each entry in <c>/api/tags</c>, in the order Ollama listed them. Empty for
/// every outcome other than <see cref="OllamaProbeOutcome.Ok"/>, and possibly empty for that one.
/// </param>
/// <param name="ChatError">
/// arb-1rr: on <see cref="OllamaProbeOutcome.ChatRejected"/>, the SCRUBBED reason Ollama gave for
/// rejecting the classification request; empty for every other outcome.
///
/// <para><b>This is upstream text, and it is the deliberate exception to the rule above.</b> The
/// design note on <see cref="OllamaProbeResult"/> says no free text derived from the upstream body
/// reaches the operator — that rule still holds for the WORDING, which is still derived from the
/// closed enum alone. This field is the same concession <c>Models</c> made and for the same reason:
/// it travels BESIDE the enum, never inside it, and is rendered as its own detail line rather than
/// interpolated into the sentence. It is admitted because "Ollama rejected the request" without the
/// reason is the exact non-answer this bead was filed about.</para>
///
/// <para>Already passed through <c>SanitizedErrorDescription</c> before it is stored here, so it
/// carries no host, address or credential — it must be, because this value reaches an operator-facing
/// response. The prober is the only writer, and it never assigns a raw body.</para>
/// </param>
public sealed record OllamaProbeResult(
    OllamaProbeOutcome Outcome,
    IReadOnlyList<string> Models,
    string ChatError = "")
{
    /// <summary>An outcome with no model list — every failure, and the shape callers build for one.</summary>
    public static OllamaProbeResult From(OllamaProbeOutcome outcome) => new(outcome, []);
}
