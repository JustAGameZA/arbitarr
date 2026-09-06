import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import type {
  MetadataCacheCoverage,
  ObservabilitySnapshot,
  StalenessEnvelopeResponse,
} from '../../api/types';
import styles from '../surface.module.css';
import local from './System.module.css';
import { useObservabilityQuery, useStalenessQuery } from './queries';

/**
 * Formats a ratio as a percentage, or a dash when there is nothing to divide.
 *
 * `rate`/`hitRate` are null until the counter has seen traffic, and a fresh
 * process legitimately has none. Rendering "0%" there would assert a measured
 * zero hit rate, which is a different and wronger claim than "no data yet".
 */
function formatRate(rate: number | null): string {
  return rate === null ? '—' : `${(rate * 100).toFixed(1)}%`;
}

/** The six-field AC25 staleness envelope, in the order the contract lists them. */
const STALENESS_FIELDS: readonly (readonly [keyof StalenessEnvelopeResponse, string])[] = [
  ['worst_case_unjudged_age', 'Worst-case unjudged age'],
  ['search_result_cache_band_bound', 'Search-result cache band bound'],
  ['classifier_queue_latency', 'Classifier queue latency'],
  ['fresh_until', 'Fresh until'],
  ['refresh_lead_plus_worker_cycle_interval', 'Refresh lead + worker cycle'],
  ['serve_until', 'Serve until'],
];

function StalenessTable({ envelope }: { envelope: StalenessEnvelopeResponse }) {
  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Bound</th>
            <th>Duration</th>
          </tr>
        </thead>
        <tbody>
          {STALENESS_FIELDS.map(([field, label]) => (
            <tr key={field}>
              <td>{label}</td>
              {/* Rendered verbatim as the server's TimeSpan.ToString() ("01:30:00").
                  Reformatting it here would put this page's idea of a duration in
                  front of the operator instead of the contract's own wording. */}
              <td className={local.duration}>{envelope[field]}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** A labelled scalar. The counters are read at a glance, so they are tiles, not a table. */
function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <div className={local.metric}>
      <dt className={local.metricLabel}>{label}</dt>
      <dd className={local.metricValue}>{value}</dd>
    </div>
  );
}

/**
 * An open-ended server-keyed map (suppressions by source+reason, served-age
 * distribution).
 *
 * The keys are server-authored data, not a fixed enum this page can enumerate,
 * so the rows are whatever the server sent, sorted by descending count so the
 * dominant cause is first.
 */
function BreakdownTable({
  emptyLabel,
  entries,
  keyHeading,
}: {
  emptyLabel: string;
  entries: Record<string, number>;
  keyHeading: string;
}) {
  const rows = Object.entries(entries).sort(([, a], [, b]) => b - a);

  if (rows.length === 0) {
    return <p className={styles.empty}>{emptyLabel}</p>;
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>{keyHeading}</th>
            <th>Count</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(([key, count]) => (
            <tr key={key}>
              <td className={local.breakdownKey}>{key}</td>
              <td>{count}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Counters({
  counters,
  metadataCache,
}: {
  counters: ObservabilitySnapshot;
  metadataCache: MetadataCacheCoverage;
}) {
  return (
    <>
      {/* Process-lifetime, reset on restart -- said here because a low number
          after a deploy otherwise reads as a drop in traffic. */}
      <p className={styles.muted}>
        Pipeline counters accumulate over the process lifetime and reset when the service restarts.
      </p>

      <dl className={local.metrics}>
        <Metric label="Results in" value={counters.resultsIn} />
        <Metric label="Suppressed" value={counters.suppressedTotal} />
        <Metric label="LLM calls" value={counters.llmCalls} />
        <Metric label="LLM failures" value={counters.llmFailures} />
        <Metric label="Verdict cache hit rate" value={formatRate(counters.verdictCache.rate)} />
        <Metric label="Search cache hit rate" value={formatRate(counters.searchCache.hitRate)} />
      </dl>

      <h3 className={local.subheading}>Search-result cache reads</h3>
      <dl className={local.metrics}>
        <Metric label="Fresh hits" value={counters.searchCache.freshHits} />
        <Metric label="Stale-but-valid hits" value={counters.searchCache.staleButValidHits} />
        <Metric label="Fetched misses" value={counters.searchCache.fetchedMisses} />
        <Metric label="Degraded misses" value={counters.searchCache.degradedMisses} />
      </dl>

      <h3 className={local.subheading}>Metadata cache coverage</h3>
      <dl className={local.metrics}>
        <Metric label="Entries" value={metadataCache.entries} />
        {/* A negative entry is a recorded "looked up, nothing there" -- not an error. */}
        <Metric label="Negative entries" value={metadataCache.negativeEntries} />
        <Metric label="Distinct series" value={metadataCache.distinctSeries} />
      </dl>

      <h3 className={local.subheading}>Suppressions by source and reason</h3>
      <BreakdownTable
        emptyLabel="No suppressions recorded yet."
        entries={counters.suppressedBySourceAndReason}
        keyHeading="Source and reason"
      />

      <h3 className={local.subheading}>Served age distribution</h3>
      <BreakdownTable
        emptyLabel="No served ages recorded yet."
        entries={counters.servedAgeDistribution}
        keyHeading="Age band"
      />
    </>
  );
}

/**
 * System.
 *
 * Runtime diagnostics: the AC25 staleness envelope (how old a served result can
 * be, worst case) and the pipeline observability counters.
 *
 * The two panels are deliberately independent queries. Staleness is PublicRead
 * and observability is admin-gated, so on a server with no admin key configured
 * -- the review environment's permanent state -- the staleness half still
 * renders while the counters half shows the 503 affordance QueryState owns.
 */
export default function SystemPage() {
  const staleness = useStalenessQuery();
  const observability = useObservabilityQuery();

  return (
    <>
      <PageHeader title="System" description="Build information, logs and runtime diagnostics." />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Staleness envelope</h2>
        <div className={styles.panelBody}>
          <p className={styles.muted}>
            The worst-case age of a served result, derived from the effective settings.
          </p>
          <QueryState isPending={staleness.isPending} error={staleness.error} data={staleness.data}>
            {(envelope) => <StalenessTable envelope={envelope} />}
          </QueryState>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Pipeline counters</h2>
        <div className={styles.panelBody}>
          <QueryState
            isPending={observability.isPending}
            error={observability.error}
            data={observability.data}
          >
            {(data) => <Counters counters={data.counters} metadataCache={data.metadataCache} />}
          </QueryState>
        </div>
      </section>
    </>
  );
}
