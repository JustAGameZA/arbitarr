import { useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import {
  PageToolbar,
  PageToolbarMenu,
  PageToolbarMenuItem,
  PageToolbarSection,
} from '../../components/shell/toolbar';
import { QueryState } from '../QueryState';
import { useTableDensityStore } from '../../state/tableDensityStore';
import type { ActivityEntry, ActivityKind } from '../../api/types';
import styles from '../surface.module.css';
import local from './Activity.module.css';
import { useActivityQuery, type ActivityFilters, type TimeWindow } from './queries';
import { formatSeasonEpisode, parseSearchDetail } from './searchDetail';

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

/**
 * The human label for a filter value, falling back to the raw value.
 *
 * The fallback is the point: a menu trigger reading "Kind: workerCycle" is ugly
 * but honest, where a blank one would read as "no filter applied" while the
 * query is in fact narrowed.
 */
function optionLabel<T extends string>(
  options: ReadonlyArray<readonly [T, string]>,
  selected: T,
): string {
  return options.find(([value]) => value === selected)?.[1] ?? selected;
}

/** The badge label for a kind, falling back to the raw value for an unknown one. */
function kindLabel(kind: ActivityKind): string {
  return optionLabel(KIND_OPTIONS, kind as ActivityKind | 'all');
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

/**
 * The "×N" badge's tooltip (arb-itw).
 *
 * The instant is localized rather than left as the raw ISO string, unlike the one
 * `Timestamp` puts in its own `title`: there the raw value is the POINT (the visible text
 * is already localized, so the title is what exposes what the server actually sent), while
 * this tooltip is the only place the last-repeat instant appears at all and its reader is
 * comparing it against their own clock. It still carries the offset via `timeZoneName`, so
 * it is no more ambiguous than the row's own timestamp (AC9).
 *
 * A malformed value falls back to the raw string for the same reason `Timestamp` does:
 * showing it makes a server-side format change visible instead of looking like a row that
 * merely never repeated.
 */
function repeatTitle(count: number, lastRepeatedAt: string | null): string {
  if (lastRepeatedAt === null) {
    return `repeated ${count} times`;
  }

  const parsed = new Date(lastRepeatedAt);
  const rendered = Number.isNaN(parsed.getTime())
    ? lastRepeatedAt
    : parsed.toLocaleString(undefined, { timeZoneName: 'short' });

  return `repeated ${count} times, last at ${rendered}`;
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
              {/* arb-itw: repeats are folded onto one stored row at write time, so the
                  count is reported, never recomputed here by grouping the page — a page
                  boundary would split a group and make the surface disagree with the
                  server. The badge appears only above 1: rendering "×1" on every row
                  would add a column of noise to say nothing, and it is the departure
                  from 1 that is the operational signal. The title pairs the count with
                  the LAST repeat, because "71 times" without "still, as of now" does not
                  distinguish a storm that has stopped from one that has not. */}
              <td>
                {entry.summary}
                {entry.repeatCount > 1 && (
                  <span
                    className={`${styles.badge} ${local.repeat}`}
                    title={repeatTitle(entry.repeatCount, entry.lastRepeatedAt)}
                  >
                    ×{entry.repeatCount}
                  </span>
                )}
              </td>
              {/* AC2: the row says what happened AND why. An em dash rather than
                  a blank cell, so "this event has no separate reason" reads
                  differently from "the reason failed to load". */}
              <td className={entry.reason === null ? styles.muted : undefined}>
                {entry.reason ?? '—'}
                <SearchDetailLine entry={entry} />
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
 * The parsed search descriptor, as a secondary line under the reason (arb-2b6).
 *
 * Renders nothing at all unless the row is a SearchServed event whose detail parses. Both guards
 * are needed and neither implies the other: another kind may legitimately put free-form text in
 * `detail` (IEventSink: "Free-form kind-specific detail"), and a SearchServed row from before
 * #204 has a detail this format cannot read. Returning null in either case leaves every other
 * row byte-identical to what it rendered before this change.
 *
 * SECOND LINE, NOT A SIXTH COLUMN. The table already carries five columns and must stay usable at
 * phone width; a column that is empty on every non-search row would cost a horizontal scroll on
 * every row to show one. It goes under the reason specifically because the reason IS the
 * human-readable spelling of this same query (SearchQueryDescriptor.Describe vs DescribeDetail) —
 * the two belong in one cell, and putting the structured form anywhere else would separate a value
 * from its own summary.
 *
 * THE RAW STRING STAYS REACHABLE via `title` rather than an expandable row. An expander is a
 * second interactive control in a dense table, needs its own open/closed state per row, and has to
 * be operable by keyboard to be worth having; a title attribute costs one attribute and no state,
 * and the raw string is a debugging aid, not something an operator reads routinely. If it ever
 * becomes routine, that is the moment for the expander — not before.
 */
function SearchDetailLine({ entry }: { entry: ActivityEntry }) {
  if (entry.kind !== 'searchServed') {
    return null;
  }

  const detail = parseSearchDetail(entry.detail);
  if (detail === null) {
    return null;
  }

  const seasonEpisode = formatSeasonEpisode(detail);

  return (
    <div className={`${local.detail} ${styles.muted}`} title={entry.detail ?? undefined}>
      {/* The declared t= mode, as a chip: it is the one field every parsed detail has, so it
          anchors the line and tells a tvsearch from a movie search at a glance. */}
      <span className={styles.badge}>{detail.type}</span>
      {detail.tvdbId !== null && <Field label="tvdb" value={detail.tvdbId} />}
      {detail.tmdbId !== null && <Field label="tmdb" value={detail.tmdbId} />}
      {seasonEpisode !== null && <span className={local.detailField}>{seasonEpisode}</span>}
      {/* abs=0 is a real value distinct from absent (arb-u1c), so this tests for null rather
          than falsiness — a truthiness check would hide exactly the search it identifies. */}
      {detail.abs !== null && <Field label="abs" value={detail.abs} />}
      {/* The COUNT, not the ids: the list can be long and an operator scanning the feed wants to
          know a request was category-scoped, not which 14 categories. The full list is in the
          title attribute with the rest of the raw string. */}
      {detail.cats.length > 0 && (
        <span className={local.detailField}>
          {detail.cats.length} {detail.cats.length === 1 ? 'category' : 'categories'}
        </span>
      )}
      {/* Unknown keys are rendered rather than dropped, so a backend that adds a field shows up
          here as an unstyled extra instead of silently not appearing. */}
      {Object.entries(detail.rest).map(([key, value]) => (
        <Field key={key} label={key} value={value} />
      ))}
      {/* Last, mirroring the wire order, and the only part allowed to wrap. Quoted so an empty
          -looking or whitespace-only term is still visibly a term. */}
      {detail.q !== null && <span className={local.detailQuery}>“{detail.q}”</span>}
    </div>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <span className={local.detailField}>
      {label}={value}
    </span>
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
  const density = useTableDensityStore((state) => state.density);
  const toggleDensity = useTableDensityStore((state) => state.toggleDensity);

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

      {/* The toolbar is a sibling of PageHeader, not its `actions` slot: the
          filters belong on their own row under the title, the way the *arr
          shell arranges them. No sort menu, because the server returns
          most-recent-first and exposes no sort parameter — inventing one the
          API cannot serve would be a control that silently does nothing. */}
      <PageToolbar label="Activity filters">
        <PageToolbarSection align="end">
          <PageToolbarMenu label={`Kind: ${optionLabel(KIND_OPTIONS, filters.kind)}`} align="end">
            {KIND_OPTIONS.map(([value, label]) => (
              <PageToolbarMenuItem
                key={value}
                label={label}
                checked={filters.kind === value}
                onSelect={() => applyFilters({ kind: value })}
              />
            ))}
          </PageToolbarMenu>

          <PageToolbarMenu
            label={`Time: ${optionLabel(WINDOW_OPTIONS, filters.window)}`}
            align="end"
          >
            {WINDOW_OPTIONS.map(([value, label]) => (
              <PageToolbarMenuItem
                key={value}
                label={label}
                checked={filters.window === value}
                onSelect={() => applyFilters({ window: value })}
              />
            ))}
          </PageToolbarMenu>

          {/*
            The view menu's trigger label is the bare word "View", not the
            active selection. The "state the active selection" rule applies to
            the RADIO menus above, where the trigger is the only place the
            chosen kind or window is visible while the panel is closed. This
            menu holds an independent checkbox whose own aria-checked already
            carries its state, so folding it into the trigger would say the
            same thing twice -- and would stop scaling as soon as a second view
            option joins it (design-system/README.md, "Page toolbar").
          */}
          <PageToolbarMenu label="View" align="end">
            <PageToolbarMenuItem
              kind="checkbox"
              label="Compact rows"
              checked={density === 'compact'}
              onSelect={toggleDensity}
            />
          </PageToolbarMenu>
        </PageToolbarSection>
      </PageToolbar>

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
