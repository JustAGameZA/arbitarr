import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type {
  EffectiveConfigResponse,
  RecentSearchEntry,
  StatusDiagnosticsResponse,
  StatusResponse,
} from '../../api/types';
import { useAdminKeyStore } from '../../state/adminKeyStore';

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

/**
 * arb-mhd2: the admin-gated error detail `/api/status` no longer publishes.
 *
 * Unlike the three queries above this one IS under `/api/admin/`, so `apiFetch` attaches the
 * `X-Admin-Api-Key` header from `adminKeyStore`.
 *
 * **This query is allowed to fail, and its failure is not an error the operator should see.** The
 * Dashboard is a page an operator reaches WITHOUT entering a key — that is the whole point of
 * `/api/status` being `PublicRead` — so a missing or rejected key here is the ORDINARY case, not a
 * fault. `enabled` keeps it from being issued at all with no key in the tab (no pointless 401,
 * nothing logged server-side), and `retry: false` stops react-query re-attempting a request that
 * will fail identically every time. The caller reads only `data`, never `error` or `isError`: with
 * no key `data` is simply `undefined` and every detail is absent, which is exactly the designed
 * degraded state.
 */
export function useStatusDiagnosticsQuery() {
  const adminKey = useAdminKeyStore((state) => state.key);

  return useQuery({
    queryKey: ['status', 'diagnostics'],
    queryFn: () => apiFetch<StatusDiagnosticsResponse>('/api/admin/status/diagnostics'),
    enabled: adminKey !== null,
    retry: false,
  });
}
