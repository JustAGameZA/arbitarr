import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { ObservabilityResponse, StalenessEnvelopeResponse } from '../../api/types';

/**
 * The two System endpoints, which differ in their gating -- the reason this
 * surface renders them as two independent panels rather than one query.
 *
 * /api/admin/observability is admin-gated (D2: the suppression breakdown names
 * filter rules and the served-age distribution reveals caching behaviour), so
 * needsAdminKey() matches the /api/admin/ prefix and apiFetch attaches the
 * X-Admin-Api-Key header. /api/health/staleness is RouteClassification.
 * PublicRead and gets NO header. System.test.tsx asserts both halves against
 * the headers the mocked fetch actually received, so a future change that
 * gates staleness -- or that leaks the key onto it -- fails loudly.
 *
 * Keeping them separate also means the staleness envelope still renders on a
 * server with no admin key configured, which is the permanent state of the
 * review environment.
 */

export function useObservabilityQuery() {
  return useQuery({
    queryKey: ['admin', 'observability'],
    queryFn: () => apiFetch<ObservabilityResponse>('/api/admin/observability'),
  });
}

export function useStalenessQuery() {
  return useQuery({
    queryKey: ['health', 'staleness'],
    queryFn: () => apiFetch<StalenessEnvelopeResponse>('/api/health/staleness'),
  });
}
