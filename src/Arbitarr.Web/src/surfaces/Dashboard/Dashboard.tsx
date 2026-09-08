import type { ReactNode } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import type { EffectiveConfigResponse, StatusResponse } from '../../api/types';
import styles from '../surface.module.css';
import { agreementRate, formatDurationSeconds, formatRate } from '../../format';
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

/**
 * One row of the shared fact grid (surface.module.css, arb-9zm). `value` takes a node
 * rather than a plain string, unlike System's `Fact` -- this surface's rows render a
 * badge and an agreement summary alongside their text, not just an identifier.
 */
function FactRow({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className={styles.fact}>
      <dt className={styles.factLabel}>{label}</dt>
      <dd className={styles.factValue}>{value}</dd>
    </div>
  );
}

function WorkerHealth({ status }: { status: StatusResponse }) {
  const { worker } = status;
  return (
    <dl className={styles.facts}>
      <FactRow
        label="Worker"
        value={
          <span className={worker.enabled ? `${styles.badge} ${styles.badgeOk}` : styles.badge}>
            {worker.enabled ? 'Enabled' : 'Disabled'}
          </span>
        }
      />
      <FactRow
        label="Last cycle started"
        value={worker.lastCycleStartedUtc === null ? '—' : formatTimestamp(worker.lastCycleStartedUtc)}
      />
      <FactRow
        label="Last cycle completed"
        value={
          worker.lastCycleCompletedUtc === null
            ? '—'
            : formatTimestamp(worker.lastCycleCompletedUtc)
        }
      />
      <FactRow
        label="Last cycle"
        value={
          <>
            {worker.lastCycleCandidates} candidates · {worker.lastCycleRefreshed} refreshed ·{' '}
            {worker.lastCycleFailed} failed
          </>
        }
      />
      <FactRow label="Consecutive failed cycles" value={worker.consecutiveFailedCycles} />
      {worker.lastError !== null && (
        <FactRow label="Last error" value={<span className={styles.error}>{worker.lastError}</span>} />
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
          // formatRate(null) supplies the required em-dash; folded into one
          // sentence rather than a fragment plus a parenthetical, which is
          // what arb-rzx / audit F-018 flagged about the old wording.
          ` — agreement ${formatRate(null)}: no decisions reviewed in the last ${windowDays} days.`
        : ` — agreed with ${agreed} of ${reviewed} reviewed in the last ${windowDays} days (${formatRate(rate)}).`}
    </span>
  );
}

/**
 * Durations render as `hh:mm:ss` (arb-rzx / audit F-018), matching the format
 * System's staleness envelope and Settings' catalog values already show — both
 * of those are `TimeSpan`-typed server-side and arrive pre-formatted; these
 * fields arrive as raw seconds, so `formatDurationSeconds` reproduces the same
 * convention on the client. See that function's doc comment for why System and
 * Settings are not touched: they already render the server's own string.
 */
function ConfigFacts({ config }: { config: EffectiveConfigResponse }) {
  return (
    <dl className={styles.facts}>
      <FactRow label="NZBHydra configured" value={config.nzbHydraConfigured ? 'Yes' : 'No'} />
      <FactRow label="Fresh until" value={formatDurationSeconds(config.freshUntilSeconds)} />
      <FactRow label="Serve until" value={formatDurationSeconds(config.serveUntilSeconds)} />
      <FactRow label="Active window" value={formatDurationSeconds(config.activeWindowSeconds)} />
      <FactRow label="Refresh lead" value={formatDurationSeconds(config.refreshLeadSeconds)} />
      <FactRow
        label="Worker cycle interval"
        value={formatDurationSeconds(config.workerCycleIntervalSeconds)}
      />
      <FactRow label="Worker enabled" value={config.workerEnabled ? 'Yes' : 'No'} />
      <FactRow
        label="Query snapshot TTL"
        value={formatDurationSeconds(config.querySnapshotTtlSeconds)}
      />
      <FactRow
        label="Shadow mode"
        value={
          <>
            {config.shadowMode === null ? 'Not set' : config.shadowMode ? 'On' : 'Off'}
            <AgreementSummary />
          </>
        }
      />
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
