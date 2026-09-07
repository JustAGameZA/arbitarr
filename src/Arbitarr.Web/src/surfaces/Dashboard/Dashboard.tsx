import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import type { EffectiveConfigResponse, StatusResponse } from '../../api/types';
import styles from '../surface.module.css';
import local from './Dashboard.module.css';
import { agreementRate, formatRate } from '../../format';
import { useAgreementQuery } from '../Suppressions/decisionQueries';
import { useEffectiveConfigQuery, useRecentSearchesQuery, useStatusQuery } from './queries';

/** ISO-8601 timestamps render in the operator's own locale, as the legacy page did. */
function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

function stateBadgeClass(state: string): string {
  const normalized = state.toLowerCase();
  if (normalized === 'healthy') {
    return `${styles.badge} ${styles.badgeOk}`;
  }
  if (normalized === 'failed' || normalized === 'unhealthy') {
    return `${styles.badge} ${styles.badgeDanger}`;
  }
  return `${styles.badge} ${styles.badgeWarn}`;
}

function WorkerHealth({ status }: { status: StatusResponse }) {
  const { worker } = status;
  return (
    <dl className={local.facts}>
      <dt>Worker</dt>
      <dd>
        <span className={worker.enabled ? `${styles.badge} ${styles.badgeOk}` : styles.badge}>
          {worker.enabled ? 'Enabled' : 'Disabled'}
        </span>
      </dd>
      <dt>Last cycle started</dt>
      <dd>
        {worker.lastCycleStartedUtc === null ? '—' : formatTimestamp(worker.lastCycleStartedUtc)}
      </dd>
      <dt>Last cycle completed</dt>
      <dd>
        {worker.lastCycleCompletedUtc === null
          ? '—'
          : formatTimestamp(worker.lastCycleCompletedUtc)}
      </dd>
      <dt>Last cycle</dt>
      <dd>
        {worker.lastCycleCandidates} candidates · {worker.lastCycleRefreshed} refreshed ·{' '}
        {worker.lastCycleFailed} failed
      </dd>
      <dt>Consecutive failed cycles</dt>
      <dd>{worker.consecutiveFailedCycles}</dd>
      {worker.lastError !== null && (
        <>
          <dt>Last error</dt>
          <dd className={styles.error}>{worker.lastError}</dd>
        </>
      )}
    </dl>
  );
}

/**
 * `status.sources` is built from health snapshots written when a source is
 * *used*, not from configuration -- so an empty list is ambiguous between
 * "nothing configured" and "configured, but no search has run yet". Those are
 * different operator situations (one needs setup, the other just needs to
 * wait), so the empty state reads `nzbHydraConfigured` from the effective-config
 * query -- already fetched for the Effective configuration panel below -- to
 * tell them apart. `nzbHydraConfigured === undefined` while that query is
 * still pending falls back to the neutral wording rather than asserting either
 * state before the fact is known.
 *
 * #53 stage 53d settled what the flag means, and it is worth stating here
 * because this empty state is where an operator reads the answer:
 * `nzbHydraConfigured` is true only when an ENABLED source carries an API key.
 * A source that exists and has a key but has been disabled reports FALSE, so
 * "No sources configured" is what shows — which is the correct reading, since a
 * disabled source is never searched and telling the operator it is configured
 * would describe a working setup that cannot answer a query. That is the
 * configured-vs-reporting distinction this surface exists to keep honest.
 * Resolution happens once at startup, so a source disabled from Settings
 * changes this only after a restart.
 */
function SourcesTable({
  status,
  nzbHydraConfigured,
}: {
  status: StatusResponse;
  nzbHydraConfigured: boolean | undefined;
}) {
  if (status.sources.length === 0) {
    if (nzbHydraConfigured === false) {
      return (
        <p className={styles.empty}>
          No sources configured. Add an NZBHydra2 URL and API key to start searching.
        </p>
      );
    }
    if (nzbHydraConfigured === true) {
      return (
        <p className={styles.empty}>
          NZBHydra2 is configured. Sources appear here after the first search runs.
        </p>
      );
    }
    return <p className={styles.empty}>No sources reporting yet.</p>;
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Source</th>
            <th>State</th>
            <th>Failures</th>
            <th>Last error</th>
          </tr>
        </thead>
        <tbody>
          {status.sources.map((source) => (
            <tr key={source.sourceName}>
              <td>{source.sourceName}</td>
              <td>
                <span className={stateBadgeClass(source.state)}>{source.state}</span>
              </td>
              <td>{source.consecutiveFailures}</td>
              <td>{source.lastError ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * The effective-config rows, named one by one rather than iterated.
 *
 * AC7 calls for "secrets masked". The masking is the SERVER's: ConfigProjection
 * is an explicit allow-list that carries no source URLs, host names or API keys
 * at all -- it omits them rather than redacting them, so no placeholder betrays
 * that an upstream exists at a particular address. The client's job is to not
 * undo that. The legacy app.js rendered Object.entries(config), which mirrors
 * whatever the server sends, so the day a secret-bearing field is added to the
 * DTO it would appear on screen with no code change here to notice. Naming the
 * fields means a new one is invisible until someone adds it deliberately.
 */
/**
 * The agreement summary, rendered on the Shadow mode row (#54 step 6 / AC3).
 *
 * HERE, rather than on the review surface, because this is where the decision it
 * informs is actually made: requirement 3's motivating sentence is "the pipeline
 * has been right 47 of 52 times this week", and that figure is only useful next
 * to the switch the operator is deciding whether to flip.
 *
 * THE RATIO IS FORMED HERE, AT THE POINT OF DISPLAY, AND THAT IS THE WHOLE
 * POINT. The endpoint returns counts and never a rate, because with zero reviews
 * there is no rate to return — 0/0 is not 0%, and a server-side 0 would assert a
 * measured zero agreement rate, which is a far stronger and wronger claim than
 * "nobody has reviewed anything yet". agreementRate answers null for that case
 * and formatRate renders the em-dash, the same "no data yet" convention the
 * System surface has always used. AC4 is exactly this: a dash, never "0%".
 *
 * A failed or pending fetch renders NOTHING rather than a placeholder rate. The
 * shadow-mode state itself is the load-bearing fact on this row and is already
 * rendered; a fabricated or half-loaded agreement figure beside it would be
 * worse than its absence.
 */
function AgreementSummary() {
  const agreement = useAgreementQuery();

  if (agreement.data === undefined) {
    return null;
  }

  const { agreed, reviewed, windowDays } = agreement.data;
  const rate = agreementRate(agreed, reviewed);

  return (
    <span className={styles.muted}>
      {rate === null
        ? // Says what would fill it (#52): a verdict has to be recorded first.
          ` — agreement ${formatRate(null)} (no decisions reviewed in the last ${windowDays} days)`
        : ` — agreed with ${agreed} of ${reviewed} reviewed in the last ${windowDays} days (${formatRate(rate)})`}
    </span>
  );
}

function ConfigFacts({ config }: { config: EffectiveConfigResponse }) {
  const seconds = (value: number) => `${value}s`;

  return (
    <dl className={local.facts}>
      <dt>NZBHydra configured</dt>
      <dd>{config.nzbHydraConfigured ? 'Yes' : 'No'}</dd>
      <dt>Fresh until</dt>
      <dd>{seconds(config.freshUntilSeconds)}</dd>
      <dt>Serve until</dt>
      <dd>{seconds(config.serveUntilSeconds)}</dd>
      <dt>Active window</dt>
      <dd>{seconds(config.activeWindowSeconds)}</dd>
      <dt>Refresh lead</dt>
      <dd>{seconds(config.refreshLeadSeconds)}</dd>
      <dt>Worker cycle interval</dt>
      <dd>{seconds(config.workerCycleIntervalSeconds)}</dd>
      <dt>Worker enabled</dt>
      <dd>{config.workerEnabled ? 'Yes' : 'No'}</dd>
      <dt>Query snapshot TTL</dt>
      <dd>{seconds(config.querySnapshotTtlSeconds)}</dd>
      <dt>Shadow mode</dt>
      <dd>
        {config.shadowMode === null ? 'Not set' : config.shadowMode ? 'On' : 'Off'}
        <AgreementSummary />
      </dd>
    </dl>
  );
}

/**
 * The Dashboard (AC7): status, recent searches and effective config, read-only.
 *
 * Requires NO admin key -- all three endpoints are PublicRead and none sits
 * under /api/admin/, so apiFetch's path-prefix rule attaches no header. The
 * corresponding test asserts the header's ABSENCE, which is the inverse of the
 * four admin surfaces' assertion.
 */
export default function DashboardPage() {
  const status = useStatusQuery();
  const searches = useRecentSearchesQuery();
  const config = useEffectiveConfigQuery();

  return (
    <>
      <PageHeader
        title="Dashboard"
        description="Indexer health, recent activity and proxy status."
      />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Status</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={status.isPending} error={status.error} data={status.data}>
            {(data) => (
              <>
                <WorkerHealth status={data} />
                <SourcesTable status={data} nzbHydraConfigured={config.data?.nzbHydraConfigured} />
              </>
            )}
          </QueryState>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Recent searches</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={searches.isPending} error={searches.error} data={searches.data}>
            {(entries) =>
              entries.length === 0 ? (
                <p className={styles.empty}>
                  No searches recorded yet. Entries appear here once a search runs.
                </p>
              ) : (
                <div className={styles.tableScroll}>
                  <table className={styles.table}>
                    <thead>
                      <tr>
                        <th>Time</th>
                        <th>Query</th>
                        <th>Identity</th>
                        <th>Results</th>
                        <th>Elapsed</th>
                        <th>Band</th>
                      </tr>
                    </thead>
                    <tbody>
                      {entries.map((entry, index) => (
                        <tr key={`${entry.receivedAt}-${index}`}>
                          <td>{formatTimestamp(entry.receivedAt)}</td>
                          <td>{entry.query}</td>
                          <td>{entry.resolvedIdentity ?? ''}</td>
                          <td>{entry.resultCount}</td>
                          <td>{entry.elapsedMilliseconds}ms</td>
                          <td>{entry.band ?? ''}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )
            }
          </QueryState>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Effective configuration</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={config.isPending} error={config.error} data={config.data}>
            {(data) => <ConfigFacts config={data} />}
          </QueryState>
        </div>
      </section>
    </>
  );
}
