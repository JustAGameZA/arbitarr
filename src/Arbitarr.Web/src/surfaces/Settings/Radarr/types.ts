/**
 * arb-6l9b.2 — the wire types for the admin Radarr surface.
 *
 * These live beside the section rather than in `api/types.ts` for the same
 * reason the Sonarr, Sources and AI sections keep their own: the whole feature
 * is one self-contained subdirectory mounted into Settings by a single line, and
 * keeping the types local keeps the shared file out of the rebase.
 *
 * DUPLICATED FROM `Sonarr/types.ts` RATHER THAN SHARED WITH IT, deliberately,
 * mirroring the server's own decision (arb-arrq D3): `RadarrConfigResponse` is a
 * separate record from `ArrConfigResponse` even though the two shapes are
 * identical today, so the two wire contracts stay free to diverge. Hoisting
 * these into a shared `ArrConfig` would couple them for no gain beyond a few
 * saved lines, and would make a Radarr-only field a breaking change for Sonarr.
 */

/**
 * AdminRadarrEndpoints.cs — RadarrConfigResponse.
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
export interface RadarrConfig {
  /** The stored Radarr base URL, or null when nothing is configured yet. */
  baseUrl: string | null;
  /** Whether a key is stored. Never the key itself — there is no read path for it. */
  hasApiKey: boolean;
}

/**
 * The five outcomes of `POST /api/admin/arr/radarr/test`, mirroring the server's
 * closed `SourceProbeOutcome` enum.
 *
 * CLOSED ON PURPOSE, on both sides. The server enum has no string field, so no
 * probe result can carry text derived from the response body, an exception
 * message, the configured address, or the API key; this union is the client half
 * of the same guarantee.
 *
 * FIVE, NOT FOUR — `AuthenticationFailed` IS here, unlike the AI section's union,
 * and that difference is substantive rather than cosmetic. Radarr carries a key
 * and a wrong key is the single most likely misconfiguration this button exists
 * to catch; Ollama has no authentication at all, so the same member there could
 * never be produced.
 */
export type RadarrProbeOutcome =
  | 'Ok'
  | 'Unreachable'
  | 'TlsFailure'
  | 'AuthenticationFailed'
  | 'UnexpectedResponse';

/** AdminRadarrEndpoints.cs — RadarrTestResponse. */
export interface RadarrTestResult {
  success: boolean;
  /** One of `RadarrProbeOutcome`, as a stable string the UI switches on. */
  outcome: string;
  /**
   * The server's fixed wording for the outcome. Rendered as-is alongside our own
   * short label — it is chosen from the enum alone and is never derived from
   * Radarr's response, an exception message, the configured address, or the key.
   */
  message: string;
}

/**
 * Body for `PUT /api/admin/arr/radarr`.
 *
 * `apiKey` is OPTIONAL because the server made it optional, and omitting it means
 * "LEAVE THE STORED KEY ALONE" rather than "clear it". That is the source-API-key
 * contract exactly: the client never had the stored value, so it cannot
 * read-and-reapply it, and an edit that changes only the address must not destroy
 * the key as a side effect. There is deliberately NO key-only clear route (ADR 0010:
 * secrets are never readable, omission never clears, and a secret is cleared only by
 * deleting the thing that owns it) -- `AdminRadarrEndpointsTests` asserts
 * `DELETE /api/admin/arr/radarr/key` does not exist. Clearing the key means
 * unconfiguring the whole instance via `DELETE /api/admin/arr/radarr`, which removes
 * both the address and the key together.
 */
export interface UpdateRadarrConfigRequest {
  baseUrl: string;
  apiKey?: string;
}
