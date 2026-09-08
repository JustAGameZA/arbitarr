import { useAdminKeyStore } from '../state/adminKeyStore';

/** Matches AdminApiKeyFilter.HeaderName (src/Arbitarr.Api/Admin/AdminApiKeyFilter.cs:23). */
export const ADMIN_KEY_HEADER = 'X-Admin-Api-Key';

/**
 * Whether a request carries the admin key, decided by PATH PREFIX and never by
 * HTTP verb.
 *
 * Every /api/admin/ route is gated, GETs included -- 28 .RequireAdminApiKey()
 * call sites across 10 files (was 12 across 7 when this note was written; the
 * COUNT drifts as routes are added, the RULE does not):
 *   Admin/AdminSourceEndpoints.cs (6)    Admin/AdminRuleEndpoints.cs (5)
 *   Admin/AdminNotificationEndpoints.cs (5)
 *   Admin/AdminApiKeyEndpoints.cs (3)    Admin/AdminBackupEndpoints.cs (3)
 *   Admin/AdminSettingsEndpoints.cs (2)  Admin/AdminPingEndpoint.cs (1)
 *   Admin/AdminSecurityEndpoints.cs (1)  Admin/LogsEndpoint.cs (1)
 *   Dashboard/DecisionReviewEndpoints.cs (1)
 * plus SuppressionViewEndpoint, ObservabilityEndpoint, AdHocSearchEndpoint and
 * MatchExplanationEndpoint, which reach the same convention indirectly.
 *
 * #56's GET /api/admin/backup is the sharpest case for the prefix rule: its
 * RESPONSE BODY is the instance's HMAC secret and every source API key. A verb
 * split would serve that file to anyone who can reach the port.
 *
 * Those last two, plus SuppressionViewEndpoint's MapGet, ARE this app's Search
 * and Suppressions surfaces. Splitting attachment on the verb -- "read-only
 * calls send no key" -- would 401 both surfaces outright in production while
 * every mocked component test kept passing, because the mocks answer whatever
 * they are asked. The prefix rule has no such blind spot.
 *
 * Reproduce the count:
 *   git grep -n "\.RequireAdminApiKey()" -- 'src/Arbitarr.Api/**\/*.cs'
 * (Anchor the leading dot and use **\/*.cs; the unanchored, Admin/-only form
 *  returns 14 and 9 respectively, and both numbers are wrong.)
 */
export const needsAdminKey = (path: string): boolean => path.startsWith('/api/admin/');

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly body?: unknown,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/**
 * Raised on 503: the server itself has no admin key configured yet.
 *
 * Since #43 a 503 additionally implies the request did NOT arrive from the
 * local network -- the server admits unkeyed admin requests from loopback and
 * private ranges precisely so a first key can be set, and only falls back to
 * 503 for callers outside them. The message stays accurate either way (no key
 * IS configured), so it is deliberately unchanged; the extra condition is not
 * surfaced here because the browser cannot tell which side of that line it is
 * on, and guessing in the copy would be worse than saying less.
 *
 * TopBar renders the actionable form of this state, which is reachable exactly
 * when the bypass applied and the UI loaded at all.
 */
export class AdminKeyNotConfiguredError extends ApiError {
  constructor(body?: unknown) {
    super(503, 'No admin API key is configured on the server.', body);
    this.name = 'AdminKeyNotConfiguredError';
  }
}

/** Raised on 401/403: our key is absent or wrong, and has been cleared. */
export class AdminKeyRejectedError extends ApiError {
  constructor(status: number, body?: unknown) {
    super(status, 'The admin API key was rejected.', body);
    this.name = 'AdminKeyRejectedError';
  }
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text();
  if (text === '') {
    return undefined;
  }
  try {
    return JSON.parse(text);
  } catch {
    // A non-JSON error body (a proxy's HTML 502 page, say) is still worth
    // surfacing verbatim rather than discarding.
    return text;
  }
}

export interface ApiRequestOptions extends Omit<RequestInit, 'headers'> {
  headers?: Record<string, string>;
}

/**
 * The single fetch wrapper. Everything that talks to the backend goes through
 * here so the key-attachment rule and the three error branches have exactly one
 * home rather than five conventions.
 */
export async function apiFetch<T>(path: string, options: ApiRequestOptions = {}): Promise<T> {
  const { headers: callerHeaders, ...rest } = options;
  const headers: Record<string, string> = { ...callerHeaders };

  // FormData is EXCLUDED deliberately, and that is load-bearing rather than tidy.
  // #56's restore uploads the archive as multipart, and a multipart request is only
  // parseable if its Content-Type carries the boundary the browser generated. Setting
  // 'application/json' here would overwrite that boundary and the server would reject a
  // well-formed upload as having no form content at all. Leaving the header unset lets
  // fetch fill in 'multipart/form-data; boundary=...' itself, which is the only way it
  // can be right -- the boundary is not knowable here.
  if (
    rest.body !== undefined &&
    !(rest.body instanceof FormData) &&
    headers['Content-Type'] === undefined
  ) {
    headers['Content-Type'] = 'application/json';
  }

  const store = useAdminKeyStore.getState();
  if (needsAdminKey(path)) {
    const key = store.key;
    if (key !== null && key !== '') {
      // Header only. The key is NEVER appended to the URL -- see
      // AdminApiKeyFilter's own note on keeping it out of access logs.
      headers[ADMIN_KEY_HEADER] = key;
    }
  }

  const response = await fetch(path, { ...rest, headers });

  if (response.ok) {
    if (response.status === 204) {
      return undefined as T;
    }
    return (await readBody(response)) as T;
  }

  const body = await readBody(response);

  // Three branches, matching AdminApiKeyFilter.cs:36-47.
  if (response.status === 503 && needsAdminKey(path)) {
    // The server has no key set (fresh install). Do NOT clear the stored key:
    // the user's key is not the problem, and clearing it would prompt them to
    // re-enter a value that cannot help until the server is configured.
    store.setServerKeyUnset(true);
    throw new AdminKeyNotConfiguredError(body);
  }

  if (response.status === 401 || response.status === 403) {
    // 401: a key is configured and ours is wrong or missing.
    // 403: defensive only -- AdminApiKeyFilter returns 503 and 401 and has no
    // 403 path, so handle it but do not assert the server ever sends it.
    store.clearKey();
    throw new AdminKeyRejectedError(response.status, body);
  }

  throw new ApiError(response.status, `Request to ${path} failed with ${response.status}.`, body);
}

/** A downloaded file: its bytes and the name the server asked us to save it under. */
export interface ApiBlob {
  blob: Blob;
  fileName: string | null;
}

/**
 * Fetches a binary response (#56's backup archive) through the SAME key-attachment
 * and error branches as apiFetch, differing only in how the success body is read.
 *
 * It exists rather than the caller using an <a download> href because the archive is
 * credential-bearing: an anchor cannot carry the X-Admin-Api-Key header, so making a
 * plain link work would mean putting a token in the URL -- into browser history, into
 * any proxy's access log, and into the backend's own Information-level record of full
 * absolute URIs. The blob is handed to the browser through an object URL instead, which
 * needs no URL-borne credential at all.
 *
 * The failure branches are deliberately NOT duplicated here: this delegates the whole
 * error path to readError below so a 401 still clears the stored key and a 503 still
 * sets the server-unset flag, exactly as every other admin call does.
 */
export async function apiFetchBlob(path: string, options: ApiRequestOptions = {}): Promise<ApiBlob> {
  const { headers: callerHeaders, ...rest } = options;
  const headers: Record<string, string> = { ...callerHeaders };

  const store = useAdminKeyStore.getState();
  if (needsAdminKey(path)) {
    const key = store.key;
    if (key !== null && key !== '') {
      // Header only, never the URL -- see this module's needsAdminKey note.
      headers[ADMIN_KEY_HEADER] = key;
    }
  }

  const response = await fetch(path, { ...rest, headers });

  if (!response.ok) {
    throw await readError(response, path);
  }

  return {
    blob: await response.blob(),
    fileName: parseContentDispositionFileName(response.headers.get('Content-Disposition')),
  };
}

/**
 * The three error branches, shared by apiFetch and apiFetchBlob so the key-clearing and
 * server-unset side effects have one home rather than two that can drift.
 */
async function readError(response: Response, path: string): Promise<ApiError> {
  const body = await readBody(response);

  if (response.status === 503 && needsAdminKey(path)) {
    useAdminKeyStore.getState().setServerKeyUnset(true);
    return new AdminKeyNotConfiguredError(body);
  }

  if (response.status === 401 || response.status === 403) {
    useAdminKeyStore.getState().clearKey();
    return new AdminKeyRejectedError(response.status, body);
  }

  return new ApiError(response.status, `Request to ${path} failed with ${response.status}.`, body);
}

/**
 * Pulls the file name out of a Content-Disposition header.
 *
 * `filename*` (RFC 5987) is preferred over `filename` because that is the one the server
 * actually sends for a non-ASCII-safe value, and it is percent-encoded -- reading the
 * plain `filename` when both are present would silently pick the fallback.
 */
export function parseContentDispositionFileName(header: string | null): string | null {
  if (header === null) {
    return null;
  }

  const extended = /filename\*=(?:UTF-8'')?([^;]+)/i.exec(header);
  if (extended !== null) {
    try {
      return decodeURIComponent(extended[1].trim());
    } catch {
      // A malformed percent-escape is not worth failing a download over; fall through
      // to the plain form below rather than throwing on the operator's save.
      return extended[1].trim();
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(header);
  return plain === null ? null : plain[1].trim();
}
