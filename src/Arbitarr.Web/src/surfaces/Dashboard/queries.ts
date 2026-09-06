import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type {
  EffectiveConfigResponse,
  RecentSearchEntry,
  StatusResponse,
} from '../../api/types';

/**
 * The three read-only dashboard endpoints.
 *
 * All three are RouteClassification.PublicRead and none is under /api/admin/,
 * so apiFetch attaches NO X-Admin-Api-Key header for any of them -- AC7's
 * requirement, and asserted directly in Dashboard.test.tsx by inspecting the
 * headers the mocked fetch actually received.
 *
 * A note on the shape of StatusResponse, because the legacy wwwroot/app.js is
 * wrong about it and copying that file would have shipped the bug: app.js
 * renders `data.workerStatus`, and StatusEndpoint.cs has no such property. The
 * worker information lives under `worker` as a WorkerHealthResponse object.
 * These hooks are typed from the C# records instead.
 */

export function useStatusQuery() {
  return useQuery({
    queryKey: ['status'],
    queryFn: () => apiFetch<StatusResponse>('/api/status'),
  });
}

export function useRecentSearchesQuery() {
  return useQuery({
    queryKey: ['searches', 'recent'],
    queryFn: () => apiFetch<RecentSearchEntry[]>('/api/searches/recent'),
  });
}

export function useEffectiveConfigQuery() {
  return useQuery({
    queryKey: ['config', 'effective'],
    queryFn: () => apiFetch<EffectiveConfigResponse>('/api/config/effective'),
  });
}
