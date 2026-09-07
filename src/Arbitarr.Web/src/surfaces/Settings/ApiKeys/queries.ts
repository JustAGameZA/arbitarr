import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';
import type { ApiKeyEntry, CreateApiKeyRequest, CreatedApiKeyResponse } from '../../../api/types';

const API_KEYS_KEY = ['admin', 'keys'];

const KEYS_ROUTE = '/api/admin/keys';

/**
 * GET /api/admin/keys.
 *
 * Admin-scoped like every other route under this prefix, GET included and
 * deliberately: a key list tells a caller which labels exist, what authority each
 * holds and which are dormant. `apiFetch` attaches the key by path prefix, so
 * nothing is needed here to make that happen.
 */
export function useApiKeysQuery() {
  return useQuery({
    queryKey: API_KEYS_KEY,
    queryFn: () => apiFetch<ApiKeyEntry[]>(KEYS_ROUTE),
  });
}

/**
 * POST /api/admin/keys.
 *
 * The response carries the plaintext exactly once. It is returned to the caller
 * and DELIBERATELY NOT written into the query cache: `invalidateQueries` refetches
 * the list, whose projection has no field capable of holding a key value, so the
 * cache never holds a credential even for a moment. The plaintext's only home is
 * the calling component's state.
 *
 * The scope is sent as its NAME. Never send the numeric form — the server matches
 * the two names explicitly precisely so `{"scope":"1"}` cannot mint an Admin key,
 * and a client that posted an index would be asking for the guard to be relaxed.
 */
export function useCreateApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: CreateApiKeyRequest) =>
      apiFetch<CreatedApiKeyResponse>(KEYS_ROUTE, {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: API_KEYS_KEY }),
  });
}

/**
 * DELETE /api/admin/keys/{id}.
 *
 * Revocation is a tombstone, not a row deletion: the refetched list still carries
 * the key with a `revokedAt`, and the UI keeps rendering it. A client that removed
 * the row optimistically would be inventing a disappearance the server never
 * performed.
 *
 * There is NO client-side guard against revoking the last admin key. The server
 * refuses that (AC5) with an explanatory message, and a second opinion here would
 * either block a revocation the server allows or invent a refusal it never issued
 * — the same reason the settings editor pre-checks no bounds.
 */
export function useRevokeApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => apiFetch<void>(`${KEYS_ROUTE}/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: API_KEYS_KEY }),
  });
}
