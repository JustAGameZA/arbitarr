import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';
import type { OllamaConfig, OllamaTestResult, UpdateOllamaConfigRequest } from './types';

const AI_KEY = ['admin', 'ai', 'ollama'];
const OLLAMA_ROUTE = '/api/admin/ai/ollama';

/**
 * GET /api/admin/ai/ollama.
 *
 * Gated like every other admin route — by PATH PREFIX, never by verb, so this
 * read carries the admin key exactly as the write does. `apiFetch` attaches it
 * from the session-only Zustand store.
 */
export function useOllamaConfigQuery() {
  return useQuery({
    queryKey: AI_KEY,
    queryFn: () => apiFetch<OllamaConfig>(OLLAMA_ROUTE),
  });
}

/**
 * PUT /api/admin/ai/ollama.
 *
 * The request goes to the server exactly as assembled, with NO client-side
 * validation in front of it — the same rule `useUpdateSettingMutation` and the
 * notifications section state, for the same reason: the server rejects an
 * invalid value and never quietly adjusts one, and a second opinion here would
 * either block a value the server accepts or invent a rejection it never issued.
 * The operator reads the server's exact words.
 *
 * <b>NO `gcTime: 0` HERE, DELIBERATELY — and that is a decision, not an
 * omission.</b> The sibling mutations in `Sources/queries.ts`,
 * `Notifications/queries.ts` and `ApiKeys/queries.ts` all set it because their
 * `variables` or `data` carry a CREDENTIAL (a source API key, a webhook URL
 * whose path is the token, a freshly minted key), and react-query retains a
 * settled mutation in the MutationCache for five minutes by default, where the
 * devtools or the console can still read it. Nothing here is secret: the base
 * URL is served back on the GET above, and the server rejects a URL containing
 * credentials so it cannot become secret later. Adding `gcTime: 0` would imply a
 * confidentiality property this value does not have and does not need. Recorded
 * against bead arb-689 so the asymmetry with its three siblings reads as
 * intentional rather than as an oversight in a later sweep.
 *
 * The caller still captures the server's rejection from the PER-CALL `onError`
 * into component state (see `Ai.tsx`), because a successful save invalidates
 * this query and the refetch remounts the form — so an outcome held inside it
 * would be destroyed by the very save it is reporting on.
 */
export function useUpdateOllamaConfigMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateOllamaConfigRequest) =>
      apiFetch<OllamaConfig>(OLLAMA_ROUTE, {
        method: 'PUT',
        body: JSON.stringify(request),
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: AI_KEY }),
  });
}

/**
 * POST /api/admin/ai/ollama/test — the connectivity probe.
 *
 * Sends no body at all: the address under test is the STORED one, read
 * server-side. That is what makes the button answer the question an operator is
 * actually asking ("does the address I saved work") rather than probing whatever
 * happens to be in the field at the time.
 *
 * Deliberately NOT invalidating the config: a probe changes no stored state, and
 * refetching would remount the form and discard the result the operator just
 * asked for.
 */
export function useTestOllamaMutation() {
  return useMutation({
    mutationFn: () => apiFetch<OllamaTestResult>(`${OLLAMA_ROUTE}/test`, { method: 'POST' }),
  });
}
