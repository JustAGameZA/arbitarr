import { useEffect, useRef, useState } from 'react';

import {
  PageToolbar,
  PageToolbarInput,
  PageToolbarMenu,
  PageToolbarMenuItem,
  PageToolbarSection,
} from '../../components/shell/toolbar';
import { QueryState } from '../QueryState';
import type { LogEntryResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './System.module.css';
import {
  LOG_LEVELS,
  LOG_PAGE_SIZE,
  useLogsQuery,
  type LogFilters,
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
 * `format.ts`'s `formatTimestamp`/`formatTimestampTitle` (arb-p94u) now hold this
 * exact string/local-time/zone-name reasoning for Activity, Dashboard, Suppressions,
 * Search and ApiKeys. This component keeps its own copy rather than delegating to
 * them because it returns MARKUP -- a `<time>` element with `dateTime`/`title`
 * attributes and no wrapping class, plus a `<span title>` fallback for a malformed
 * value -- not a plain string those callers render into a `<td>`. `formatTimestamp`
 * is a string formatter and is not a drop-in for a component with its own element
 * shape and fallback rendering; lifting this one too would mean inventing a second,
 * JSX-returning helper that no other caller needs yet.
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
 * The visible label for one level filter choice.
 *
 * "and above" is spelled out on every option rather than left implicit, because the
 * option the operator reads is the only place the minimum-severity semantic is
 * visible -- a bare "Warning" reads as exact, which is precisely the
 * misunderstanding that made the old exact filter hide Error and Critical. Critical
 * is the exception: nothing sits above it, so the suffix there would promise a
 * breadth that does not exist.
 *
 * Shared by the menu ITEMS and the menu's TRIGGER (arb-ajrv). When these were
 * <option> elements the label existed in one place by construction; a toolbar menu
 * states the active selection on its trigger too, so deriving both from this
 * function is what stops the trigger from drifting into the bare level name the
 * comment above exists to forbid.
 */
function levelOptionLabel(level: LogFilters['level']): string {
  if (level === 'all') {
    return 'All levels';
  }

  return level === LOG_LEVELS[LOG_LEVELS.length - 1] ? level : `${level} and above`;
}

/**
 * The visible label for one logger filter choice.
 *
 * The empty string is the unfiltered case on the wire, and "All loggers" is what it
 * reads as. Sharing this between the trigger and the item keeps the two spellings
 * from diverging the way the level labels could.
 */
function loggerOptionLabel(logger: string): string {
  return logger === '' ? 'All loggers' : logger;
}

/**
 * The Logs tab (#65, plan §4.6) -- application logs, filterable by level and logger.
 *
 * A toolbar row above the table, matching the Activity surface's shape (arb-ajrv; it was
 * a second `.panel` headed "Filter" until then). The filter list of loggers comes back
 * with each page of rows rather than from its own query, so the logger menu can never
 * render empty beside a table already showing rows from those loggers -- see
 * LogsEndpoint's own note on why the server sends it that way.
 */
export function LogsTab() {
  // Defaults to Warning, which the API applies as a MINIMUM severity (arb-pw7r), so this
  // lands the operator on Warning, Error and Critical rather than the full firehose --
  // with "All levels" one click away.
  const [filters, setFilters] = useState<LogFilters>({ level: 'Warning', logger: '', message: '' });

  // The Message <input> itself, kept separate from filters.message: the box has to react to
  // every keystroke, but the value that reaches useLogsQuery -- and therefore the request and
  // the query key -- is debounced, so typing a word issues one admin round trip instead of one
  // per character.
  const [messageInput, setMessageInput] = useState(filters.message);

  // The message the debounce has already pushed into `filters`. Seeded with the initial
  // message so the effect's MOUNT run finds nothing to commit -- see its note below.
  const committedMessage = useRef(filters.message);

  // 1-based, matching the server's own page numbering rather than translating at the
  // boundary. Offset paging, unlike Activity's cursor: see LogsResponse's note on why the
  // two stores are paged differently.
  const [page, setPage] = useState(1);

  const logs = useLogsQuery(filters, page);

  // Changing a filter resets to page 1: page 3 of an unfiltered store is a different set
  // of rows from page 3 of a filtered one, and staying there would show an operator a
  // page they never asked for -- or an empty one past the new end.
  const applyFilters = (next: Partial<LogFilters>) => {
    setFilters((current) => ({ ...current, ...next }));
    setPage(1);
  };

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
      if (committedMessage.current === messageInput) {
        return;
      }

      committedMessage.current = messageInput;
      setFilters((current) => ({ ...current, message: messageInput }));
      // A genuinely new search: page 3 of the old result set is not page 3 of the new
      // one, so the reset belongs with the change that invalidates the page.
      setPage(1);
    }, 250);

    return () => clearTimeout(timer);
  }, [messageInput]);

  // Derived from the server's OWN page/pageSize, not from the request: those are the
  // clamped values it actually served, so a clamp stays visible instead of silently
  // disagreeing with the rows on screen.
  const total = logs.data?.total ?? 0;
  const servedPageSize = logs.data?.pageSize ?? LOG_PAGE_SIZE;
  const pageCount = Math.max(1, Math.ceil(total / servedPageSize));
  const servedPage = logs.data?.page ?? page;
  const loggers = logs.data?.loggers ?? [];

  return (
    <>
      {/* The toolbar is this TAB PANEL's first child, not a sibling of a
          PageHeader: System owns the route's single <h1> and this tab has no
          header of its own. design-system/README.md permits that placement
          explicitly (arb-ajrv) because Tabs partition a surface into
          sub-surfaces, each governing its own data set -- these controls filter
          the log store and nothing else on the System route. The label names
          that data set rather than the route, because a second toolbar
          elsewhere on the page must not be indistinguishable from this one in
          the accessibility tree. */}
      <PageToolbar label="Log filters">
        <PageToolbarSection align="end">
          <PageToolbarMenu label={`Level: ${levelOptionLabel(filters.level)}`} align="end">
            <PageToolbarMenuItem
              label={levelOptionLabel('all')}
              checked={filters.level === 'all'}
              onSelect={() => applyFilters({ level: 'all' })}
            />
            {LOG_LEVELS.map((level) => (
              <PageToolbarMenuItem
                key={level}
                label={levelOptionLabel(level)}
                checked={filters.level === level}
                onSelect={() => applyFilters({ level })}
              />
            ))}
          </PageToolbarMenu>

          {/* Unlike Activity's menus, these options come from the query RESPONSE
              (`logs.data.loggers`), so before the first page lands the menu holds
              only "All loggers". That is the honest pending state rather than a
              spinner or a disabled trigger: "no logger filter" is true before the
              response as well as after it, and the escape hatch back to unfiltered
              must never be the thing that is missing. */}
          <PageToolbarMenu label={`Logger: ${loggerOptionLabel(filters.logger)}`} align="end">
            <PageToolbarMenuItem
              label={loggerOptionLabel('')}
              checked={filters.logger === ''}
              onSelect={() => applyFilters({ logger: '' })}
            />
            {loggers.map((logger) => (
              <PageToolbarMenuItem
                key={logger}
                label={logger}
                checked={filters.logger === logger}
                onSelect={() => applyFilters({ logger })}
              />
            ))}
          </PageToolbarMenu>

          {/* The debounce and the page reset stay HERE, in the caller, rather
              than inside PageToolbarInput: Suppressions drives the same
              primitive with an explicit Apply button, and folding a timer into
              the component would impose this surface's interaction model on
              that one. */}
          <PageToolbarInput
            label="Message"
            value={messageInput}
            placeholder="Search messages…"
            onChange={setMessageInput}
          />
        </PageToolbarSection>
      </PageToolbar>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Logs</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={logs.isPending} error={logs.error} data={logs.data}>
            {(data) => (
              <>
                <LogsTable entries={data.entries} />

                {/* Hidden entirely on a single page: a disabled Next under a short table
                    is noise. Same rule the Activity surface applies. total/pageCount come
                    from the server's OWN count, which since arb-w8ju is taken with the
                    message filter applied -- so the pager describes the whole search, not
                    one page of it. */}
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
