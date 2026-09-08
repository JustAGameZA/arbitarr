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
 */
export interface OllamaConfig {
  baseUrl: string;
}

/**
 * The four outcomes of `POST /api/admin/ai/ollama/test`, mirroring the server's
 * closed `OllamaProbeOutcome` enum.
 *
 * CLOSED ON PURPOSE, on both sides. The server enum has no string field, so no
 * probe result can carry text derived from the response body, an exception
 * message, or the configured address; this union is the client half of the same
 * guarantee.
 *
 * FOUR, NOT FIVE — there is no `AuthenticationFailed` here, and adding one to
 * "match" the source probe would be wrong: Ollama ships with no authentication
 * and Arbitarr sends it no credential, so that outcome could never be produced
 * and offering it would send an operator hunting for a key that does not exist.
 */
export type OllamaProbeOutcome = 'Ok' | 'Unreachable' | 'TlsFailure' | 'UnexpectedResponse';

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
}

/** Body for `PUT /api/admin/ai/ollama`. */
export interface UpdateOllamaConfigRequest {
  baseUrl: string;
}
