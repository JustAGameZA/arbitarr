import { useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { MatchExplanation, SuppressionViewEntry } from '../../api/types';

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

/**
 * GET /api/admin/search/{proxyGuid}/explanation — the original-vs-rewritten
 * title pair AC11 asks for alongside the acting layer.
 *
 * KNOWN BACKEND GAP, ported deliberately rather than silently dropped (the same
 * class of gap the Search surface hits, but with a different root cause):
 *
 *   - The endpoint resolves releases through IReleaseLookup, keyed on
 *     RenderedRelease.ProxyGuid.
 *   - ProxyGuid is ReleaseGuid.Compute(ReleaseIdentity(SourceName, Candidate.Guid)),
 *     and that is an HMAC-SHA256 keyed by a per-instance secret that is
 *     deliberately never served to clients (SEC-L2, so proxy guids cannot be
 *     enumerated). No client can compute one, given any inputs.
 *   - The audit row this surface lists carries only Candidate.Guid:
 *     SuppressionAuditLogMapper writes `ReleaseIdentifier = record.Release.Guid`
 *     and drops the SourceName half of the identity entirely.
 *
 * So even if the secret were public, the row is missing an input. Closing this
 * needs a schema change (persist SourceName, or persist the ProxyGuid itself)
 * plus a projection change, both under src/Arbitarr.Api/ and src/Arbitarr.Data/,
 * which AC17 puts out of scope for this step. The affordance is wired to the
 * identifier the row actually has, and the surface renders whatever the server
 * answers — including a 404 — as a plain sentence rather than pretending the
 * lookup succeeded.
 */
export function useExplanationQuery(releaseIdentifier: string | null) {
  return useQuery({
    queryKey: ['explanation', releaseIdentifier],
    queryFn: () =>
      apiFetch<MatchExplanation>(
        `/api/admin/search/${encodeURIComponent(releaseIdentifier ?? '')}/explanation`,
      ),
    enabled: releaseIdentifier !== null,
  });
}
