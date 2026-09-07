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
 */
export function useUpdateNotificationConfigMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (request: UpdateNotificationConfigRequest) =>
      apiFetch<NotificationConfig>(NOTIFICATIONS_ROUTE, {
        method: 'PUT',
        body: JSON.stringify(request),
      }),
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
