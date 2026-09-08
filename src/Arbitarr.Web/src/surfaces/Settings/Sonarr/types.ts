/**
 * arb-u1c — the wire types for the admin Sonarr surface.
 *
 * These live beside the section rather than in `api/types.ts` for the same
 * reason the Sources and AI sections keep their own: the whole feature is one
 * self-contained subdirectory mounted into Settings by a single line, and
 * keeping the types local keeps the shared file out of the rebase.
 */

/**
 * AdminArrEndpoints.cs — ArrConfigResponse.
 *
 * THE TWO FIELDS ARE SHAPED DIFFERENTLY ON PURPOSE, and neither should be
 * "tidied" into matching the other.
 *
 * `baseUrl` carries the VALUE, like `OllamaConfig` and unlike `SourceSummary`,
 * because it is not a credential — the server rejects a URL containing userinfo
 * precisely so that stays true. An operator can therefore see and correct the
 * address rather than retyping it blind.
 *
 * `hasApiKey` is a BOOLEAN, like `SourceSummary` and unlike the AI section
 * (which has no key at all), because it IS a credential. The write-only contract
 * means there is no read path for it anywhere on the server, so there is nothing
 * this type could carry even if it wanted to.
 */
export interface ArrConfig {
  /** The stored Sonarr base URL, or null when nothing is configured yet. */
  baseUrl: string | null;
  /** Whether a key is stored. Never the key itself — there is no read path for it. */
  hasApiKey: boolean;
}

/**
 * The five outcomes of `POST /api/admin/arr/sonarr/test`, mirroring the server's
 * closed `SourceProbeOutcome` enum.
 *
 * CLOSED ON PURPOSE, on both sides. The server enum has no string field, so no
 * probe result can carry text derived from the response body, an exception
 * message, the configured address, or the API key; this union is the client half
 * of the same guarantee.
 *
 * FIVE, NOT FOUR — `AuthenticationFailed` IS here, unlike the AI section's union,
 * and that difference is substantive rather than cosmetic. Sonarr carries a key
 * and a wrong key is the single most likely misconfiguration this button exists
 * to catch; Ollama has no authentication at all, so the same member there could
 * never be produced.
 */
export type ArrProbeOutcome =
  | 'Ok'
  | 'Unreachable'
  | 'TlsFailure'
  | 'AuthenticationFailed'
  | 'UnexpectedResponse';

/** AdminArrEndpoints.cs — ArrTestResponse. */
export interface ArrTestResult {
  success: boolean;
  /** One of `ArrProbeOutcome`, as a stable string the UI switches on. */
  outcome: string;
  /**
   * The server's fixed wording for the outcome. Rendered as-is alongside our own
   * short label — it is chosen from the enum alone and is never derived from
   * Sonarr's response, an exception message, the configured address, or the key.
   */
  message: string;
}

/**
 * Body for `PUT /api/admin/arr/sonarr`.
 *
 * `apiKey` is OPTIONAL because the server made it optional, and omitting it means
 * "LEAVE THE STORED KEY ALONE" rather than "clear it". That is the source-API-key
 * contract exactly: the client never had the stored value, so it cannot
 * read-and-reapply it, and an edit that changes only the address must not destroy
 * the key as a side effect. Clearing is its own route
 * (`DELETE /api/admin/arr/sonarr/key`) for the same reason.
 */
export interface UpdateArrConfigRequest {
  baseUrl: string;
  apiKey?: string;
}
