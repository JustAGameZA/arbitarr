/**
 * #89 — the wire types for the admin AI backend surface.
 *
 * These live beside the section rather than in `api/types.ts` for the same
 * reason the Sources section keeps its own: the whole feature is one
 * self-contained subdirectory mounted into Settings by a single line, and
 * keeping the types local keeps the shared file out of the rebase.
 */

/**
 * AdminAiEndpoints.cs — OllamaConfigResponse.
 *
 * NOTE WHAT IS PRESENT: unlike `SourceSummary` and `NotificationConfig`, which
 * report a stored address as a bare `has…` boolean, this one carries the value
 * itself. That is deliberate and not an oversight to be tidied into the
 * write-only idiom: Ollama has no authentication, the URL carries no token, and
 * the server rejects a URL containing credentials precisely so this stays true.
 * Making the operator retype an address they cannot see, to defend a secret that
 * does not exist, would be cargo-culting the pattern rather than applying it.
 *
 * `model` (#112) is present for the same reason and is likewise NOT a secret: it
 * is the name of a file the operator pulled, and the whole point of #112 is that
 * it stops being invisible. Before it, the model lived only in server config and
 * an operator could get a green "Connected" from an instance that had never
 * pulled it.
 */
export interface OllamaConfig {
  baseUrl: string;
  model: string;
}

/**
 * The outcomes of `POST /api/admin/ai/ollama/test`, mirroring the server's closed
 * `OllamaProbeOutcome` enum.
 *
 * CLOSED ON PURPOSE, on both sides. The server enum has no string field, so the
 * probe's WORDING can never be text derived from the response body, an exception
 * message, or the configured address; this union is the client half of the same
 * guarantee. (arb-1rr adds one field of upstream text — `chatError` below — but
 * beside the outcome, never inside it, exactly as `models` already was.)
 *
 * NO `AuthenticationFailed` — and adding one to "match" the source probe would be
 * wrong: Ollama ships with no authentication and Arbitarr sends it no credential,
 * so that outcome could never be produced and offering it would send an operator
 * hunting for a key that does not exist.
 *
 * arb-1rr added the last two. The probe used to check `GET /api/tags` alone, so it
 * reported success whenever the ADDRESS was right — which is how it could show
 * "Connected successfully" while every real classification failed 400. It now also
 * posts a `/api/chat` request: `ChatRejected` is "the address is right and the
 * classification request was refused", and `OkNoModelConfigured` is "the address
 * is right and there was no model to test with", which is deliberately NOT a
 * success.
 */
export type OllamaProbeOutcome =
  | 'Ok'
  | 'Unreachable'
  | 'TlsFailure'
  | 'UnexpectedResponse'
  | 'ChatRejected'
  | 'OkNoModelConfigured';

/** AdminAiEndpoints.cs — OllamaTestResponse. */
export interface OllamaTestResult {
  success: boolean;
  /** One of `OllamaProbeOutcome`, as a stable string the UI switches on. */
  outcome: string;
  /**
   * The server's fixed wording for the outcome. Rendered as-is alongside our own
   * short label — it is chosen from the enum alone and is never derived from the
   * backend's response, an exception message, or the configured address.
   */
  message: string;
  /**
   * #112: the model names the instance reported, on an `Ok` outcome only — and
   * possibly empty even then, because an instance that has pulled nothing is
   * perfectly healthy.
   *
   * A SEPARATE FIELD FROM `message`, and that separation is why the outcome union
   * above is still six members. Carrying the names inside the wording would have
   * meant a server message derived from the upstream body, which is exactly the
   * shape the closed enum exists to prevent. These are rendered only as options in
   * a picker, never as prose.
   */
  models: string[];
  /**
   * arb-1rr: on a `ChatRejected` outcome, the reason Ollama gave for refusing the
   * test classification request; empty string for every other outcome (present,
   * never absent, so "no rejection" is not ambiguous with "field missing").
   *
   * This is the ONE piece of upstream text on this response, and it is admitted
   * because "Ollama rejected the request" without the reason is the exact
   * non-answer that made the old button untrustworthy. It rides in its own field
   * rather than inside `message` for the same reason `models` does — the wording
   * stays derived from the closed outcome alone.
   *
   * Already scrubbed server-side (`SanitizedErrorDescription`): no host, address
   * or credential survives into it. Render it as-is; do not parse it.
   */
  chatError: string;
}

/**
 * Body for `PUT /api/admin/ai/ollama`.
 *
 * `model` is OPTIONAL because the server made it optional: #89 shipped this route
 * taking `baseUrl` alone, and omitting the field means "leave the stored model
 * alone" rather than "clear it". This page always sends both, but the type states
 * the server's contract rather than this page's habit.
 */
export interface UpdateOllamaConfigRequest {
  baseUrl: string;
  model?: string;
}
