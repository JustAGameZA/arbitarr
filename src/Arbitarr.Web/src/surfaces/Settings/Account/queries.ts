import { useMutation } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

/**
 * POST /api/auth/password (#96).
 *
 * Under `/api/auth/` and NOT `/api/admin/`, which is load-bearing on this side
 * too: `needsAdminKey` keys off that prefix, so no admin key is attached to this
 * request. The server refuses the key anyway -- a machine credential must not be
 * able to rotate a human's password -- and the two facts agree by construction
 * rather than by a rule somebody has to keep true. The CSRF header
 * (`SESSION_REQUEST_HEADER`) is attached by `apiFetch` unconditionally, which is
 * what the handler requires alongside the cookie.
 *
 * The response is 204 with no body and DELIBERATELY no `Set-Cookie`: the session
 * that made the request is spared, not replaced. Nothing is invalidated here --
 * the session query's answer (who am I) is unchanged by a password change, and
 * refetching it would only invite the impression that something about the
 * identity moved.
 *
 * `gcTime: 0` IS LOAD-BEARING, NOT A TUNING KNOB. It is the same idiom #93
 * established for the minted API key and #44 for the login password -- one rule
 * for this codebase's credential-carrying mutations rather than three. react-query
 * retains a settled mutation's `variables` -- here BOTH passwords -- on the
 * MutationCache for the default five minutes, reachable from the devtools or the
 * console. Zero drops it the moment its last observer leaves, which is what makes
 * the per-call reset in `Account.tsx` erase the values rather than merely detach
 * them. Neither half is sufficient alone: drop either and both passwords go back
 * into the cache.
 *
 * Do NOT reset from a hook-level `onSuccess`/`onSettled` here, however obvious a
 * home this looks. Those are awaited BEFORE the settle action is dispatched, and
 * that dispatch is what runs the per-call callbacks in `Account.tsx` that record
 * the outcome; resetting from up here removes the observer they arrive on, so the
 * server's rejection is swallowed and the form sits there looking as though
 * nothing happened. The reset belongs after the capture -- see
 * `dropChangeFromCache`, and the control test that proves this placement matters.
 */
export function useChangePasswordMutation() {
  return useMutation({
    gcTime: 0,
    mutationFn: (request: ChangePasswordRequest) =>
      apiFetch<void>('/api/auth/password', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
  });
}
