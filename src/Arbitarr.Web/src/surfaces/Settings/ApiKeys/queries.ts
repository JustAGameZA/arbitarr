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
 *
 * `gcTime: 0` is load-bearing, not a tuning knob. The MutationCache is the LAST
 * place on the client that can hold the plaintext once the reveal panel is gone:
 * react-query keeps a settled mutation's `state.data` — the whole
 * `CreatedApiKeyResponse`, plaintext included — for the default five minutes,
 * reachable from the devtools or the console. Zero means the mutation is dropped
 * from the cache the moment its last observer leaves, which is what makes the
 * reset in `ApiKeys.tsx` actually erase the value instead of merely detaching it:
 * `reset()` removes the observer and schedules the GC, and only a zero gcTime lets
 * that GC run now rather than five minutes from now. Neither half is sufficient
 * alone — dropping either one puts the plaintext back in the cache.
 *
 * Do NOT move that reset into a hook-level `onSuccess`/`onSettled` here, however
 * obvious a home this looks. Those are awaited BEFORE the settle action is
 * dispatched, and that dispatch is what runs the per-call callbacks in
 * `ApiKeys.tsx` that capture the response; resetting from here removes the
 * observer they arrive on, so the reveal never receives the key and a server
 * rejection is swallowed silently. The reset belongs after the capture — see the
 * comment on `dropCreateFromCache`.
 */
export function useCreateApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
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
 *
 * `gcTime: 0` for consistency with the create above. This mutation's variables are
 * an id and its data is void, so nothing sensitive is retained either way — but
 * one rule for this file's mutations is one fewer thing to get wrong when the next
 * one is added, and a reader who finds the zero only on the create is invited to
 * conclude it was arbitrary there.
 */
export function useRevokeApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: (id: number) => apiFetch<void>(`${KEYS_ROUTE}/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: API_KEYS_KEY }),
  });
}
