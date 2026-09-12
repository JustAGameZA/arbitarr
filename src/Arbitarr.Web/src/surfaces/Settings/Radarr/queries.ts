import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';
import type { RadarrConfig, RadarrTestResult, UpdateRadarrConfigRequest } from './types';

const RADARR_KEY = ['admin', 'arr', 'radarr'];
const RADARR_ROUTE = '/api/admin/arr/radarr';

/**
 * GET /api/admin/arr/radarr.
 *
 * Gated like every other admin route — by PATH PREFIX, never by verb, so this
 * read carries the admin key exactly as the writes do. `apiFetch` attaches it
 * from the session-only Zustand store.
 */
export function useRadarrConfigQuery() {
  return useQuery({
    queryKey: RADARR_KEY,
    queryFn: () => apiFetch<RadarrConfig>(RADARR_ROUTE),
  });
}

/**
 * PUT /api/admin/arr/radarr.
 *
 * <b>`gcTime: 0` BECAUSE THIS MUTATION'S `variables` CAN CARRY THE API KEY.</b>
 * React Query keeps a settled mutation — variables and data included — in the
 * MutationCache for its default gcTime of five minutes, and `queryClient.ts` sets
 * no override, so without this the key would outlive the form that collected it
 * and stay readable from the devtools or the console long after submit. Clearing
 * component state is not enough; the cache is a second copy. This matches
 * `Sonarr/queries.ts`, `Sources/queries.ts` and `Notifications/queries.ts` and
 * deliberately DIFFERS from `Ai/queries.ts`, whose body carries nothing secret
 * (see bead arb-689 for why that asymmetry is intentional). The caller also
 * `reset()`s on settle, which drops it immediately rather than at gc.
 *
 * The request goes to the server exactly as assembled, with NO client-side
 * validation in front of it — the same rule every sibling mutation on this
 * surface states, for the same reason: the server rejects an invalid value and
 * never quietly adjusts one, and a second opinion here would either block a value
 * the server accepts or invent a rejection it never issued. The operator reads
 * the server's exact words.
 *
 * The caller decides whether `apiKey` is present, because ABSENT IS A MEANINGFUL
 * VALUE in this contract (leave the stored key alone) and `JSON.stringify` drops
 * `undefined` properties, which is exactly the behaviour wanted. Do not add a
 * `?? ''` or a normalisation step here: an empty-string key would be sent as a
 * replacement and the server would reject the whole write.
 */
export function useUpdateRadarrConfigMutation() {
  const client = useQueryClient();
  return useMutation({
    gcTime: 0,
    mutationFn: (request: UpdateRadarrConfigRequest) =>
      apiFetch<RadarrConfig>(RADARR_ROUTE, {
        method: 'PUT',
        body: JSON.stringify(request),
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: RADARR_KEY }),
  });
}

/*
 * THERE IS DELIBERATELY NO CLEAR MUTATION HERE (ADR 0010; bead arb-c26).
 *
 * A key-only clear does not exist on the server: under the shared rule a secret
 * is cleared only by deleting the thing that owns it, and clearing this key alone
 * would leave an address with no credential — the half-configured state the
 * Sources surface never offers. `DELETE /api/admin/arr/radarr` unconfigures the
 * whole instance, and this page does not call it yet: it is a destructive
 * whole-section action wanting the two-step confirmation the Notifications
 * section uses, which is its own piece of work rather than a hook added in
 * passing. Recorded here so its absence reads as a decision rather than an
 * oversight for a later sweep to "complete".
 */

/**
 * POST /api/admin/arr/radarr/test — the connectivity probe.
 *
 * Sends no body at all: the address and key under test are the STORED ones, read
 * server-side. That is what makes the button answer the question an operator is
 * actually asking ("does what I saved work") rather than probing whatever happens
 * to be in the fields at the time — and it is also why the key never has to leave
 * the server to be tested.
 *
 * Deliberately NOT invalidating the config: a probe changes no stored state, and
 * refetching would remount the form and discard the result the operator just
 * asked for.
 */
export function useTestRadarrMutation() {
  return useMutation({
    mutationFn: () => apiFetch<RadarrTestResult>(`${RADARR_ROUTE}/test`, { method: 'POST' }),
  });
}
