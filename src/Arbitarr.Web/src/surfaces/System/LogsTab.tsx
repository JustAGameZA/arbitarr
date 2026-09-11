import { useId, useState } from 'react';

import { QueryState } from '../QueryState';
import type { LogEntryResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './System.module.css';
import {
  LOG_LEVELS,
  LOG_PAGE_SIZE,
  useLogsQuery,
  type LogFilters,
  type LogLevelName,
} from './queries';

/**
 * The badge variant for a level.
 *
 * Reuses surface.module.css's existing badgeOk/Warn/Danger vocabulary rather than
 * introducing a per-level colour set: severity is the one thing an operator scans down
 * this column for, and a second colour idiom on the same page would make the Activity
 * surface's badges mean something different from these. Information is the plain badge —
 * colouring the normal case would leave nothing for the abnormal one to stand out
 * against.
 *
 * An unrecognised level (the sink writes whatever LogLevel it was handed) falls back to
 * the plain badge and still renders its own text, rather than being dropped or coerced.
 */
function levelBadgeClass(level: string): string {
  switch (level.toLowerCase()) {
    case 'critical':
    case 'error':
      return `${styles.badge} ${styles.badgeDanger}`;
    case 'warning':
      return `${styles.badge} ${styles.badgeWarn}`;
    default:
      return styles.badge;
  }
}

/**
 * Renders a log row's instant unambiguously (AC9).
 *
 * Deliberately identical in behaviour to Activity's Timestamp: local time for the
 * viewer's own clock, the zone name alongside it so the string is not ambiguous between
 * two readers, and the server's exact ISO instant kept in `title` for comparison against
 * a `docker logs` line. Not shared with that copy because the two surfaces are otherwise
 * independent and a shared helper would be a component-library seam this codebase has
 * not chosen to open; if a third caller appears, that is the time to lift it.
 */
function LogTimestamp({ value }: { value: string }) {
  const parsed = new Date(value);

  if (Number.isNaN(parsed.getTime())) {
    // Never silently blank a malformed timestamp: the raw value makes a server-side
    // format change visible instead of looking like a missing row.
    return <span title={value}>{value}</span>;
  }

  return (
    <time dateTime={value} title={value} className={local.logTime}>
      {parsed.toLocaleString(undefined, { timeZoneName: 'short' })}
    </time>
  );
}

/**
 * One log row, with its exception behind a disclosure.
 *
 * <details> rather than a click handler and a piece of state: it is keyboard-operable and
 * screen-reader-announced for free, and an expanded row survives a re-render without this
 * component having to track which ids are open. Rows without an exception render no
 * disclosure at all -- an empty expander on most rows would be noise on the majority
 * case.
 */
function LogRow({ entry }: { entry: LogEntryResponse }) {
  return (
    <tr>
      <td>
        <LogTimestamp value={entry.time} />
      </td>
      <td>
        <span className={levelBadgeClass(entry.level)}>{entry.level}</span>
      </td>
      <td className={local.logLogger}>{entry.logger}</td>
      <td>
        <span className={local.logMessage}>{entry.message}</span>
        {entry.exception !== null && (
          <details className={local.logException}>
            {/* The type name is the summary because it is what an operator recognises at
                a glance ("another SqliteException"); the stack trace underneath is the
                part worth hiding. */}
            <summary>{entry.exceptionType ?? 'Exception'}</summary>
            <pre className={local.logExceptionText}>{entry.exception}</pre>
          </details>
        )}
      </td>
    </tr>
  );
}

function LogsTable({ entries }: { entries: LogEntryResponse[] }) {
  if (entries.length === 0) {
    return (
      // #52's empty-state rule, and plan §5: the zero-rows case renders THIS, never "0".
      // It names what would fill the table -- and, because a filter is the likeliest
      // reason an operator sees this on a running system, says so rather than implying
      // the log store is empty.
      <p className={styles.empty}>
        No log entries match these filters. Entries appear here as the service logs at
        Information and above; widen the level, clear the logger filter, or clear the
        message filter to see more.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Time</th>
            <th>Level</th>
            <th>Logger</th>
            <th>Message</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry) => (
            <LogRow key={entry.id} entry={entry} />
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The Logs tab (#65, plan §4.6) -- application logs, filterable by level and logger.
 *
 * Two panels, matching the Activity surface's shape: the filter controls above, the table
 * below. The filter list of loggers comes back with each page of rows rather than from
 * its own query, so the <select> can never render empty beside a table already showing
 * rows from those loggers -- see LogsEndpoint's own note on why the server sends it that
 * way.
 */
export function LogsTab() {
  // Defaults to Warning rather than "All levels": LogStore matches Level as an EXACT,
  // case-insensitive string (see queries.ts's LOG_LEVELS note) with no minimum-severity
  // semantic, so a "Warning and above" default is not achievable without a backend
  // change out of scope for this PR (arb-kz8) -- filed as a follow-up. Exact "Warning" is
  // the closest useful default: an operator opening the tab lands on the level that
  // usually needs attention instead of the full firehose, and "All levels" stays one
  // click away.
  const [filters, setFilters] = useState<LogFilters>({ level: 'Warning', logger: '' });

  // Client-side only: the endpoint has no message/text query parameter (checked against
  // LogsEndpoint.cs), so this narrows only the rows already on the current page rather
  // than searching the whole store. Wiring a server-side search parameter is a follow-up,
  // noted in the commit body.
  const [messageFilter, setMessageFilter] = useState('');

  // 1-based, matching the server's own page numbering rather than translating at the
  // boundary. Offset paging, unlike Activity's cursor: see LogsResponse's note on why the
  // two stores are paged differently.
  const [page, setPage] = useState(1);

  const logs = useLogsQuery(filters, page);

  const levelFilterId = useId();
  const loggerFilterId = useId();
  const messageFilterId = useId();

  // Changing a filter resets to page 1: page 3 of an unfiltered store is a different set
  // of rows from page 3 of a filtered one, and staying there would show an operator a
  // page they never asked for -- or an empty one past the new end.
  const applyFilters = (next: Partial<LogFilters>) => {
    setFilters((current) => ({ ...current, ...next }));
    setPage(1);
  };

  // Derived from the server's OWN page/pageSize, not from the request: those are the
  // clamped values it actually served, so a clamp stays visible instead of silently
  // disagreeing with the rows on screen.
  const total = logs.data?.total ?? 0;
  const servedPageSize = logs.data?.pageSize ?? LOG_PAGE_SIZE;
  const pageCount = Math.max(1, Math.ceil(total / servedPageSize));
  const servedPage = logs.data?.page ?? page;
  const loggers = logs.data?.loggers ?? [];

  // Filters only the current page's rows (see messageFilter's declaration above for why
  // this cannot be a server-side query yet). Case-insensitive substring match against the
  // message text, mirroring the logger filter's own substring semantics.
  const trimmedMessageFilter = messageFilter.trim().toLowerCase();
  const visibleEntries = logs.data
    ? trimmedMessageFilter === ''
      ? logs.data.entries
      : logs.data.entries.filter((entry) =>
          entry.message.toLowerCase().includes(trimmedMessageFilter),
        )
    : [];

  return (
    <>
      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Filter</h2>
        <div className={styles.panelBody}>
          <div className={styles.form}>
            <label className={styles.field} htmlFor={levelFilterId}>
              Level
              <select
                id={levelFilterId}
                className={styles.select}
                value={filters.level}
                onChange={(event) =>
                  applyFilters({ level: event.target.value as LogLevelName | 'all' })
                }
              >
                <option value="all">All levels</option>
                {LOG_LEVELS.map((level) => (
                  <option key={level} value={level}>
                    {level}
                  </option>
                ))}
              </select>
            </label>

            <label className={styles.field} htmlFor={loggerFilterId}>
              Logger
              <select
                id={loggerFilterId}
                className={styles.select}
                value={filters.logger}
                onChange={(event) => applyFilters({ logger: event.target.value })}
              >
                <option value="">All loggers</option>
                {loggers.map((logger) => (
                  <option key={logger} value={logger}>
                    {logger}
                  </option>
                ))}
              </select>
            </label>

            <label className={styles.field} htmlFor={messageFilterId}>
              Message
              <input
                id={messageFilterId}
                type="text"
                className={styles.input}
                placeholder="Filter messages… (filters this page)"
                value={messageFilter}
                onChange={(event) => setMessageFilter(event.target.value)}
              />
            </label>
          </div>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Logs</h2>
        <div className={styles.panelBody}>
          {/* The render prop's own `data` argument is intentionally unused here:
              `visibleEntries` (declared above, with its own `logs.data ? … : []`
              fallback) already narrows to the message filter and replaces it. */}
          <QueryState isPending={logs.isPending} error={logs.error} data={logs.data}>
            {() => (
              <>
                <LogsTable entries={visibleEntries} />

                {trimmedMessageFilter !== '' && (
                  <p className={styles.muted}>
                    Showing {visibleEntries.length} of {logs.data?.entries.length ?? 0} rows on
                    this page match the message filter.
                  </p>
                )}

                {/* Hidden entirely on a single page: a disabled Next under a short table
                    is noise. Same rule the Activity surface applies. total/pageCount are
                    always derived from the server's unfiltered result set, never from
                    visibleEntries -- the pager describes what the server served, not what
                    the message filter narrowed it to. */}
                {total > servedPageSize && (
                  <div className={local.paging}>
                    <button
                      type="button"
                      className={styles.buttonSecondary}
                      disabled={servedPage <= 1}
                      onClick={() => setPage((current) => Math.max(1, current - 1))}
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
                      onClick={() => setPage((current) => current + 1)}
                    >
                      Next
                    </button>
                  </div>
                )}
              </>
            )}
          </QueryState>
        </div>
      </section>
    </>
  );
}
