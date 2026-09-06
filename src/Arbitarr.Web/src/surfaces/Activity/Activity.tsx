import { useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import type { ActivityEntry, ActivityKind } from '../../api/types';
import styles from '../surface.module.css';
import local from './Activity.module.css';
import { useActivityQuery, type ActivityFilters, type TimeWindow } from './queries';

/**
 * The kind filter's options, and the human label for each.
 *
 * The labels name what the operator would ask about ("Searches served"), not
 * the enum member. The wire values are the camelCase EventKind names and must
 * match the server's; an unknown one is a 400 there rather than a silently
 * unfiltered list, so a typo here surfaces immediately instead of quietly
 * widening the query.
 */
const KIND_OPTIONS: ReadonlyArray<[ActivityKind | 'all', string]> = [
  ['all', 'Everything'],
  ['decision', 'Decisions'],
  ['searchServed', 'Searches served'],
  ['snapshotRefreshed', 'Snapshot refreshes'],
  ['workerCycle', 'Worker cycles'],
  ['sourceFailed', 'Source failures'],
];

const WINDOW_OPTIONS: ReadonlyArray<[TimeWindow, string]> = [
  ['hour', 'Last hour'],
  ['day', 'Last 24 hours'],
  ['week', 'Last 7 days'],
  ['all', 'All time'],
];

/** The badge label for a kind, falling back to the raw value for an unknown one. */
function kindLabel(kind: ActivityKind): string {
  return KIND_OPTIONS.find(([value]) => value === kind)?.[1] ?? kind;
}

/**
 * Renders an event's instant unambiguously (AC9, plan step 7).
 *
 * `toLocaleString` renders in the VIEWER's timezone, which is the right default
 * — an operator reads "did this happen during the outage?" in their own clock —
 * but a bare local time is exactly the ambiguity AC9 forbids, since the same
 * string means different instants to two readers. So the offset comes with it,
 * and the full ISO instant the server actually sent is kept in `title`.
 *
 * This deliberately does NOT invent a relative-time formatter ("3 minutes ago").
 * The project's convention for durations is verbatim TimeSpan.ToString()
 * (System.tsx:47-49, a load-bearing comment), and the same honesty principle
 * applies here: state the instant, do not hide it behind a rounded approximation
 * that cannot be compared against a log line.
 */
function Timestamp({ value }: { value: string }) {
  const parsed = new Date(value);

  if (Number.isNaN(parsed.getTime())) {
    // Never silently blank a malformed timestamp: showing the raw value makes a
    // server-side format change visible rather than looking like a missing row.
    return <span title={value}>{value}</span>;
  }

  return (
    <time dateTime={value} title={value} className={local.timestamp}>
      {parsed.toLocaleString(undefined, { timeZoneName: 'short' })}
    </time>
  );
}

function ActivityTable({ entries }: { entries: ActivityEntry[] }) {
  if (entries.length === 0) {
    return (
      // #52's empty-state rule: say what would FILL it, not merely that it is
      // empty. Both named causes are real emission points, so this doubles as a
      // hint that the surface is working and simply has nothing yet.
      <p className={styles.empty}>
        No activity recorded yet. Events appear here once the worker cycles or a search runs.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Occurred</th>
            <th>Kind</th>
            <th>What happened</th>
            <th>Why</th>
            <th>Source</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry, index) => (
            <tr key={`${entry.occurredAt}:${index}`}>
              <td>
                <Timestamp value={entry.occurredAt} />
              </td>
              <td>
                <span className={styles.badge}>{kindLabel(entry.kind)}</span>
              </td>
              <td>{entry.summary}</td>
              {/* AC2: the row says what happened AND why. An em dash rather than
                  a blank cell, so "this event has no separate reason" reads
                  differently from "the reason failed to load". */}
              <td className={entry.reason === null ? styles.muted : undefined}>
                {entry.reason ?? '—'}
              </td>
              <td className={entry.sourceDisplayName === null ? styles.muted : undefined}>
                {entry.sourceDisplayName ?? '—'}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Activity (#55) — what Arbitarr actually did, and why.
 *
 * The seventh sidebar surface. It answers the question the Dashboard cannot:
 * the Dashboard shows current state, this shows the sequence of events that
 * produced it, and unlike the process-lifetime pipeline counters
 * (System.tsx:123-124) these rows survive a restart.
 */
export default function ActivityPage() {
  const [filters, setFilters] = useState<ActivityFilters>({ kind: 'all', window: 'day' });

  // Cursor-based paging, one page at a time. `cursors` is the stack of page
  // starts already visited, which is what makes "Previous" possible without
  // refetching from the top: a seek cursor knows how to go forward, not back.
  const [cursors, setCursors] = useState<Array<number | null>>([null]);
  const cursor = cursors[cursors.length - 1];
  const activity = useActivityQuery(filters, cursor);

  // Changing a filter invalidates the whole cursor stack: a cursor is a position
  // within one filtered result set and is meaningless in another.
  const applyFilters = (next: Partial<ActivityFilters>) => {
    setFilters((current) => ({ ...current, ...next }));
    setCursors([null]);
  };

  const pageNumber = cursors.length;
  const nextCursor = activity.data?.nextCursor ?? null;

  return (
    <>
      <PageHeader
        title="Activity"
        description="What Arbitarr did, most recent first — each entry with the reason it happened."
      />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Filter</h2>
        <div className={styles.panelBody}>
          <div className={styles.form}>
            <label className={styles.field}>
              Kind
              <select
                className={styles.select}
                value={filters.kind}
                onChange={(event) =>
                  applyFilters({ kind: event.target.value as ActivityKind | 'all' })
                }
              >
                {KIND_OPTIONS.map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </label>

            <label className={styles.field}>
              Time
              <select
                className={styles.select}
                value={filters.window}
                onChange={(event) => applyFilters({ window: event.target.value as TimeWindow })}
              >
                {WINDOW_OPTIONS.map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
          </div>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Events</h2>
        <div className={styles.panelBody}>
          <QueryState
            isPending={activity.isPending}
            error={activity.error}
            data={activity.data}
          >
            {(page) => (
              <>
                <ActivityTable entries={page.events} />

                {/* Paging controls are hidden entirely on a single page — a
                    disabled "Next" under an empty table is noise. */}
                {(pageNumber > 1 || nextCursor !== null) && (
                  <div className={local.paging}>
                    <button
                      type="button"
                      className={styles.buttonSecondary}
                      disabled={pageNumber === 1}
                      onClick={() => setCursors((stack) => stack.slice(0, -1))}
                    >
                      Previous
                    </button>
                    <span className={styles.muted}>Page {pageNumber}</span>
                    <button
                      type="button"
                      className={styles.buttonSecondary}
                      disabled={nextCursor === null}
                      onClick={() => setCursors((stack) => [...stack, nextCursor])}
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
