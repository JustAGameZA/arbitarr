import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { SettingCatalogEntry } from '../../api/types';

const SETTINGS_KEY = ['admin', 'settings'];

export function useSettingsQuery() {
  return useQuery({
    queryKey: SETTINGS_KEY,
    queryFn: () => apiFetch<SettingCatalogEntry[]>('/api/admin/settings'),
  });
}

/**
 * PUT /api/admin/settings/{key}.
 *
 * The value goes to the server exactly as typed. There is deliberately NO
 * client-side bounds check in front of this call: the legacy admin-settings.js
 * had an isWithinBounds() guard that blocked the request and displayed a
 * message of its own invention, which meant the operator saw a rejection the
 * server never issued and, when the two disagreed, a value the server would
 * have accepted could not be set at all. The server owns validation; the client
 * shows what it says.
 */
export function useUpdateSettingMutation() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: ({ key, value }: { key: string; value: string }) =>
      apiFetch<void>(`/api/admin/settings/${encodeURIComponent(key)}`, {
        method: 'PUT',
        body: JSON.stringify({ value }),
      }),
    onSuccess: () => client.invalidateQueries({ queryKey: SETTINGS_KEY }),
  });
}
