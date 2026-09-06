import { useMutation, useQuery } from '@tanstack/react-query';

import { apiFetch } from '../../api/client';
import type { AdHocSearchResponse, MatchExplanation } from '../../api/types';

/** The form's state, in the shape the surface holds it: all strings from inputs. */
export interface SearchCriteria {
  q: string;
  tvdbid: string;
  tmdbid: string;
  season: string;
  ep: string;
  cat: string;
  limit: string;
  offset: string;
  runAiSync: boolean;
}

export const EMPTY_CRITERIA: SearchCriteria = {
  q: '',
  tvdbid: '',
  tmdbid: '',
  season: '',
  ep: '',
  cat: '',
  limit: '50',
  offset: '0',
  runAiSync: false,
};

/**
 * Builds the query string for GET /api/admin/search.
 *
 * Only non-empty fields are sent, so an untouched optional input does not
 * become `&tvdbid=` -- the endpoint binds those as nullable and an empty string
 * is not the same as absent. `runAiSync` is emitted ONLY when the operator
 * opted in (AC8): its default is off, and sending `runAiSync=false` explicitly
 * would still be a request the operator never made, so absence is the default.
 *
 * Exported for its own test: the assertion that toggling the control changes
 * the outgoing request is the point of AC8, and reading it off the built string
 * is more direct than inferring it from a rendered table.
 */
export function buildSearchQuery(criteria: SearchCriteria): string {
  const params = new URLSearchParams();
  const put = (name: string, value: string) => {
    const trimmed = value.trim();
    if (trimmed !== '') {
      params.set(name, trimmed);
    }
  };

  put('q', criteria.q);
  put('tvdbid', criteria.tvdbid);
  put('tmdbid', criteria.tmdbid);
  put('season', criteria.season);
  put('ep', criteria.ep);
  put('cat', criteria.cat);
  put('limit', criteria.limit);
  put('offset', criteria.offset);
  if (criteria.runAiSync) {
    params.set('runAiSync', 'true');
  }

  return params.toString();
}

/**
 * The ad-hoc search itself, as a mutation rather than a query.
 *
 * It runs on submit, not on mount: a search is an action the operator takes,
 * and useQuery would fire one the moment the surface opened -- an unasked-for
 * upstream call on every visit, against endpoints that are rate-limited.
 */
export function useAdHocSearchMutation() {
  return useMutation({
    mutationFn: (criteria: SearchCriteria) =>
      apiFetch<AdHocSearchResponse>(`/api/admin/search?${buildSearchQuery(criteria)}`),
  });
}

/**
 * The match explanation for one release.
 *
 * Enabled only once a guid is selected, so opening the surface fetches nothing.
 * See Search.tsx for why this endpoint currently 404s for ad-hoc results.
 */
export function useExplanationQuery(guid: string | null) {
  return useQuery({
    queryKey: ['explanation', guid],
    queryFn: () =>
      apiFetch<MatchExplanation>(`/api/admin/search/${encodeURIComponent(guid ?? '')}/explanation`),
    enabled: guid !== null,
  });
}
