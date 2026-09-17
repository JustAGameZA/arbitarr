import { useId, useState } from 'react';
import { Link } from 'react-router-dom';

import { PageHeader } from '../../components/shell/PageHeader';
import { ApiError } from '../../api/client';
import { QueryState, errorMessage } from '../QueryState';
import { CACHE_BAND_LABELS } from '../../api/types';
import type { AdHocRelease, AdHocSearchProvenance, AdHocSearchResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './Search.module.css';
import { useEffectiveConfigQuery } from '../Dashboard/queries';
import { EMPTY_CRITERIA, useAdHocSearchMutation, useExplanationQuery } from './queries';
import type { SearchCriteria } from './queries';

/**
 * The "no sources configured" hint (audit F-020b), reusing the Dashboard's
 * empty-state wording and its `nzbHydraConfigured` source (#53 stage 53d,
 * widened by arb-72mf): true only when an ENABLED source OF ANY KIND carries an
 * API key. See Dashboard.tsx's `SourcesTable` doc comment for the full
 * configured-vs-reporting reasoning this shares, and for why the field keeps a
 * name that no longer describes its predicate. `undefined` -- still loading, or
 * the query failed -- renders nothing rather than asserting a state before it
 * is known.
 *
 * The wording is deliberately kept in step with the Dashboard's, including
 * naming sources generally rather than NZBHydra: the two hints answer the same
 * question about one install, and an operator who sees both should not read two
 * different diagnoses of it.
 */
function NoSourcesHint() {
  const config = useEffectiveConfigQuery();

  if (config.data?.nzbHydraConfigured !== false) {
    return null;
  }

  return (
    <p className={styles.empty}>
      No sources configured. Add a source URL and API key to start searching in{' '}
      <Link to="/settings">Settings &gt; Sources</Link>.
    </p>
  );
}

/** Bytes to a human size. The server sends the byte count untouched (passthrough). */
function formatSize(bytes: number): string {
  if (bytes <= 0) {
    return '—';
  }
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/**
 * Renders a .NET TimeSpan string ("00:01:30.5000000") as an age.
 *
 * The wire form is the framework's, not seconds: JsonSerializerDefaults.Web has
 * no TimeSpan converter, so parseFloat on it yields 0 and every age would read
 * "0s". Splitting on ':' is what the format actually requires.
 */
export function formatCacheAge(value: string | null): string {
  if (value === null) {
    return 'not cached';
  }
  const parts = value.split(':');
  if (parts.length !== 3) {
    return value;
  }
  const [hours, minutes, seconds] = parts;
  const wholeSeconds = Math.floor(Number(seconds));
  if (Number.isNaN(wholeSeconds)) {
    return value;
  }
  const total = Number(hours) * 3600 + Number(minutes) * 60 + wholeSeconds;
  if (Number.isNaN(total)) {
    return value;
  }
  if (total < 60) {
    return `${total}s`;
  }
  if (total < 3600) {
    return `${Math.floor(total / 60)}m ${total % 60}s`;
  }
  return `${Math.floor(total / 3600)}h ${Math.floor((total % 3600) / 60)}m`;
}

/**
 * @param failuresNamedBelow Suppresses the two failure chips because the
 * no-source-answered empty state directly beneath already names the SAME
 * sources. Only the chips are suppressed, never the underlying lists and never
 * the rate-limit chip, whose behaviour and copy are unchanged in every case.
 * Without this the operator reads each name twice, one line apart, and a test
 * asking which element names a source gets two answers.
 */
function Provenance({
  provenance,
  failuresNamedBelow = false,
}: {
  provenance: AdHocSearchProvenance;
  failuresNamedBelow?: boolean;
}) {
  // cacheBand arrives as a NUMBER (a plain C# enum under the Web defaults),
  // unlike aiVerdict, which the endpoint projects via ToString() and which
  // therefore arrives as a name. The asymmetry is the server's.
  const band = CACHE_BAND_LABELS[provenance.cacheBand] ?? `Band ${provenance.cacheBand}`;

  return (
    <p className={local.provenance}>
      <span>
        Cache: <strong>{band}</strong>
      </span>
      <span>
        Age: <strong>{formatCacheAge(provenance.cacheAge)}</strong>
      </span>
      {provenance.rateLimitedSources.length > 0 && (
        <span className={styles.error}>
          Rate-limited: {provenance.rateLimitedSources.join(', ')}
        </span>
      )}
      {/*
       * arb-cy1y: the two failure lists ride the same strip as the rate-limit
       * chip, so a PARTIAL degradation -- some sources answered, some did not --
       * is visible beside the releases that did arrive rather than only in the
       * all-down empty state below. "Did not answer in time" rather than "down"
       * because the whole-fan-out ceiling names healthy-but-slow sources here
       * too; asserting they are down would be a claim this list cannot support.
       */}
      {!failuresNamedBelow && provenance.timedOutSources.length > 0 && (
        <span className={styles.error}>
          Did not answer in time: {provenance.timedOutSources.join(', ')}
        </span>
      )}
      {!failuresNamedBelow && provenance.failedSources.length > 0 && (
        <span className={styles.error}>Failed: {provenance.failedSources.join(', ')}</span>
      )}
    </p>
  );
}

/**
 * The explanation panel for one selected release.
 *
 * KNOWN BACKEND GAP, ported deliberately rather than silently dropped: the
 * endpoint looks the release up in IReleaseLookup, which is keyed on
 * RenderedRelease.ProxyGuid (a hash of source name + upstream guid), while
 * /api/admin/search returns the RAW upstream Candidate.Guid, and only
 * SearchEndpoint.cs (the Torznab path) ever records into the lookup at all.
 * So for an ad-hoc result this 404s today. Fixing it means changing
 * src/Arbitarr.Api/, which this change does not touch; the affordance is
 * therefore ported with the 404 rendered as a plain sentence instead of a
 * crash, and the gap is called out in the pull request.
 */
function Explanation({
  id,
  guid,
  onClose,
}: {
  id: string;
  guid: string;
  onClose: () => void;
}) {
  const explanation = useExplanationQuery(guid);

  return (
    <div id={id} className={local.explanation}>
      <div className={local.explanationHead}>
        <strong>Match explanation</strong>
        <button type="button" className={styles.buttonSecondary} onClick={onClose}>
          Close
        </button>
      </div>
      <QueryState
        isPending={explanation.isPending}
        error={explanation.error}
        data={explanation.data}
        renderError={(error) =>
          error instanceof ApiError && error.status === 404
            ? 'No stored explanation for this release. Ad-hoc results are not recorded in the release lookup, so only releases served through a Torznab search have one.'
            : errorMessage(error)
        }
      >
        {(loaded) => (
          <dl className={local.explanationBody}>
            <dt>Title</dt>
            <dd>{loaded.title}</dd>
            <dt>Original title</dt>
            <dd>{loaded.originalTitle}</dd>
          </dl>
        )}
      </QueryState>
    </div>
  );
}

/**
 * arb-cy1y: the empty state for a search where nothing answered, as opposed to
 * one that genuinely matched nothing.
 *
 * The distinction is not cosmetic. /torznab/api answers the IDENTICAL merge with
 * a 900/5xx infrastructure element (arb-nus0), so before this the dashboard was
 * the only surface telling the operator to broaden a query while every one of
 * their indexers was silent -- advice that cannot work and hides the outage.
 *
 * The two lists are named separately rather than merged into one sentence
 * because they answer different questions: a timeout says nothing about whether
 * the source is healthy (the whole-fan-out ceiling names healthy-but-slow
 * sources in it), while a failure is a fault worth chasing. Hence "did not
 * answer in time", never "is down".
 */
function NoSourceAnswered({ provenance }: { provenance: AdHocSearchProvenance }) {
  return (
    <>
      <p className={styles.empty}>
        No source answered this search, so there is nothing to show. This is not an empty result:
        broadening the query will not help until the sources below respond.
      </p>
      {provenance.timedOutSources.length > 0 && (
        <p className={styles.empty}>
          Did not answer in time: {provenance.timedOutSources.join(', ')}.
        </p>
      )}
      {provenance.failedSources.length > 0 && (
        <p className={styles.empty}>Failed: {provenance.failedSources.join(', ')}.</p>
      )}
    </>
  );
}

/**
 * Notice that the on-screen page equals the limit ACTUALLY SENT with the
 * request that produced it (po-gate-plan-cpdo ruling 1).
 *
 * The limit is read from the mutation's own retained `variables`, never from
 * the live form field: the operator may have edited the limit or pressed
 * Clear after submitting, and the results on screen still belong to whatever
 * was sent when they were fetched. `criteria.limit` is a string straight from
 * the number input; an empty or non-numeric value binds server-side to the
 * default (50) rather than "no limit", so parsing it here and falling back to
 * 50 mirrors what the server actually did with it.
 */
function ResultCount({
  count,
  submittedLimit,
}: {
  count: number;
  submittedLimit: string;
}) {
  const parsedLimit = Number.parseInt(submittedLimit, 10);
  // NaN > 0 is false, so an unparseable submittedLimit already falls through
  // to the default without a separate Number.isFinite/isNaN guard.
  const limit = parsedLimit > 0 ? parsedLimit : 50;
  const plural = count === 1 ? 'result' : 'results';

  if (count === limit) {
    return (
      <p className={local.resultCount}>
        Showing {count} {plural}, the limit you asked for. There may be more.
      </p>
    );
  }

  return (
    <p className={local.resultCount}>
      Showing {count} {plural}.
    </p>
  );
}

type SortColumn = 'title' | 'size' | 'published';
type SortDirection = 'asc' | 'desc';
/** `null` is the server's own order -- the default, and the state a third
 * click on the same column returns to (po-gate-plan-cpdo ruling 2). */
type SortState = { column: SortColumn; direction: SortDirection } | null;

/** Cycles one column: unsorted -> asc -> desc -> unsorted (server order). */
function nextSortState(current: SortState, column: SortColumn): SortState {
  if (current === null || current.column !== column) {
    return { column, direction: 'asc' };
  }
  if (current.direction === 'asc') {
    return { column, direction: 'desc' };
  }
  return null;
}

function ariaSortFor(current: SortState, column: SortColumn): 'ascending' | 'descending' | 'none' {
  if (current === null || current.column !== column) {
    return 'none';
  }
  return current.direction === 'asc' ? 'ascending' : 'descending';
}

/**
 * Orders releases for display without mutating `response.releases` (ruling
 * 3): `.slice()` first, since `.sort()` is in place. `null` sorts by the
 * server's own order, i.e. does nothing.
 *
 * Size sorts on the raw byte count, never `formatSize`'s string. Published
 * sorts on the raw date. Both treat "absent or unparseable" as sorting LAST in
 * BOTH directions, so a comparator returning a fixed +1/-1 for that case would
 * be wrong for desc; the direction is applied only to definite comparisons and
 * the last-in-both-directions cases are handled ahead of it.
 *
 * `Array.prototype.sort` is stable per spec (guaranteed since ES2019, which
 * every engine this app ships to implements), so two equal keys keep the
 * server's relative order without extra bookkeeping.
 */
function sortedReleases(releases: AdHocRelease[], sort: SortState): AdHocRelease[] {
  if (sort === null) {
    return releases;
  }

  const { column, direction } = sort;
  const sign = direction === 'asc' ? 1 : -1;

  const rank = (release: AdHocRelease): number | null => {
    if (column === 'size') {
      return release.size > 0 ? release.size : null;
    }
    if (column === 'published') {
      const time = new Date(release.pubDate).getTime();
      return Number.isNaN(time) ? null : time;
    }
    return null;
  };

  return releases.slice().sort((a, b) => {
    if (column === 'title') {
      return sign * a.title.localeCompare(b.title);
    }

    const rankA = rank(a);
    const rankB = rank(b);
    if (rankA === null && rankB === null) {
      return 0;
    }
    if (rankA === null) {
      return 1;
    }
    if (rankB === null) {
      return -1;
    }
    return sign * (rankA - rankB);
  });
}

function SortableHeader({
  label,
  column,
  sort,
  onSort,
}: {
  label: string;
  column: SortColumn;
  sort: SortState;
  onSort: (column: SortColumn) => void;
}) {
  return (
    <th aria-sort={ariaSortFor(sort, column)}>
      <button type="button" className={local.sortButton} onClick={() => onSort(column)}>
        {label}
      </button>
    </th>
  );
}

function Results({
  response,
  selectedGuid,
  onSelect,
  submittedLimit,
}: {
  response: AdHocSearchResponse;
  selectedGuid: string | null;
  onSelect: (guid: string | null) => void;
  submittedLimit: string;
}) {
  const [sort, setSort] = useState<SortState>(null);
  const onSort = (column: SortColumn) => setSort((current) => nextSortState(current, column));
  // One explanation panel at a time (mirrors selectedGuid), so one stable id
  // for the whole results list is enough for aria-controls to point at.
  const explanationId = useId();

  if (response.releases.length === 0) {
    // The "nothing answered" test is zero releases AND a non-empty failure list,
    // which is exactly the condition SearchEndpoint.cs escalates on: a merge
    // where some sources failed but others returned releases is a PARTIAL
    // degradation, and that case keeps the releases plus the provenance chips.
    const noSourceAnswered =
      response.provenance.timedOutSources.length > 0 ||
      response.provenance.failedSources.length > 0;

    return (
      <>
        <Provenance provenance={response.provenance} failuresNamedBelow={noSourceAnswered} />
        {noSourceAnswered ? (
          <NoSourceAnswered provenance={response.provenance} />
        ) : (
          <p className={styles.empty}>No releases matched. Try a broader query or different search terms.</p>
        )}
      </>
    );
  }

  const rows = sortedReleases(response.releases, sort);

  return (
    <>
      <Provenance provenance={response.provenance} />
      <ResultCount count={response.releases.length} submittedLimit={submittedLimit} />
      <div className={styles.tableScroll}>
        <table className={styles.table}>
          <thead>
            <tr>
              <SortableHeader label="Title" column="title" sort={sort} onSort={onSort} />
              <th>Source</th>
              <SortableHeader label="Size" column="size" sort={sort} onSort={onSort} />
              <th>Categories</th>
              <SortableHeader label="Published" column="published" sort={sort} onSort={onSort} />
              <th>AI verdict</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {rows.map((release) => (
              <tr key={release.guid}>
                <td>{release.title}</td>
                <td>{release.sourceName}</td>
                <td>{formatSize(release.size)}</td>
                <td>{release.category.join(', ')}</td>
                <td>{new Date(release.pubDate).toLocaleString()}</td>
                <td>
                  {/* A name, not a number — see the note in Provenance. */}
                  {release.aiVerdict === null ? (
                    <span className={styles.muted}>not run</span>
                  ) : (
                    <span className={styles.badge}>{release.aiVerdict}</span>
                  )}
                </td>
                <td>
                  <button
                    type="button"
                    className={styles.buttonSecondary}
                    aria-expanded={release.guid === selectedGuid}
                    aria-controls={release.guid === selectedGuid ? explanationId : undefined}
                    onClick={() => onSelect(release.guid === selectedGuid ? null : release.guid)}
                  >
                    Explain
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {selectedGuid !== null && (
        <Explanation id={explanationId} guid={selectedGuid} onClose={() => onSelect(null)} />
      )}
    </>
  );
}

/**
 * Ad-hoc search (AC8).
 *
 * The AI arbitration opt-in is an explicit, off-by-default checkbox: the run is
 * synchronous and costs an upstream model call per release, so it happens only
 * when the operator asks for it in that submission. Nothing here remembers the
 * choice between searches.
 */
export default function SearchPage() {
  const [criteria, setCriteria] = useState<SearchCriteria>(EMPTY_CRITERIA);
  const [selectedGuid, setSelectedGuid] = useState<string | null>(null);
  const search = useAdHocSearchMutation();

  const update = <K extends keyof SearchCriteria>(name: K, value: SearchCriteria[K]) => {
    setCriteria((previous) => ({ ...previous, [name]: value }));
  };

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    setSelectedGuid(null);
    search.mutate(criteria);
  };

  /**
   * Resets the form to EMPTY_CRITERIA and clears the selected explanation
   * (ruling 4). Results themselves are left alone -- they belong to whatever
   * was last submitted, and clearing the form is not itself a new search, so
   * the operator's last results stay visible until the next Search click
   * replaces them.
   */
  const clear = () => {
    setCriteria(EMPTY_CRITERIA);
    setSelectedGuid(null);
  };

  return (
    <>
      <PageHeader title="Search" description="Run an ad-hoc query against the configured sources." />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Query</h2>
        <div className={styles.panelBody}>
          <NoSourcesHint />
          <form className={styles.form} onSubmit={submit}>
            <label className={styles.field}>
              Query
              <input
                className={styles.input}
                value={criteria.q}
                onChange={(event) => update('q', event.target.value)}
              />
            </label>
            {/*
             * arb-cpdo: TVDB/TMDB/Season/Episode all narrow the SAME query
             * rather than standing alone like Query or Categories, so a
             * fieldset groups them visibly for a sighted operator and names
             * the group for assistive tech. No client-side validation is
             * added beyond what the controls themselves impose -- the
             * grouping is presentational, not a new constraint.
             */}
            <fieldset className={local.narrowingFields}>
              <legend>Narrow by identity</legend>
              <label className={styles.field}>
                TVDB id
                <input
                  className={`${styles.input} ${styles.inputNarrow}`}
                  value={criteria.tvdbid}
                  onChange={(event) => update('tvdbid', event.target.value)}
                />
              </label>
              <label className={styles.field}>
                TMDB id
                <input
                  className={`${styles.input} ${styles.inputNarrow}`}
                  value={criteria.tmdbid}
                  onChange={(event) => update('tmdbid', event.target.value)}
                />
              </label>
              <label className={styles.field}>
                Season
                <input
                  className={`${styles.input} ${styles.inputNarrow}`}
                  value={criteria.season}
                  onChange={(event) => update('season', event.target.value)}
                />
              </label>
              <label className={styles.field}>
                Episode
                <input
                  className={`${styles.input} ${styles.inputNarrow}`}
                  value={criteria.ep}
                  onChange={(event) => update('ep', event.target.value)}
                />
              </label>
            </fieldset>
            <label className={styles.field}>
              Categories
              <input
                className={styles.input}
                placeholder="5000,5040"
                value={criteria.cat}
                onChange={(event) => update('cat', event.target.value)}
              />
            </label>
            <label className={styles.field}>
              Limit
              {/* limit and offset bind as int? server-side: a non-numeric entry
                  would be a 400, so the control refuses one up front. */}
              <input
                type="number"
                min="1"
                className={`${styles.input} ${styles.inputNarrow}`}
                value={criteria.limit}
                onChange={(event) => update('limit', event.target.value)}
              />
            </label>
            <label className={styles.field}>
              Offset
              <input
                type="number"
                min="0"
                className={`${styles.input} ${styles.inputNarrow}`}
                value={criteria.offset}
                onChange={(event) => update('offset', event.target.value)}
              />
            </label>
            <label className={styles.checkboxField}>
              <input
                type="checkbox"
                checked={criteria.runAiSync}
                onChange={(event) => update('runAiSync', event.target.checked)}
              />
              Run AI arbitration (synchronous)
            </label>
            <button type="submit" className={styles.button} disabled={search.isPending}>
              {search.isPending ? 'Searching…' : 'Search'}
            </button>
            <button type="button" className={styles.buttonSecondary} onClick={clear}>
              Clear
            </button>
          </form>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Results</h2>
        <div className={styles.panelBody}>
          {/* Deliberately NOT QueryState (arb-z505), unlike the Explanation
              panel above and the other surfaces this bead migrated. `search` is
              a MUTATION, not a query — see useSearchMutation's own doc for why
              — and so it has a fourth state QueryState has no way to express:
              `isIdle`, before anything has been asked for. QueryState computes
              `pending = isPending || data === undefined`, which for an idle
              mutation is true, so it would render "Loading…" where this panel
              must render the prompt below. That prompt is the first thing an
              operator sees on the app's primary surface, and "Searching…" is
              likewise not QueryState's hardcoded "Loading…". Migrating this
              would be a user-visible regression, not a de-duplication. */}
          {search.error !== null && (
            <p className={styles.error} role="alert">
              {errorMessage(search.error)}
            </p>
          )}
          {search.error === null && search.isPending && <p className={styles.muted}>Searching…</p>}
          {search.error === null && search.isIdle && (
            <p className={styles.empty}>Enter a query above to search.</p>
          )}
          {search.data !== undefined && search.error === null && (
            <Results
              response={search.data}
              selectedGuid={selectedGuid}
              onSelect={setSelectedGuid}
              submittedLimit={search.variables?.limit ?? EMPTY_CRITERIA.limit}
            />
          )}
        </div>
      </section>
    </>
  );
}
