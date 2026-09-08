import { keepPreviousData, useQuery } from '@tanstack/react-query';

import { apiFetch, apiFetchBlob } from '../../api/client';
import type {
  BackupStatusResponse,
  BuildInfoResponse,
  LogsResponse,
  ObservabilityResponse,
  RestoreResponse,
  StalenessEnvelopeResponse,
} from '../../api/types';

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

/**
 * The levels the filter offers, newest-first in severity order.
 *
 * These are Microsoft.Extensions.Logging.LogLevel NAMES and must match the server's
 * spelling exactly — LogStore matches `Level = $level COLLATE NOCASE`, an EXACT
 * comparison and not a prefix, so "Info" would silently match nothing rather than
 * erroring. Trace and Debug are absent because SqliteLoggerProvider's minimum level is
 * Information (plan §4.3): offering a filter that can only ever return zero rows would
 * teach the operator that the log store is broken.
 */
export const LOG_LEVELS = ['Information', 'Warning', 'Error', 'Critical'] as const;

export type LogLevelName = (typeof LOG_LEVELS)[number];

export interface LogFilters {
  level: LogLevelName | 'all';
  /** Substring match against the logger category; empty means all loggers. */
  logger: string;
}

/** Rows per page. Below LogStore.MaxPageSize (200), so the server never clamps this. */
export const LOG_PAGE_SIZE = 50;

/**
 * Builds the query string for GET /api/admin/logs.
 *
 * Exported for its own test, exactly as buildActivityQuery is: the filter-to-URL mapping
 * is the part that breaks silently. A dropped `level` widens the query to everything
 * while the table still renders plausible rows, which no render-level assertion would
 * catch.
 *
 * An empty or whitespace-only logger is OMITTED rather than sent as an empty string. The
 * store treats a whitespace filter as absent anyway, so sending one would work by
 * accident; leaving it out keeps the request honest about what was asked.
 */
export function buildLogsQuery(filters: LogFilters, page: number): string {
  const params = new URLSearchParams();

  if (filters.level !== 'all') {
    params.set('level', filters.level);
  }

  const logger = filters.logger.trim();
  if (logger !== '') {
    params.set('logger', logger);
  }

  if (page > 1) {
    params.set('page', String(page));
  }

  params.set('pageSize', String(LOG_PAGE_SIZE));

  return `/api/admin/logs?${params.toString()}`;
}

/**
 * One page of application logs.
 *
 * GET /api/admin/logs is ADMIN-GATED, so needsAdminKey() matches the /api/admin/ prefix
 * and apiFetch attaches X-Admin-Api-Key — the same treatment useObservabilityQuery gets.
 * That this read is gated while /api/activity is not is DELIBERATE and is not an
 * inconsistency to tidy up: LogsEndpoint.cs carries the full reasoning (raw application
 * logs are an unvetted surface — exception text, paths, internal names — where activity
 * events are a curated one). Do not "fix" it here by dropping the prefix.
 *
 * `page` is part of the query key so each page caches separately, matching
 * useActivityQuery's treatment of its cursor; without it, paging would overwrite a single
 * cache entry and re-render the same rows.
 *
 * placeholderData keeps the previous page on screen while the next one loads. Without it
 * the table unmounts to "Loading…" on every page step and every filter change, which
 * makes the paging controls jump under the pointer.
 */
export function useLogsQuery(filters: LogFilters, page: number) {
  return useQuery({
    queryKey: ['admin', 'logs', filters.level, filters.logger.trim(), page],
    queryFn: () => apiFetch<LogsResponse>(buildLogsQuery(filters, page)),
    placeholderData: keepPreviousData,
  });
}

// --- Backup and restore (#56) --------------------------------------------

/** The word an operator types to confirm. Matches AdminBackupEndpoints.RestoreConfirmationWord. */
export const RESTORE_CONFIRMATION_WORD = 'RESTORE';

/**
 * GET /api/admin/backup/status.
 *
 * Admin-gated like every /api/admin/ route, so apiFetch attaches the header by path
 * prefix. Its own query rather than part of the Status tab's set, because the Backup tab
 * is the only thing that reads it and the tabs unmount when switched.
 */
export function useBackupStatusQuery() {
  return useQuery({
    queryKey: ['admin', 'backup', 'status'],
    queryFn: () => apiFetch<BackupStatusResponse>('/api/admin/backup/status'),
  });
}

/**
 * Downloads the backup archive and hands it to the browser to save.
 *
 * The two-step (fetch as a blob, then click a synthesised object-URL anchor) is not
 * ceremony: the archive contains the release-GUID secret and every source API key, so it
 * must be fetched with the X-Admin-Api-Key HEADER. A plain <a download href="..."> cannot
 * send a header, so making a direct link work would mean a credential in the URL — into
 * browser history, into any proxy's access log, and into the backend's own record of full
 * absolute URIs. See AdminBackupEndpoints' remarks and client.ts's needsAdminKey note.
 *
 * The object URL is revoked immediately after the click. It is a live handle to a
 * credential-bearing blob held in the page; leaving it un-revoked would keep those bytes
 * reachable from the document for as long as it stays open.
 */
export async function downloadBackupArchive(): Promise<string> {
  const { blob, fileName } = await apiFetchBlob('/api/admin/backup');

  const name = fileName ?? 'arbitarr-backup.zip';
  const objectUrl = URL.createObjectURL(blob);

  try {
    const anchor = document.createElement('a');
    anchor.href = objectUrl;
    anchor.download = name;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(objectUrl);
  }

  return name;
}

/**
 * POST /api/admin/restore — the destructive half.
 *
 * The confirmation word travels in the body rather than being checked only in the
 * browser: a client-side-only gate is a suggestion, and this is the last thing standing
 * in front of replacing the configuration database and the release-GUID secret.
 */
export async function restoreFromArchive(file: File): Promise<RestoreResponse> {
  const body = new FormData();
  body.append('archive', file);
  body.append('confirm', RESTORE_CONFIRMATION_WORD);

  // No Content-Type header: the browser must set the multipart boundary itself, and
  // apiFetch only defaults a JSON content type when none was given for a non-FormData body.
  return apiFetch<RestoreResponse>('/api/admin/restore', { method: 'POST', body });
}
