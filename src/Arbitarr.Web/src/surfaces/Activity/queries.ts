import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { ActivityKind, ActivityPageResponse } from '../../api/types';

/** The time windows the surface offers, and how far back each reaches. */
export const TIME_WINDOWS = {
  hour: 60 * 60 * 1000,
  day: 24 * 60 * 60 * 1000,
  week: 7 * 24 * 60 * 60 * 1000,
  all: null,
} as const;

export type TimeWindow = keyof typeof TIME_WINDOWS;

export interface ActivityFilters {
  kind: ActivityKind | 'all';
  window: TimeWindow;
}

/**
 * Builds the query string for GET /api/activity.
 *
 * Exported for its own test: the filter-to-URL mapping is the part that silently
 * breaks (a dropped `kind`, a `since` computed in local time), and asserting it
 * through the rendered surface alone would only prove the request was made, not
 * that it asked for the right thing.
 *
 * `since` is sent as an ISO-8601 UTC instant, never a local-time string — the
 * server compares it against stored UTC timestamps, so a local-time bound would
 * silently shift the window by the viewer's offset.
 */
export function buildActivityQuery(
  filters: ActivityFilters,
  cursor: number | null,
  now: number = Date.now(),
): string {
  const params = new URLSearchParams();

  if (filters.kind !== 'all') {
    params.set('kind', filters.kind);
  }

  const span = TIME_WINDOWS[filters.window];
  if (span !== null) {
    params.set('since', new Date(now - span).toISOString());
  }

  if (cursor !== null) {
    // Opaque: echoed back exactly as the server issued it. Never computed here.
    params.set('cursor', String(cursor));
  }

  const query = params.toString();
  return query === '' ? '/api/activity' : `/api/activity?${query}`;
}

/**
 * One page of activity.
 *
 * /api/activity is RouteClassification.PublicRead and is not under /api/admin/,
 * so apiFetch attaches no X-Admin-Api-Key — the same treatment the Dashboard's
 * three reads get. That is #59's ruling applied: the admin key gates mutating
 * actions, and reading history mutates nothing.
 *
 * The cursor is part of the query key so each page caches separately; without
 * it, paging would overwrite one cache entry and re-render the same page.
 */
export function useActivityQuery(filters: ActivityFilters, cursor: number | null) {
  return useQuery({
    queryKey: ['activity', filters.kind, filters.window, cursor],
    queryFn: () => apiFetch<ActivityPageResponse>(buildActivityQuery(filters, cursor)),
  });
}
