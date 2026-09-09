import { useState } from 'react';
import { Link } from 'react-router-dom';

import { PageHeader } from '../../components/shell/PageHeader';
import { ApiError } from '../../api/client';
import { errorMessage } from '../QueryState';
import { CACHE_BAND_LABELS } from '../../api/types';
import type { AdHocSearchProvenance, AdHocSearchResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './Search.module.css';
import { useEffectiveConfigQuery } from '../Dashboard/queries';
import { EMPTY_CRITERIA, useAdHocSearchMutation, useExplanationQuery } from './queries';
import type { SearchCriteria } from './queries';

/**
 * The "no sources configured" hint (audit F-020b), reusing the Dashboard's
 * empty-state wording and its `nzbHydraConfigured` source (#53 stage 53d):
 * true only when an ENABLED source carries an API key. See Dashboard.tsx's
 * `SourcesTable` doc comment for the full configured-vs-reporting reasoning
 * this shares. `undefined` -- still loading, or the query failed -- renders
 * nothing rather than asserting a state before it is known.
 */
function NoSourcesHint() {
  const config = useEffectiveConfigQuery();

  if (config.data?.nzbHydraConfigured !== false) {
    return null;
  }

  return (
    <p className={styles.empty}>
      No sources configured. Add an NZBHydra2 URL and API key to start searching in{' '}
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

function Provenance({ provenance }: { provenance: AdHocSearchProvenance }) {
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
function Explanation({ guid, onClose }: { guid: string; onClose: () => void }) {
  const explanation = useExplanationQuery(guid);

  return (
    <div className={local.explanation}>
      <div className={local.explanationHead}>
        <strong>Match explanation</strong>
        <button type="button" className={styles.buttonSecondary} onClick={onClose}>
          Close
        </button>
      </div>
      {explanation.isPending && <p className={styles.muted}>Loading…</p>}
      {explanation.error !== null && (
        <p className={styles.error} role="alert">
          {explanation.error instanceof ApiError && explanation.error.status === 404
            ? 'No stored explanation for this release. Ad-hoc results are not recorded in the release lookup, so only releases served through a Torznab search have one.'
            : errorMessage(explanation.error)}
        </p>
      )}
      {explanation.data !== undefined && (
        <dl className={local.explanationBody}>
          <dt>Title</dt>
          <dd>{explanation.data.title}</dd>
          <dt>Original title</dt>
          <dd>{explanation.data.originalTitle}</dd>
        </dl>
      )}
    </div>
  );
}

function Results({
  response,
  selectedGuid,
  onSelect,
}: {
  response: AdHocSearchResponse;
  selectedGuid: string | null;
  onSelect: (guid: string | null) => void;
}) {
  if (response.releases.length === 0) {
    return (
      <>
        <Provenance provenance={response.provenance} />
        <p className={styles.empty}>No releases matched. Try a broader query or different search terms.</p>
      </>
    );
  }

  return (
    <>
      <Provenance provenance={response.provenance} />
      <div className={styles.tableScroll}>
        <table className={styles.table}>
          <thead>
            <tr>
              <th>Title</th>
              <th>Source</th>
              <th>Size</th>
              <th>Categories</th>
              <th>Published</th>
              <th>AI verdict</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {response.releases.map((release) => (
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
      {selectedGuid !== null && <Explanation guid={selectedGuid} onClose={() => onSelect(null)} />}
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
          </form>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Results</h2>
        <div className={styles.panelBody}>
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
            />
          )}
        </div>
      </section>
    </>
  );
}
