import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';

import { apiFetch } from '../../api/client';
import type { ArrMovieItem, ArrQueueItem, ArrSectionEnvelope, ArrSeriesItem } from './types';

/**
 * The *arr instance one section reads from. The two kinds stay separate at every level the server
 * keeps them separate at — separate routes, separate credentials, separate typed clients — so this
 * is the one place the pairing is written down rather than four near-identical call sites.
 */
export type ArrKind = 'sonarr' | 'radarr';

/**
 * Rows per page.
 *
 * Equal to `AdminArrQueueEndpoints.DefaultPageSize` (25), which is also the ceiling's floor: it is
 * under `MaxPageSize` (100), so the server never clamps it and `pageSize` in the response is always
 * the value asked for. The pager still derives from the SERVED `pageSize` rather than from this
 * constant — see `Library.tsx` — because a clamp that did start happening must stay visible instead
 * of being silently papered over by the client's own assumption.
 */
export const LIBRARY_PAGE_SIZE = 25;

/**
 * How often the ACTIVE tab re-reads, in milliseconds (owner ruling, 2026-09-17).
 *
 * Five minutes and not five seconds, and the reason is upstream's shape rather than taste: the
 * library endpoints (`/api/v3/series`, `/api/v3/movie`) are UNPAGED, so every poll is a whole-library
 * fetch on the operator's Sonarr or Radarr, not a cheap delta. A queue-like cadence against that
 * would make Arbitarr the heaviest client the *arr has. The manual Refresh control exists precisely
 * so the slow automatic cadence does not have to be the only way to get current data: an operator
 * who just started a download asks for it, rather than every operator paying for the one who did.
 *
 * A cache that would make a faster cadence affordable is arb-6l9b.7 and deliberately NOT built here.
 */
export const LIBRARY_POLL_INTERVAL_MS = 5 * 60 * 1000;

/**
 * Whether the document is currently visible, as a reactive value.
 *
 * <b>THIS IS THE APP'S FIRST POLLING SURFACE</b>, so there was no precedent to follow and none is
 * invented beyond what this screen needs: it is a hook local to Library rather than a shared utility,
 * because a second caller is the right moment to lift it and a helper written for one is an
 * abstraction guessed at rather than observed.
 *
 * Gating on `document.hidden` rather than on window FOCUS is the deliberate part. React Query's own
 * `refetchIntervalInBackground: false` keys off focus, which is a different and narrower fact: a
 * background browser tab is hidden but a merely unfocused window is not, and an operator who leaves
 * this screen open on a second monitor while working elsewhere should still get current rows.
 * Visibility is what actually distinguishes "nobody can see this" from "nobody is typing into it",
 * and it is the one that should silence a poll against someone else's server.
 */
export function useDocumentVisible(): boolean {
  const [visible, setVisible] = useState(() => !document.hidden);

  useEffect(() => {
    const onChange = () => setVisible(!document.hidden);
    document.addEventListener('visibilitychange', onChange);
    // Read once on mount too: the document can have changed visibility between the initial state
    // and this effect running, and a poll silenced by a stale `true` would never re-arm.
    onChange();
    return () => document.removeEventListener('visibilitychange', onChange);
  }, []);

  return visible;
}

/**
 * Builds the query string for a queue section.
 *
 * Exported for its own test, exactly as `buildLogsQuery` is: the filter-to-URL mapping is the part
 * that breaks silently. A dropped `page` still renders plausible rows — page 1's — under a pager
 * claiming to be on page 3, which no render-level assertion catches.
 *
 * `page` is omitted at 1 rather than sent as `page=1`: the server's own default is 1, so sending it
 * would be a parameter that only ever restates the default, and omitting it keeps a first-page
 * request distinguishable from a deliberate navigation back to it.
 */
export function buildQueueQuery(kind: ArrKind, page: number): string {
  const params = new URLSearchParams();

  if (page > 1) {
    params.set('page', String(page));
  }

  params.set('pageSize', String(LIBRARY_PAGE_SIZE));

  return `/api/admin/arr/${kind}/queue?${params.toString()}`;
}

/**
 * Builds the query string for a library section.
 *
 * The route differs by kind because the two upstreams name their collections differently and the
 * server mirrors that: Sonarr serves `series`, Radarr serves `movies`. Mapping it here rather than
 * asking the caller for a path keeps the four sections' call sites symmetrical.
 *
 * An empty or whitespace-only `q` is OMITTED rather than sent as an empty string, matching
 * `buildLogsQuery`: the server treats a whitespace filter as absent anyway, so sending one would work
 * by accident, and leaving it out keeps the request honest about what was asked.
 */
export function buildLibraryQuery(kind: ArrKind, query: string, page: number): string {
  const params = new URLSearchParams();

  const trimmed = query.trim();
  if (trimmed !== '') {
    params.set('q', trimmed);
  }

  if (page > 1) {
    params.set('page', String(page));
  }

  params.set('pageSize', String(LIBRARY_PAGE_SIZE));

  const collection = kind === 'sonarr' ? 'series' : 'movies';
  return `/api/admin/arr/${kind}/${collection}?${params.toString()}`;
}

/**
 * The options every section shares.
 *
 * `refetchInterval` is `false` while hidden, which is what actually STOPS the timer rather than
 * letting it fire into a discarded result — React Query re-evaluates the option when the value
 * changes, so flipping it back to the interval on `visibilitychange` re-arms the poll.
 *
 * `refetchOnWindowFocus` is left OFF (this app's QueryClient default) on purpose: with a five-minute
 * cadence, refetching on every focus would quietly turn window-switching into the real polling rate
 * against the operator's *arr, which is the cost this interval was chosen to avoid.
 *
 * `placeholderData` keeps the previous page on screen while the next loads, the same reason
 * `useLogsQuery` carries it: without it the table unmounts to "Loading…" on every page step and every
 * poll, which makes the paging controls jump under the pointer once every five minutes.
 */
function sectionOptions(visible: boolean) {
  return {
    refetchInterval: visible ? LIBRARY_POLL_INTERVAL_MS : (false as const),
    placeholderData: keepPreviousData,
  };
}

/**
 * GET /api/admin/arr/{kind}/queue.
 *
 * ADMIN-GATED, so `needsAdminKey()` matches the `/api/admin/` prefix and `apiFetch` attaches
 * `X-Admin-Api-Key` by PATH PREFIX — this module never names the key itself. The *arr's own key never
 * reaches the browser at all: the server holds it, spends it outbound, and answers with a projection
 * that has no key-shaped member and no field for the address that was called.
 *
 * The `kind` is in the query key so Sonarr's and Radarr's queues cache separately; without it the two
 * tabs would serve each other's rows.
 */
export function useArrQueueQuery(kind: ArrKind, page: number, visible: boolean) {
  return useQuery({
    queryKey: ['admin', 'arr', kind, 'queue', page],
    queryFn: () => apiFetch<ArrSectionEnvelope<ArrQueueItem>>(buildQueueQuery(kind, page)),
    ...sectionOptions(visible),
  });
}

/**
 * GET /api/admin/arr/sonarr/series.
 *
 * Its own hook rather than a generic over the row type, because the two library sections serve
 * genuinely different rows (`ArrSeriesItem` has season and episode counts; `ArrMovieItem` has
 * `hasFile`) and a shared hook would have to be generic in exactly the place the caller already knows
 * the answer. The shared part — interval, visibility gating, placeholder — is `sectionOptions`.
 */
export function useSonarrSeriesQuery(query: string, page: number, visible: boolean) {
  return useQuery({
    // Every filter that reaches the URL is in the key: a filter that changes the URL but not the key
    // serves the previous search's cached rows back and looks like a search that found nothing new.
    queryKey: ['admin', 'arr', 'sonarr', 'series', query.trim(), page],
    queryFn: () =>
      apiFetch<ArrSectionEnvelope<ArrSeriesItem>>(buildLibraryQuery('sonarr', query, page)),
    ...sectionOptions(visible),
  });
}

/** GET /api/admin/arr/radarr/movies. The counterpart to `useSonarrSeriesQuery`. */
export function useRadarrMoviesQuery(query: string, page: number, visible: boolean) {
  return useQuery({
    queryKey: ['admin', 'arr', 'radarr', 'movies', query.trim(), page],
    queryFn: () =>
      apiFetch<ArrSectionEnvelope<ArrMovieItem>>(buildLibraryQuery('radarr', query, page)),
    ...sectionOptions(visible),
  });
}
