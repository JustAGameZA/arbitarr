/**
 * #53 stage 53d — the wire types for the admin sources surface.
 *
 * These live beside the section rather than in `api/types.ts` because the whole
 * Sources feature is one self-contained subdirectory mounted into Settings by a
 * single line, and three sibling sections were landing into `Settings.tsx` at
 * the same time. Keeping the types local keeps the shared file out of the
 * rebase.
 */

/**
 * AdminSourceEndpoints.cs — SourceResponse.
 *
 * NOTE WHAT IS ABSENT: there is no field for the API key, and deliberately no
 * nullable "key" property. That is not an oversight to be tidied up later — the
 * server's `SourceResponse` has no field capable of holding a key and
 * `ToResponseAsync` is its single projection, which is what makes write-only
 * structurally enforced rather than maintained by care (§3.1/AC2). `hasApiKey`
 * is the entire read surface for the secret, and mirroring that shape here is
 * what stops a component from reaching for a value that does not exist.
 */
export interface SourceSummary {
  id: number;
  kind: string;
  displayName: string;
  baseUrl: string;
  enabled: boolean;
  hasApiKey: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * The five outcomes of `POST /api/admin/sources/{id}/test`, mirroring the
 * server's closed `SourceProbeOutcome` enum.
 *
 * CLOSED ON PURPOSE, on both sides. The server enum has no string field so that
 * no probe failure can carry key-derived text upstream into the response; this
 * union is the client half of the same guarantee. §3.3/AC4 requires the five to
 * be reported DISTINCTLY, because unreachable / TLS / auth / unexpected-shape
 * have entirely different fixes and a single red "failed" makes the button
 * decorative.
 */
export type SourceProbeOutcome =
  | 'Ok'
  | 'Unreachable'
  | 'TlsFailure'
  | 'AuthenticationFailed'
  | 'UnexpectedResponse';

/** AdminSourceEndpoints.cs — SourceTestResponse. */
export interface SourceTestResult {
  success: boolean;
  /** One of `SourceProbeOutcome`, as a stable string the UI switches on. */
  outcome: string;
  /**
   * The server's fixed wording for the outcome. Rendered as-is alongside our own
   * short label — it is chosen from the enum alone and is never derived from the
   * upstream response, an exception message, or the submitted key.
   */
  message: string;
}

/** Body for `POST /api/admin/sources`. `apiKey` is write-only and optional. */
export interface CreateSourceRequest {
  kind: string;
  displayName: string;
  baseUrl: string;
  enabled: boolean;
  /** Omitted entirely when the operator typed nothing. */
  apiKey?: string;
}

/**
 * Body for `PUT /api/admin/sources/{id}`.
 *
 * THE UPDATE CONTRACT HAS THREE DIFFERENT NULL POLICIES IN ONE SIGNATURE, and
 * only one of them is the forgiving one everybody learns first:
 *
 * - `apiKey` — omitting it means LEAVE THE STORED KEY ALONE. It never clears.
 *   Getting this backwards silently wipes a working source's credential on an
 *   unrelated edit, so this field is added to the body only when the operator
 *   actually typed a replacement.
 * - `kind` / `displayName` / `baseUrl` — omitting these makes them
 *   `string.Empty` server-side, which is then REJECTED as a validation failure
 *   (`AdminSourceEndpoints.cs:181-183`). They are therefore required here and
 *   sent on EVERY edit, not just when changed.
 * - `enabled` — preserved via `?? existing.Enabled` server-side, but sent
 *   always for the same reason: an edit form that omits it is one refactor away
 *   from meaning something else.
 *
 * The `apiKey` precedent actively invites the wrong generalisation to the other
 * four fields. It does not apply to them.
 */
export interface UpdateSourceRequest {
  kind: string;
  displayName: string;
  baseUrl: string;
  enabled: boolean;
  /** Present ONLY when replacing the stored key. Absent means "leave it alone". */
  apiKey?: string;
}
