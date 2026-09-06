import { useAdminKeyStore } from '../state/adminKeyStore';

/** Matches AdminApiKeyFilter.HeaderName (src/Arbitarr.Api/Admin/AdminApiKeyFilter.cs:23). */
export const ADMIN_KEY_HEADER = 'X-Admin-Api-Key';

/**
 * Whether a request carries the admin key, decided by PATH PREFIX and never by
 * HTTP verb.
 *
 * Every /api/admin/ route is gated, GETs included -- 12 .RequireAdminApiKey()
 * call sites across 7 files:
 *   Admin/AdminRuleEndpoints.cs (5)      Admin/AdminSettingsEndpoints.cs (2)
 *   Admin/SuppressionViewEndpoint.cs (1) Admin/AdminPingEndpoint.cs (1)
 *   Dashboard/ObservabilityEndpoint.cs (1)
 *   Search/AdHocSearchEndpoint.cs (1)      <- MapGet /api/admin/search
 *   Search/MatchExplanationEndpoint.cs (1) <- MapGet .../{proxyGuid}/explanation
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

  if (rest.body !== undefined && headers['Content-Type'] === undefined) {
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
