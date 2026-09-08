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
 * No `gcTime: 0` here (arb-689 settled this): this mutation's variables are a
 * bare id and its data is void, so there is nothing sensitive for the
 * MutationCache to retain either way. The rule as of arb-689's shared hook
 * (`useSecretEvictingMutation`) is that `gcTime: 0` marks a mutation whose
 * request or response can actually carry a secret — not a blanket applied for
 * consistency's own sake. An earlier version of this comment set the zero
 * anyway "for consistency with the create", which is exactly the divergence
 * arb-689 was filed to settle: `useDeleteSourceMutation` (Sources) never had
 * a matching zero, so "for consistency" was inconsistent across the two
 * sections it was meant to unify.
 */
export function useRevokeApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) => apiFetch<void>(`${KEYS_ROUTE}/${id}`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: API_KEYS_KEY }),
  });
}

/**
 * DELETE /api/admin/keys/{id}/tombstone (#98).
 *
 * Removal is a HARD DELETE of an already-revoked key's row, and a SEPARATE route
 * from the revoke above rather than a flag on it. The two-step is the feature: a
 * live credential cannot be destroyed in one call, so there is no client state in
 * which a single click both revokes and erases. The sub-path names what is being
 * deleted — the tombstone, not the key, which is already dead by the time this
 * route answers at all.
 *
 * There is NO client-side guard against removing a live key, for the same reason
 * the revoke has none: the server refuses it with an explanatory message naming
 * the key and stating the two-step, and a second opinion here would either block a
 * removal the server allows or invent a refusal it never issued.
 *
 * No `gcTime: 0` here either, for the same reason as the revoke above: this
 * mutation's variables are an id and its data is void, so unlike the create
 * there is no plaintext for the MutationCache to hold. `ApiKeys.tsx`'s
 * `onRemove` still calls `remove.reset()` on settle, but that is for fresh
 * `.error` state on the next attempt, not secret eviction — it does not need
 * `gcTime: 0` to work, and does not go through `useSecretEvictingMutation`.
 */
export function useRemoveApiKeyMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (id: number) =>
      apiFetch<void>(`${KEYS_ROUTE}/${id}/tombstone`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: API_KEYS_KEY }),
  });
}
