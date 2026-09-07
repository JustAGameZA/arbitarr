import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type {
  DecisionAgreementResponse,
  DecisionPageResponse,
  ReviewDecisionRequest,
} from '../../api/types';

/** The shadow-mode filter's three states, as the surface offers them. */
export type ShadowModeFilter = 'all' | 'shadow' | 'live';

/**
 * Builds the query string for GET /api/decisions.
 *
 * Exported for its own test: the filter-to-URL mapping is the part that breaks
 * silently — a dropped `shadowMode`, or the 'all' case sending `shadowMode=`
 * and thereby filtering to a value the operator never chose. Asserting it
 * through the rendered surface would only prove a request was made, not that it
 * asked for the right thing. Same reasoning as buildActivityQuery.
 *
 * 'all' OMITS the parameter rather than sending an empty one, because the
 * endpoint treats an absent shadowMode as "both" and a present one as a filter.
 */
export function buildDecisionsQuery(filter: ShadowModeFilter, cursor: number | null): string {
  const params = new URLSearchParams();

  if (filter !== 'all') {
    params.set('shadowMode', filter === 'shadow' ? 'true' : 'false');
  }

  if (cursor !== null) {
    // Opaque: echoed back exactly as the server issued it. Never computed here.
    params.set('cursor', String(cursor));
  }

  const query = params.toString();
  return query === '' ? '/api/decisions' : `/api/decisions?${query}`;
}

/** The cache key every decision read and the review write agree on. */
const DECISIONS_KEY = ['decisions'];
const AGREEMENT_KEY = ['decisions', 'agreement'];

/**
 * One page of reviewable pipeline decisions.
 *
 * /api/decisions is RouteClassification.PublicRead and is NOT under /api/admin/,
 * so apiFetch attaches no X-Admin-Api-Key — #59's ruling, the same treatment
 * /api/activity gets while reading these very rows. Note this differs from the
 * suppression audit log on the same surface, which IS admin-gated: the two
 * panels genuinely have different classifications, and the path prefix decides.
 */
export function useDecisionsQuery(filter: ShadowModeFilter, cursor: number | null) {
  return useQuery({
    queryKey: [...DECISIONS_KEY, filter, cursor],
    queryFn: () => apiFetch<DecisionPageResponse>(buildDecisionsQuery(filter, cursor)),
  });
}

/**
 * The agreement counts over a window (default 7 days, the server's own default).
 *
 * Returns COUNTS. Do not divide here — the ratio is formed at the point of
 * display by agreementRate/formatRate, so the zero-reviews case renders the
 * em-dash rather than a fabricated 0%.
 */
export function useAgreementQuery() {
  return useQuery({
    queryKey: AGREEMENT_KEY,
    queryFn: () => apiFetch<DecisionAgreementResponse>('/api/decisions/agreement'),
  });
}

/**
 * POST /api/admin/decisions/{id}/review — recording a verdict.
 *
 * IDEMPOTENT PER DECISION: the server updates the verdict columns on the
 * decision's own row, so reviewing twice changes the verdict rather than
 * appending a second review. That is why this invalidates and refetches instead
 * of pushing the new verdict onto a local list: the refetched row is the single
 * copy of the truth, and a locally-appended one would render a duplicate the
 * server does not have.
 *
 * Both keys are invalidated because a review moves BOTH numbers: the row's own
 * verdict, and the aggregate on the Dashboard that counts it. Refreshing only
 * the list would leave the agreement rate stale on the surface whose entire
 * purpose is to decide about shadow mode.
 *
 * The admin key needs no handling here: apiFetch attaches it by path prefix, and
 * this path starts with /api/admin/. The key stays session-only in Zustand.
 */
export function useReviewDecisionMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: ({ id, review }: { id: number; review: ReviewDecisionRequest }) =>
      apiFetch<void>(`/api/admin/decisions/${id}/review`, {
        method: 'POST',
        body: JSON.stringify(review),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: DECISIONS_KEY });
      void queryClient.invalidateQueries({ queryKey: AGREEMENT_KEY });
    },
  });
}
