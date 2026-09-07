import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';
import type {
  CreateSourceRequest,
  SourceSummary,
  SourceTestResult,
  UpdateSourceRequest,
} from './types';

const SOURCES_KEY = ['admin', 'sources'];
const SOURCES_ROUTE = '/api/admin/sources';

/**
 * GET /api/admin/sources.
 *
 * Gated like every other admin route — by PATH PREFIX, never by verb, so this
 * read carries the admin key exactly as the writes do. `apiFetch` attaches it
 * from the session-only Zustand store; nothing about sources is ever written to
 * browser storage.
 */
export function useSourcesQuery() {
  return useQuery({
    queryKey: SOURCES_KEY,
    queryFn: () => apiFetch<SourceSummary[]>(SOURCES_ROUTE),
  });
}

/**
 * The four writes share one invalidation of the list.
 *
 * Refetching rather than patching the cached array by hand matters more here
 * than elsewhere: `hasApiKey` is derived server-side from a row the client can
 * never read, so a locally-reconstructed list would be guessing at the one
 * field the operator most needs to be true.
 */
function useSourcesInvalidation() {
  const client = useQueryClient();
  return () => client.invalidateQueries({ queryKey: SOURCES_KEY });
}

export function useCreateSourceMutation() {
  const invalidate = useSourcesInvalidation();
  return useMutation({
    // gcTime: 0 because this mutation's `variables` ARE the request body, and
    // that body can carry a plaintext apiKey. React Query keeps a settled
    // mutation (variables and data included) in the MutationCache for its
    // default gcTime of 5 minutes, and queryClient.ts sets no override — so
    // without this the key outlives the form that collected it and stays
    // readable from the devtools or the console long after submit. Component
    // state being cleared is not enough; the cache is a second copy. Callers
    // also reset() on settle, which drops it immediately rather than at gc.
    gcTime: 0,
    mutationFn: (request: CreateSourceRequest) =>
      apiFetch<SourceSummary>(SOURCES_ROUTE, {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    onSuccess: invalidate,
  });
}

/**
 * PUT /api/admin/sources/{id}.
 *
 * The request object is serialized as given. The caller — not this hook —
 * decides whether `apiKey` is present, because "absent" is a meaningful value
 * in this contract (leave the stored key alone) and JSON.stringify drops
 * `undefined` properties, which is exactly the behaviour wanted. Do not add a
 * `?? ''` or a normalisation step here: an empty-string key would be sent as a
 * replacement and blank a working credential.
 */
export function useUpdateSourceMutation() {
  const invalidate = useSourcesInvalidation();
  return useMutation({
    // gcTime: 0 for the same reason as the create above: `variables` here is the
    // update body, which carries apiKey whenever the operator typed a
    // replacement. See that comment for the full rationale.
    gcTime: 0,
    mutationFn: ({ id, source }: { id: number; source: UpdateSourceRequest }) =>
      apiFetch<SourceSummary>(`${SOURCES_ROUTE}/${id}`, {
        method: 'PUT',
        body: JSON.stringify(source),
      }),
    onSuccess: invalidate,
  });
}

export function useDeleteSourceMutation() {
  const invalidate = useSourcesInvalidation();
  return useMutation({
    // DELETE answers 204 with no body; apiFetch returns undefined for that.
    mutationFn: (id: number) => apiFetch<void>(`${SOURCES_ROUTE}/${id}`, { method: 'DELETE' }),
    onSuccess: invalidate,
  });
}

/**
 * POST /api/admin/sources/{id}/test — the §3.3 connectivity probe.
 *
 * Sends no body at all: the source under test is identified by the route id and
 * its configuration, key included, is read server-side. There is nothing for the
 * client to submit, which is also why the operator can test a source whose key
 * they cannot see.
 *
 * Deliberately NOT invalidating the list: a probe changes no stored state, and
 * refetching would discard the result the operator just asked for.
 */
export function useTestSourceMutation() {
  return useMutation({
    mutationFn: (id: number) =>
      apiFetch<SourceTestResult>(`${SOURCES_ROUTE}/${id}/test`, { method: 'POST' }),
  });
}
