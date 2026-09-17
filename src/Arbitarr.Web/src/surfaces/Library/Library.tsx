import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';

import { PageHeader } from '../../components/shell/PageHeader';
import { TabPanel, Tabs, useTabs } from '../../components/Tabs';
import { PageToolbar, PageToolbarInput, PageToolbarSection } from '../../components/shell/toolbar';
import { formatBytes } from '../../format';
import { QueryState } from '../QueryState';
import styles from '../surface.module.css';
import {
  LIBRARY_PAGE_SIZE,
  useArrQueueQuery,
  useDocumentVisible,
  useRadarrMoviesQuery,
  useSonarrSeriesQuery,
  type ArrKind,
} from './queries';
import type { ArrMovieItem, ArrQueueItem, ArrSectionEnvelope, ArrSeriesItem } from './types';

type TabId = 'sonarr-queue' | 'sonarr-series' | 'radarr-queue' | 'radarr-movies';

const TAB_ITEMS = [
  { id: 'sonarr-queue', label: 'Sonarr queue' },
  { id: 'sonarr-series', label: 'Sonarr series' },
  { id: 'radarr-queue', label: 'Radarr queue' },
  { id: 'radarr-movies', label: 'Radarr movies' },
] as const satisfies readonly { id: TabId; label: string }[];

/**
 * The badge variant for a queue row's `trackedDownloadStatus`.
 *
 * Maps upstream's `ok` / `warning` / `error` onto `surface.module.css`'s existing
 * badgeOk/Warn/Danger vocabulary rather than introducing a per-status colour set — the same
 * reasoning `LogsTab`'s `levelBadgeClass` records: a second colour idiom on the same app would make
 * these badges mean something different from the ones an operator has already learned.
 *
 * An unrecognised value falls back to the PLAIN badge and still renders its own text, rather than
 * being dropped or coerced into one of the three. Upstream is free to add a fourth word, and
 * inventing a severity for one nobody has seen would be a claim the data does not support.
 */
function trackedStatusBadgeClass(status: string): string {
  switch (status.toLowerCase()) {
    case 'ok':
      return `${styles.badge} ${styles.badgeOk}`;
    case 'warning':
      return `${styles.badge} ${styles.badgeWarn}`;
    case 'error':
      return `${styles.badge} ${styles.badgeDanger}`;
    default:
      return styles.badge;
  }
}

/** A cell whose value the server may not have sent. The em-dash convention is `format.ts`'s. */
function Text({ value }: { value: string | null }) {
  return <>{value === null || value === '' ? '—' : value}</>;
}

/**
 * The action an operator can take about a section that is not configured.
 *
 * <b>THE LINK IS TO ARBITARR'S OWN SETTINGS, NEVER TO THE *ARR.</b> That distinction is a security
 * property and not a convenience: the operator's Sonarr/Radarr base URL is sensitive, the envelope
 * deliberately has no member carrying it, and a "open Sonarr" affordance would have to reconstruct
 * or display an address this surface must never learn. `/settings#sonarr` and `/settings#radarr` are
 * real anchors on Arbitarr's own Settings route (`Settings.tsx`'s section ids).
 */
function NotConfiguredNotice({ kind, message }: { kind: ArrKind; message: string }) {
  return (
    <div>
      {/* The server's own wording, verbatim -- it already names what is missing and where. The
          LINK is the only thing added, because a sentence telling an operator to go and configure
          something is more useful next to the way to get there. */}
      <p className={styles.empty}>{message}</p>
      <Link className={styles.buttonSecondary} to={`/settings#${kind}`}>
        Open {kind === 'sonarr' ? 'Sonarr' : 'Radarr'} settings
      </Link>
    </div>
  );
}

/**
 * One section's verdict, or its rows.
 *
 * <b>THERE IS NO `switch` WITH A DEFAULT THAT INVENTS TEXT</b>, and that is the whole design. Only
 * two statuses are recognised here: `Ok`, which has rows, and `NotConfigured`, which has an action.
 * EVERY other value — the three remaining members, and equally a sixth the server grows later or a
 * string this client has never heard of — renders the server's own `message` verbatim in the error
 * treatment. The server chooses that wording from a closed enum and can interpolate nothing upstream
 * said, so passing it through is both safe and the only answer that stays correct for a status the
 * client does not know. A default branch reading "something went wrong" would replace a specific,
 * accurate sentence with a vaguer one precisely when the client understands least.
 */
function SectionBody<T>({
  envelope,
  children,
  kind,
}: {
  envelope: ArrSectionEnvelope<T>;
  kind: ArrKind;
  children: (records: T[]) => React.ReactNode;
}) {
  if (envelope.status === 'NotConfigured') {
    return <NotConfiguredNotice kind={kind} message={envelope.message} />;
  }

  if (envelope.status !== 'Ok') {
    // The same treatment QueryState gives a failed REQUEST, reached deliberately from a successful
    // one: the transport is always 200 and the verdict is `status`, so this branch is where a
    // reachable Arbitarr reports an unreachable *arr. role="alert" matches QueryState's own error
    // branch so the two read identically to a screen reader.
    return (
      <p className={styles.error} role="alert">
        {envelope.message}
      </p>
    );
  }

  return <>{children(envelope.records)}</>;
}

/** The pager. Derived from the SERVER's own page/pageSize, never from what was requested. */
function Pager({
  envelope,
  onPage,
}: {
  envelope: ArrSectionEnvelope<unknown>;
  onPage: (next: number) => void;
}) {
  // The served values, not the requested ones: these are what the server actually answered with
  // after its clamp, so a clamp stays visible instead of silently disagreeing with the rows.
  const servedPageSize = envelope.pageSize > 0 ? envelope.pageSize : LIBRARY_PAGE_SIZE;
  const pageCount = Math.max(1, Math.ceil(envelope.totalRecords / servedPageSize));
  const servedPage = envelope.page;

  // Hidden entirely on a single page: a disabled Next under a short table is noise. Same rule the
  // Logs and Activity surfaces apply.
  if (envelope.totalRecords <= servedPageSize) {
    return null;
  }

  return (
    <div className={styles.facts}>
      <button
        type="button"
        className={styles.buttonSecondary}
        disabled={servedPage <= 1}
        onClick={() => onPage(Math.max(1, servedPage - 1))}
      >
        Previous
      </button>
      <span className={styles.muted}>
        Page {servedPage} of {pageCount}
      </span>
      <button
        type="button"
        className={styles.buttonSecondary}
        disabled={servedPage >= pageCount}
        onClick={() => onPage(servedPage + 1)}
      >
        Next
      </button>
    </div>
  );
}

function QueueTable({ records }: { records: ArrQueueItem[] }) {
  if (records.length === 0) {
    // #52's empty-state rule: say what would FILL it, not merely that it is empty.
    return (
      <p className={styles.empty}>
        Nothing is downloading. Grabs that Sonarr or Radarr sends to a download client appear here
        while they are in progress.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Title</th>
            <th>Status</th>
            <th>Size</th>
            <th>Remaining</th>
            <th>Time left</th>
            <th>Client</th>
            <th>Indexer</th>
          </tr>
        </thead>
        <tbody>
          {records.map((record, index) => (
            // The *arr's own queue id (arb-6l9b.6), which is stable across a poll in a way a title
            // is not. The index fallback is only for a record upstream sent without one: it is a
            // worse key, but a duplicated `null` key would be worse still.
            <tr key={record.id ?? `row-${index}`}>
              <td>
                <Text value={record.title} />
              </td>
              <td>
                {record.trackedDownloadStatus !== null && (
                  <span className={trackedStatusBadgeClass(record.trackedDownloadStatus)}>
                    {record.trackedDownloadStatus}
                  </span>
                )}{' '}
                <Text value={record.status} />
              </td>
              <td>{formatBytes(record.size)}</td>
              <td>{formatBytes(record.sizeLeft)}</td>
              {/* Upstream's own string, rendered verbatim: re-deriving a duration here would be a
                  second convention that can disagree with what the operator sees in Sonarr. */}
              <td>
                <Text value={record.timeLeft} />
              </td>
              <td>
                <Text value={record.downloadClient} />
              </td>
              <td>
                <Text value={record.indexer} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function SeriesTable({ records }: { records: ArrSeriesItem[] }) {
  if (records.length === 0) {
    return (
      <p className={styles.empty}>
        No series match this search. Series that Sonarr is tracking appear here; clear the filter to
        see the whole library.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Title</th>
            <th>Year</th>
            <th>Status</th>
            <th>Monitored</th>
            <th>Episodes</th>
            <th>Size</th>
            <th>Network</th>
          </tr>
        </thead>
        <tbody>
          {records.map((record) => (
            <tr key={record.id}>
              <td>
                <Text value={record.title} />
              </td>
              <td>{record.year ?? '—'}</td>
              <td>
                <Text value={record.status} />
              </td>
              <td>{record.monitored ? 'Yes' : 'No'}</td>
              {/* Files over episodes rather than a percentage: "12 of 13" says which one is
                  missing is a countable thing, where "92%" rounds the last gap away. */}
              <td>
                {record.episodeFileCount} of {record.episodeCount}
              </td>
              <td>{formatBytes(record.sizeOnDisk)}</td>
              <td>
                <Text value={record.network} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function MoviesTable({ records }: { records: ArrMovieItem[] }) {
  if (records.length === 0) {
    return (
      <p className={styles.empty}>
        No movies match this search. Movies that Radarr is tracking appear here; clear the filter to
        see the whole library.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Title</th>
            <th>Year</th>
            <th>Status</th>
            <th>Monitored</th>
            <th>Downloaded</th>
            <th>Size</th>
          </tr>
        </thead>
        <tbody>
          {records.map((record) => (
            <tr key={record.id}>
              <td>
                <Text value={record.title} />
              </td>
              <td>{record.year ?? '—'}</td>
              <td>
                <Text value={record.status} />
              </td>
              <td>{record.monitored ? 'Yes' : 'No'}</td>
              <td>{record.hasFile ? 'Yes' : 'No'}</td>
              <td>{formatBytes(record.sizeOnDisk)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * One queue tab: the section's verdict, its rows, and its pager.
 *
 * Its own component rather than an inline branch so that INACTIVE TABS UNMOUNT. That is what makes
 * the polling rule hold by construction: an unmounted query has no interval, so only the tab an
 * operator is looking at reads from their *arr. The alternative — rendering all four and hiding
 * three — would quadruple the load this surface puts on somebody else's server for three tables
 * nobody can see.
 */
function QueueTab({ kind }: { kind: ArrKind }) {
  const visible = useDocumentVisible();
  const [page, setPage] = useState(1);
  const queue = useArrQueueQuery(kind, page, visible);

  return (
    <section className={styles.panel}>
      <div className={styles.panelBody}>
        <RefreshButton onRefresh={() => void queue.refetch()} busy={queue.isFetching} />
        <QueryState isPending={queue.isPending} error={queue.error} data={queue.data}>
          {(data) => (
            <SectionBody envelope={data} kind={kind}>
              {(records) => (
                <>
                  <QueueTable records={records} />
                  <Pager envelope={data} onPage={setPage} />
                </>
              )}
            </SectionBody>
          )}
        </QueryState>
      </div>
    </section>
  );
}

/**
 * The manual Refresh control (owner ruling, 2026-09-17).
 *
 * It exists because the automatic cadence is five minutes: an operator who has just started a
 * download should not have to wait out an interval chosen to be gentle on their *arr, and the
 * alternative to this button is a faster interval that every operator pays for.
 *
 * `refetch()` and not a state bump: it asks React Query to re-run the CURRENT key, so a refresh on
 * page 3 of a filtered library re-reads page 3 of that filter rather than resetting anything the
 * operator chose.
 */
function RefreshButton({ onRefresh, busy }: { onRefresh: () => void; busy: boolean }) {
  return (
    <button type="button" className={styles.buttonSecondary} onClick={onRefresh} disabled={busy}>
      Refresh
    </button>
  );
}

/**
 * One library tab: a filter box, the verdict, the rows, and the pager.
 *
 * Generic over the row type because the two library sections differ ONLY in their table and their
 * query — everything else (the debounce, the page reset, the verdict handling, the pager) is one
 * behaviour that would otherwise exist as two copies free to drift.
 */
function LibraryTab<T>({
  kind,
  label,
  useSectionQuery,
  children,
}: {
  kind: ArrKind;
  label: string;
  /**
   * The section's query hook. Named `use…` so the lint rule that enforces the rules of hooks still
   * sees a hook call below rather than an opaque callback — it is invoked unconditionally, once per
   * render, exactly as if it were written inline.
   */
  useSectionQuery: (
    q: string,
    page: number,
    visible: boolean,
  ) => {
    data: ArrSectionEnvelope<T> | undefined;
    error: unknown;
    isPending: boolean;
    isFetching: boolean;
    refetch: () => unknown;
  };
  children: (records: T[]) => React.ReactNode;
}) {
  const visible = useDocumentVisible();
  const [page, setPage] = useState(1);
  const [filter, setFilter] = useState('');

  // The <input> itself, kept separate from the committed filter: the box has to react to every
  // keystroke, but the value that reaches the query -- and therefore the request and the query key
  // -- is debounced, so typing a word issues one admin round trip instead of one per character.
  const [filterInput, setFilterInput] = useState('');

  // The filter the debounce has already pushed into `filter`. Seeded with the initial value so the
  // effect's MOUNT run finds nothing to commit -- see its note below.
  const committedFilter = useRef('');

  const section = useSectionQuery(filter, page, visible);

  // ~250ms: long enough that a normal typing cadence produces one request per pause rather
  // than one per keystroke, short enough that the delay after the last character is not
  // itself noticeable. Debounces the VALUE, not the keystroke handler, so the input stays
  // controlled and immediate -- only what reaches `filters` (and so the query key and the
  // page-1 reset) lags behind.
  //
  // The timer commits ONLY when the debounced value actually differs from the message
  // already committed, and the page reset lives inside that guard (arb-6l13). The effect
  // also runs on MOUNT, where messageInput still equals filters.message: without the guard
  // that mount run would fire setPage(1) 250ms after the tab opened, silently throwing an
  // operator who clicked Next inside that window back to page 1. It is a real bug and not
  // merely a test artifact -- it surfaced as an order-dependent failure only because the
  // paging test usually finishes before the 250ms deadline and, under full-suite load,
  // does not. The guard compares the VALUE rather than counting runs, which is what makes
  // it hold: a first-run flag would close the mount run alone, while any later re-arm
  // carrying an unchanged message would reset the page just the same.
  useEffect(() => {
    const timer = setTimeout(() => {
      // Read the committed message from a ref rather than from `filters`, so the guard
      // does not put `filters` in this effect's dependency list -- doing that would
      // re-arm the timer on every filter change and reintroduce the same late reset
      // from a different direction. The updater itself stays pure.
      if (committedFilter.current === filterInput) {
        return;
      }

      committedFilter.current = filterInput;
      setFilter(filterInput);
      // A genuinely new search: page 3 of the old result set is not page 3 of the new
      // one, so the reset belongs with the change that invalidates the page.
      setPage(1);
    }, 250);

    return () => clearTimeout(timer);
  }, [filterInput]);

  return (
    <>
      {/* The toolbar is this TAB PANEL's first child, not a sibling of a PageHeader: Library owns
          the route's single <h1> and this tab has no header of its own. The label names the DATA
          SET rather than the route, because a second toolbar elsewhere on the page must not be
          indistinguishable from this one in the accessibility tree. */}
      <PageToolbar label={`${label} filters`}>
        <PageToolbarSection align="end">
          <PageToolbarInput
            label="Search"
            value={filterInput}
            placeholder="Filter by title…"
            onChange={setFilterInput}
          />
        </PageToolbarSection>
      </PageToolbar>

      <section className={styles.panel}>
        <div className={styles.panelBody}>
          <RefreshButton onRefresh={() => void section.refetch()} busy={section.isFetching} />
          <QueryState isPending={section.isPending} error={section.error} data={section.data}>
            {(data) => (
              <SectionBody envelope={data} kind={kind}>
                {(records) => (
                  <>
                    {children(records)}
                    <Pager envelope={data} onPage={setPage} />
                  </>
                )}
              </SectionBody>
            )}
          </QueryState>
        </div>
      </section>
    </>
  );
}

/**
 * Library (arb-6l9b.6) — the Sonarr and Radarr queues and libraries, one tab each.
 *
 * <b>THE SERVER'S VERDICT IS RENDERED PER SECTION, AND THE SECTIONS ARE INDEPENDENT.</b> Each tab
 * owns its own query, so a Sonarr that is unreachable says so in the Sonarr tabs and changes nothing
 * about the Radarr ones. That independence is the reason there is no single page-level error state:
 * one blanked screen would hide three sections that are working to report one that is not.
 *
 * Inactive panels UNMOUNT (`tabs.activeId === ...` rather than a hidden class), which is what keeps
 * the polling promise: only the visible tab holds a query, so only it reads from the operator's
 * *arr. Paired with `useDocumentVisible`, a backgrounded browser tab polls nothing at all.
 */
export default function LibraryPage() {
  const tabs = useTabs<TabId>('sonarr-queue');

  return (
    <>
      <PageHeader title="Library" />
      <Tabs label="Library sections" items={TAB_ITEMS} tabs={tabs} />
      {/* The `key` remounts the panel on every tab switch, which is what resets page and filter to
          their defaults: page 3 of Sonarr's series is not a meaningful position in Radarr's movies,
          and carrying it across would show an operator a page they never asked for. */}
      <TabPanel id={tabs.activeId} tabs={tabs}>
        {tabs.activeId === 'sonarr-queue' && <QueueTab key="sonarr-queue" kind="sonarr" />}
        {tabs.activeId === 'radarr-queue' && <QueueTab key="radarr-queue" kind="radarr" />}
        {tabs.activeId === 'sonarr-series' && (
          <LibraryTab<ArrSeriesItem>
            key="sonarr-series"
            kind="sonarr"
            label="Sonarr series"
            useSectionQuery={useSonarrSeriesQuery}
          >
            {(records) => <SeriesTable records={records} />}
          </LibraryTab>
        )}
        {tabs.activeId === 'radarr-movies' && (
          <LibraryTab<ArrMovieItem>
            key="radarr-movies"
            kind="radarr"
            label="Radarr movies"
            useSectionQuery={useRadarrMoviesQuery}
          >
            {(records) => <MoviesTable records={records} />}
          </LibraryTab>
        )}
      </TabPanel>
    </>
  );
}
