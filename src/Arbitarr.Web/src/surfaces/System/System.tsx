import { useId, useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import { BackupTab } from './BackupTab';
import { LogsTab } from './LogsTab';
import type {
  BuildInfoResponse,
  MetadataCacheCoverage,
  ObservabilitySnapshot,
  StalenessEnvelopeResponse,
} from '../../api/types';
import styles from '../surface.module.css';
import local from './System.module.css';
// Shared with #54's agreement rate: one em-dash "no data yet" convention, not two.
import { formatRate } from '../../format';
import { useBuildInfoQuery, useObservabilityQuery, useStalenessQuery } from './queries';

/**
 * Formats an uptime duration (seconds since process start) for display.
 *
 * Rendered separately from the build-time fields in BuildPanel below because it answers a
 * different question: uptime resets on every restart, while the rest of the panel only changes
 * on a redeploy. Whole seconds are enough precision for an operator glance.
 */
function formatUptime(uptimeSeconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(uptimeSeconds));
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  const parts: string[] = [];
  if (days > 0) parts.push(`${days}d`);
  if (days > 0 || hours > 0) parts.push(`${hours}h`);
  if (days > 0 || hours > 0 || minutes > 0) parts.push(`${minutes}m`);
  parts.push(`${seconds}s`);

  return parts.join(' ');
}

/**
 * The build panel: what commit, image and build the running process actually is, plus how long
 * it has been up. Answers "is the box running what I think it's running?" (issue #46 / audit R1)
 * without an ssh session.
 */
function BuildPanel({ buildInfo }: { buildInfo: BuildInfoResponse }) {
  return (
    <>
      {/* Build-time fields only change on a redeploy; uptime resets on every restart. Both
          belong in this panel, but conflating them would misread a restart as a new build. */}
      <dl className={local.metrics}>
        <Metric label="Commit" value={buildInfo.commitSha} />
        <Metric label="Image tag" value={buildInfo.imageTag} />
        <Metric label="Built" value={buildInfo.buildTimestampUtc} />
        <Metric label="Version" value={buildInfo.informationalVersion} />
        <Metric label="Uptime" value={formatUptime(buildInfo.uptimeSeconds)} />
      </dl>
    </>
  );
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
        emptyLabel="No suppressions recorded yet. Entries appear here once a rule or the AI layer acts on a release."
        entries={counters.suppressedBySourceAndReason}
        keyHeading="Source and reason"
      />

      <h3 className={local.subheading}>Served age distribution</h3>
      <BreakdownTable
        emptyLabel="No served ages recorded yet. Entries appear here once a search serves a cached result."
        entries={counters.servedAgeDistribution}
        keyHeading="Age band"
      />
    </>
  );
}

/**
 * The Status tab: build identity (issue #46 / audit R1), the AC25 staleness envelope
 * (how old a served result can be, worst case), and the pipeline observability counters.
 *
 * These three panels are EXACTLY what the System page was before #65 made it tabbed
 * (plan §3, AC6) -- extracted into a component, with their markup, headings, queries and
 * copy unchanged. The extraction is what makes the tab switch unmount them, which is the
 * point: leaving them mounted under a hidden tab would keep three queries refetching
 * behind a table the operator is actually reading.
 *
 * The three panels are deliberately independent queries. Build identity and
 * staleness are PublicRead and observability is admin-gated, so on a server
 * with no admin key configured -- the review environment's permanent state --
 * the build and staleness halves still render while the counters half shows
 * the 503 affordance QueryState owns.
 */
function StatusTab() {
  const buildInfo = useBuildInfoQuery();
  const staleness = useStalenessQuery();
  const observability = useObservabilityQuery();

  return (
    <>
      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Build</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={buildInfo.isPending} error={buildInfo.error} data={buildInfo.data}>
            {(data) => <BuildPanel buildInfo={data} />}
          </QueryState>
        </div>
      </section>

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

/**
 * The System page's tabs, in order.
 *
 * `Backup` is #56's and now FILLS the slot #65's plan §3 reserved for it. It was
 * deliberately absent rather than present-and-disabled until it did something, because an
 * inert tab invites a click that does nothing -- the same rule still applies to any future
 * tab, which is why that reasoning is kept here rather than deleted along with the
 * placeholder it justified.
 *
 * There is no `Updates` tab (Arbitarr is deployed by image tag; there is no in-app
 * updater, and the Build panel already answers "what am I running") and no `Events` tab
 * (the Activity surface is a domain event log and a better one than Sonarr's coarser
 * system-event list). Both omissions are rulings in plan §3, not oversights -- do not add
 * either back without revisiting it there.
 */
const TABS = [
  ['status', 'Status'],
  ['logs', 'Logs'],
  ['backup', 'Backup'],
] as const;

type TabId = (typeof TABS)[number][0];

/**
 * System.
 *
 * Tabbed as of #65 (plan §3) and `Status | Logs | Backup` since #56, adopting the *arr
 * System page's shape adapted to Arbitarr's actual surfaces. The tabs live INSIDE this
 * page and add no nav entry -- SidebarNav's count comment states seven and AC6 requires
 * it to stay seven, which is precisely why Backup is a tab here and not an eighth nav
 * entry.
 *
 * Tab state is local component state and deliberately not a route. The nav highlights by
 * path, so a /system/logs route would light the System entry from a URL the sidebar
 * cannot represent, and routes.tsx's per-route document titles (#51) would need an entry
 * per tab. If a tab ever needs to be linkable, that is the tradeoff to reopen.
 *
 * The tabs are a real ARIA tablist: arrow keys move between them and the panel is
 * associated with its tab, which is the behaviour a keyboard operator expects from
 * something that looks like tabs.
 */
export default function SystemPage() {
  const [tab, setTab] = useState<TabId>('status');
  const tabIds = useId();

  const tabId = (id: TabId) => `${tabIds}-tab-${id}`;
  const panelId = (id: TabId) => `${tabIds}-panel-${id}`;

  /**
   * Arrow-key roving focus (WAI-ARIA tabs pattern), wrapping at both ends.
   *
   * Without this the tablist is reachable but not operable by keyboard the way its
   * appearance promises: Tab alone would step through every tab as a separate stop,
   * which is precisely what `tabIndex={-1}` on the inactive tabs prevents.
   */
  const onTabKeyDown = (event: React.KeyboardEvent<HTMLButtonElement>) => {
    const delta = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (delta === 0) {
      return;
    }

    event.preventDefault();
    const index = TABS.findIndex(([id]) => id === tab);
    const next = TABS[(index + delta + TABS.length) % TABS.length][0];
    setTab(next);
    document.getElementById(tabId(next))?.focus();
  };

  return (
    <>
      <PageHeader title="System" description="Build information and runtime diagnostics." />

      <div className={local.tabs} role="tablist" aria-label="System sections">
        {TABS.map(([id, label]) => (
          <button
            key={id}
            type="button"
            id={tabId(id)}
            role="tab"
            aria-selected={tab === id}
            aria-controls={panelId(id)}
            // Only the active tab is a tab stop; the arrow keys move between them.
            tabIndex={tab === id ? 0 : -1}
            className={tab === id ? `${local.tab} ${local.tabActive}` : local.tab}
            onClick={() => setTab(id)}
            onKeyDown={onTabKeyDown}
          >
            {label}
          </button>
        ))}
      </div>

      {/* Only the active tab is rendered -- see StatusTab's note on why unmounting the
          inactive one matters more here than keeping its scroll position would. */}
      <div id={panelId(tab)} role="tabpanel" aria-labelledby={tabId(tab)} tabIndex={-1}>
        {tab === 'status' && <StatusTab />}
        {tab === 'logs' && <LogsTab />}
        {tab === 'backup' && <BackupTab />}
      </div>
    </>
  );
}
