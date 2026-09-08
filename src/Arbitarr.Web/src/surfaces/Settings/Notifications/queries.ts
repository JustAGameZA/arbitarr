import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../../api/client';
import type {
  NotificationConfig,
  NotificationTestResult,
  UpdateNotificationConfigRequest,
} from '../../../api/types';

const NOTIFICATIONS_ROUTE = '/api/admin/notifications';
const NOTIFICATIONS_KEY = ['admin', 'notifications'];

export function useNotificationConfigQuery() {
  return useQuery({
    queryKey: NOTIFICATIONS_KEY,
    queryFn: () => apiFetch<NotificationConfig>(NOTIFICATIONS_ROUTE),
  });
}

/**
 * PUT /api/admin/notifications.
 *
 * The request goes to the server exactly as assembled, with NO client-side
 * bounds check in front of it — the same rule, for the same reason, that
 * `useUpdateSettingMutation` states: the server rejects out-of-range values and
 * never clamps them, and a second opinion here would either block a value the
 * server accepts or invent a rejection it never issued. The operator reads the
 * server's exact words.
 *
 * The caller omits `webhookUrl` entirely when the operator did not type one.
 * That is not a convenience: the client never held the stored URL, so it cannot
 * read-and-reapply one, and sending an empty string on every threshold edit
 * would depend on the server normalizing it back to "leave alone". Omission is
 * the contract; the field appears only when there is a new value to write.
 *
 * <b>THE MUTATION CACHE IS THE LAST PLACE THE CLIENT CAN HOLD THIS SECRET, AND
 * THAT IS WHAT `gcTime: 0` PLUS A SETTLE-TIME EVICTION ARE FOR.</b> Clearing
 * the input and dropping the value from React state is not sufficient on its
 * own: react-query retains every settled mutation's `variables` on the
 * MutationCache for `gcTime`, which defaults to five minutes and which
 * `api/queryClient.ts` does not set for mutations. Since the webhook URL
 * travels as a mutation variable, without an explicit eviction it stays
 * readable from the devtools or the console long after the field looks empty.
 * `gcTime: 0` makes the entry collectable the moment it settles; the actual
 * `reset()` that collects it belongs to the PER-CALL site in
 * `Notifications.tsx` via `useSecretEvictingMutation` (arb-689), not to a
 * hook-level callback here — see that hook's module doc for why a hook-level
 * `reset()` would delete the delivery route to the per-call callbacks before
 * they run, silently swallowing the server's rejection. This mutation
 * previously worked around that with a `setTimeout(..., 0)` deferral in a
 * hook-level `onSettled`; that workaround is gone now that eviction happens
 * from the per-call site, which needs no deferral because it runs after the
 * dispatch by construction.
 */
export function useUpdateNotificationConfigMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateNotificationConfigRequest) =>
      apiFetch<NotificationConfig>(NOTIFICATIONS_ROUTE, {
        method: 'PUT',
        body: JSON.stringify(request),
      }),
    // Nothing to garbage-collect later: this mutation's variables may carry the
    // webhook URL, so the entry must not outlive the request. See the doc above.
    gcTime: 0,
    onSuccess: () => client.invalidateQueries({ queryKey: NOTIFICATIONS_KEY }),
  });
}

/**
 * DELETE /api/admin/notifications/webhook — the explicit "forget the target".
 *
 * Separate from the PUT on purpose. Clearing a secret the operator cannot see
 * must be something they asked for by name, never a side effect of an edit that
 * happened to leave a field blank.
 */
export function useClearWebhookMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiFetch<NotificationConfig>(`${NOTIFICATIONS_ROUTE}/webhook`, { method: 'DELETE' }),
    onSuccess: () => client.invalidateQueries({ queryKey: NOTIFICATIONS_KEY }),
  });
}

/**
 * POST /api/admin/notifications/test.
 *
 * Invalidates the config on success because the server records the attempt as
 * the last delivery outcome exactly as a live notification would — the
 * operator's test and the notifier's own health read the same field rather than
 * diverging, so the panel must refetch to show it.
 */
export function useSendTestNotificationMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: () =>
      apiFetch<NotificationTestResult>(`${NOTIFICATIONS_ROUTE}/test`, { method: 'POST' }),
    onSuccess: () => client.invalidateQueries({ queryKey: NOTIFICATIONS_KEY }),
  });
}
