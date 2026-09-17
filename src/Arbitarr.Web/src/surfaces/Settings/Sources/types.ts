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
  /**
   * `'Proxy'` or `'Redirect'` — how a download from this source is served.
   *
   * A STRING, NOT A UNION, AND DELIBERATELY SO. The server matches this value by
   * exact ordinal name at the repository boundary and refuses everything else. A
   * TypeScript union here would be a second, weaker copy of that rule — enforced
   * only over code the compiler can see, while the value actually arrives over
   * the wire — and it would make an unrecognised value from an older or newer
   * server a type error at the boundary rather than something the UI can render.
   * `ACCESS_MODES` in `Sources.tsx` is the list the form offers; this is what the
   * server said.
   */
  nzbAccessMode: string;
  /** arb-x7w8.11 — one of `SourceRuntimeState`, as a stable enum name. */
  runtimeState: string;
  /**
   * When a transient hold-off expires, or null.
   *
   * NON-NULL DOES NOT MEAN "BACKING OFF". A value in the PAST means the hold-off
   * already elapsed; the row keeps it because the level it was reached at is
   * still live information until the next outcome resolves it. `runtimeState` —
   * and only `runtimeState` — says whether anything is being held off now.
   * Rendering any non-null value as "backing off" shows recovered sources as
   * broken indefinitely.
   */
  disabledUntil: string | null;
  /** How far transient escalation has climbed. Zero means not escalated. */
  disabledLevel: number;
  /**
   * The last observed call outcome as a server enum name, or null before any
   * outcome was recorded. An enum name and never upstream text — the server
   * record has no field able to carry one, which is what makes the no-secret
   * property structural rather than maintained by care.
   */
  lastOutcome: string | null;
  /** Query hits spent in the current rolling window. Read against `queryLimit`. */
  queriesUsed: number;
  /** Grab hits spent in the current rolling window. Read against `grabLimit`. */
  grabsUsed: number;
  /**
   * The query cap, or null for UNLIMITED.
   *
   * NULL IS UNLIMITED AND IS NOT ZERO, on this side exactly as in the column. A
   * source whose limit an operator never configured must render "unlimited" —
   * never "0 of 0", never a percentage, and never via `queryLimit ?? 0`, which
   * is the spelling that silently reports every unconfigured indexer as spent.
   */
  queryLimit: number | null;
  /** The grab cap, with the same null-is-unlimited semantics as `queryLimit`. */
  grabLimit: number | null;
  createdAt: string;
  updatedAt: string;
}

/**
 * The mode that makes Arbitarr answer a download with a 302 at the indexer's own
 * URL — which carries the INDEXER's API key to whoever called.
 *
 * Exported as a constant rather than inlined because three places must agree on
 * the exact spelling (the option, the warning's condition, and the request
 * builders), and the server accepts only this exact ordinal form: `'redirect'`
 * and `'1'` are both 400s.
 */
export const REDIRECT_ACCESS_MODE = 'Redirect';

/**
 * The default, and the mode that does NOT expose the key: Arbitarr fetches the
 * payload upstream and streams the bytes. A new source gets this unless the
 * operator changes it.
 */
export const PROXY_ACCESS_MODE = 'Proxy';

/**
 * The four states a configured source can be in, mirroring the server's closed
 * `SourceRuntimeState` enum.
 *
 * FOUR DISTINCT LABELS, NOT ONE "UNAVAILABLE". The server entity's own doc gives
 * the reason: collapsing them "reports a broken key as a temporary pause and
 * removes the signal to go and fix it". The fixes genuinely differ — a budgeted
 * source needs a higher limit or patience, a backing-off one needs nothing at
 * all, and a permanently disabled one needs a human to replace a credential.
 * This is the same argument `OUTCOME_LABELS` in `Sources.tsx` makes for the five
 * probe outcomes, and it is the house style for this kind of distinction.
 *
 * CLOSED ON PURPOSE, on both sides: the server enum carries no string field, so
 * no upstream body, exception message or credential can travel with it.
 */
export type SourceRuntimeState =
  | 'Healthy'
  | 'Budgeted'
  | 'BackingOff'
  | 'PermanentlyDisabled';

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
  /**
   * Optional server-side: omitting it stores `'Proxy'`. Sent ALWAYS anyway, so
   * the form is explicit about the security-relevant choice it just presented
   * rather than relying on a default agreeing with the control's initial value.
   */
  nzbAccessMode?: string;
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
  /**
   * A FOURTH null policy, and the reason it is called out rather than folded in
   * with one of the three above: omitting this LEAVES THE STORED MODE ALONE (it
   * is `SourceOptions.NzbAccessMode`, whose null means "no opinion"), so it is
   * `apiKey`-shaped rather than `kind`-shaped.
   *
   * It is nonetheless sent on EVERY edit, unlike `apiKey`. The form always shows
   * the operator which mode is selected, so submitting without saying so would
   * let a displayed value and a stored value diverge silently — and for this
   * particular column that divergence is the difference between the indexer key
   * staying server-side and being handed to the caller. A field the operator can
   * see is a field the submit must mean.
   */
  nzbAccessMode?: string;
}
