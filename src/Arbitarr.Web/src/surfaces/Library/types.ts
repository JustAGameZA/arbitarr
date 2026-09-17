/**
 * The Library surface's wire shapes (arb-6l9b.6).
 *
 * Local to this surface rather than added to `api/types.ts`: these four sections are the only
 * readers of these shapes, and the envelope below is generic in a way the shared file's flat
 * response records are not.
 */

/**
 * The one envelope every *arr section answers with — `ArrSectionEnvelope<T>` on the server.
 *
 * <b>THE TRANSPORT IS ALWAYS 200 AND THE VERDICT IS `status`.</b> An unconfigured or unreachable
 * *arr is not an error in the admin request that reported it, so nothing here goes through
 * QueryState's error branch: a failed section is a SUCCESSFUL query carrying a non-Ok status. The
 * error branch stays reserved for the request itself failing (a 401, a 503, the network), which is
 * a different fact and has a different fix.
 */
export interface ArrSectionEnvelope<T> {
  /**
   * `ArrSectionStatus.ToString()` — a stable string, never the enum's ordinal, so the wire
   * contract does not depend on member order. Typed as the union WIDENED WITH `string` on purpose:
   * a server that grows a sixth member must not be a type error here, because the renderer's
   * answer for an unrecognised value is to show `message` verbatim rather than to invent text.
   */
  status: ArrSectionStatusName | (string & {});
  /**
   * The server's own fixed wording for `status`, chosen from the closed enum and the instance name
   * alone. Rendered VERBATIM and never parsed: it is the only text this surface has for a status
   * it does not recognise, and re-wording a status the client does know would put two answers to
   * one question on screen.
   */
  message: string;
  /** The page served, after the server's clamp — not the page that was asked for. */
  page: number;
  /** The page size served, after the server's clamp. */
  pageSize: number;
  /** The total across all pages; 0 for every non-Ok status. */
  totalRecords: number;
  /** The projected rows; empty for every non-Ok status. */
  records: T[];
}

/**
 * The five members `ArrSectionStatus` declares.
 *
 * FIVE, not four: `NotConfigured` exists alongside the three failure modes because this surface is
 * read on a schedule against an instance that may never have been configured. Only `NotConfigured`
 * and `Ok` are special-cased in the renderer — the first because it has an action (finish
 * configuring), the second because it has rows. The rest, and anything not in this union at all,
 * share one treatment: the server's `message`.
 */
export type ArrSectionStatusName =
  | 'NotConfigured'
  | 'Ok'
  | 'Unreachable'
  | 'AuthenticationFailed'
  | 'UnexpectedResponse';

/**
 * One queue row.
 *
 * No path-shaped member, by construction on the server (`ArrQueueItem`'s own doc): the projection
 * enumerates what it serves, so `outputPath`, `path`, `rootFolderPath` and `folderName` are absent
 * rather than filtered. Do not add one here either — a field this interface declares is a field
 * somebody intended to render.
 */
export interface ArrQueueItem {
  /**
   * The *arr's own queue id (arb-6l9b.6), used as the row key. Nullable because the server carries
   * it nullable: a record upstream omits it for has no id rather than a defaulted 0.
   */
  id: number | null;
  title: string | null;
  status: string | null;
  /** `ok` / `warning` / `error` upstream, mapped onto the shared badge trio. */
  trackedDownloadStatus: string | null;
  trackedDownloadState: string | null;
  /** Bytes. Absent renders the em-dash through `formatBytes`. */
  size: number | null;
  /** Bytes remaining. */
  sizeLeft: number | null;
  /**
   * An OPAQUE string from upstream, rendered verbatim. Not parsed into a duration: it is the
   * *arr's own formatting and re-deriving it here would be a second convention that can disagree
   * with what the operator sees in Sonarr itself.
   */
  timeLeft: string | null;
  estimatedCompletionTime: string | null;
  protocol: string | null;
  downloadClient: string | null;
  indexer: string | null;
  /** Upstream's `{title, messages[]}` pairs, already flattened to lines by the server. */
  statusMessages: string[];
  errorMessage: string | null;
}

/** One series row. No poster and no path — see `ArrSeriesItem`'s doc for why both are structural. */
export interface ArrSeriesItem {
  id: number;
  title: string | null;
  year: number | null;
  tvdbId: number | null;
  status: string | null;
  monitored: boolean;
  seasonCount: number;
  episodeFileCount: number;
  episodeCount: number;
  /** Bytes. */
  sizeOnDisk: number | null;
  network: string | null;
}

/** One movie row. Same projection contract as `ArrSeriesItem`. */
export interface ArrMovieItem {
  id: number;
  title: string | null;
  year: number | null;
  tmdbId: number | null;
  monitored: boolean;
  hasFile: boolean;
  /** Bytes. */
  sizeOnDisk: number | null;
  status: string | null;
}
