import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { SuppressionViewEntry } from '../../api/types';

/**
 * GET /api/admin/suppressions, optionally filtered to one query key.
 *
 * The endpoint answers a BARE ARRAY (Results.Ok(entries)), not an envelope, and
 * orders by OccurredAt descending server-side. The filter is a real server-side
 * WHERE on QueryKey — not a client-side narrowing of a full fetch — so an empty
 * filter and a filter that matches nothing are different requests with
 * different costs, and both legitimately answer [].
 */
export function useSuppressionsQuery(queryKey: string) {
  const trimmed = queryKey.trim();
  return useQuery({
    queryKey: ['admin', 'suppressions', trimmed],
    queryFn: () =>
      apiFetch<SuppressionViewEntry[]>(
        trimmed === ''
          ? '/api/admin/suppressions'
          : `/api/admin/suppressions?queryKey=${encodeURIComponent(trimmed)}`,
      ),
  });
}
