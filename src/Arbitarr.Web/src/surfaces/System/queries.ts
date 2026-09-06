import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { BuildInfoResponse, ObservabilityResponse, StalenessEnvelopeResponse } from '../../api/types';

/**
 * GET /api/system/build.
 *
 * RouteClassification.PublicRead, same as useStalenessQuery: no admin header attached, and kept
 * as its own independent query so the build panel renders even when useObservabilityQuery's
 * admin-gated request fails.
 */
export function useBuildInfoQuery() {
  return useQuery({
    queryKey: ['system', 'build'],
    queryFn: () => apiFetch<BuildInfoResponse>('/api/system/build'),
  });
}

/**
 * GET /api/admin/observability.
 *
 * Admin-gated (D2: the suppression breakdown names filter rules and the
 * served-age distribution reveals caching behaviour), so needsAdminKey() matches
 * the /api/admin/ prefix and apiFetch attaches the X-Admin-Api-Key header.
 */
export function useObservabilityQuery() {
  return useQuery({
    queryKey: ['admin', 'observability'],
    queryFn: () => apiFetch<ObservabilityResponse>('/api/admin/observability'),
  });
}

/**
 * GET /api/health/staleness.
 *
 * RouteClassification.PublicRead: it gets NO admin header. Kept a separate query
 * from useObservabilityQuery so the envelope still renders on a server with no
 * admin key configured, which is the permanent state of the review environment.
 * System.test.tsx asserts both halves against the headers the mocked fetch
 * actually received, so a future change that gates staleness -- or that leaks
 * the key onto it -- fails loudly.
 */
export function useStalenessQuery() {
  return useQuery({
    queryKey: ['health', 'staleness'],
    queryFn: () => apiFetch<StalenessEnvelopeResponse>('/api/health/staleness'),
  });
}
