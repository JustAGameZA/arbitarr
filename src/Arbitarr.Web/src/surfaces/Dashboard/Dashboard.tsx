import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';

import { Disclosure } from '../../components/Disclosure';
import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState } from '../QueryState';
import type { EffectiveConfigResponse, StatusResponse } from '../../api/types';
import styles from '../surface.module.css';
import { agreementRate, formatDurationSeconds, formatRate } from '../../format';
import { useAgreementQuery } from '../Suppressions/decisionQueries';
import { useEffectiveConfigQuery, useRecentSearchesQuery, useStatusQuery } from './queries';

/**
 * The one announcement scope all three Dashboard queries share (arb-xzvk).
 *
 * Status, Recent searches and Effective configuration are three queries but one
 * arrival: an operator navigating here experiences a single page loading, and
 * before this each QueryState announced "Loading…" on its own pending edge, so
 * a screen reader said it three times. One key, declared once at module level
 * rather than typed as a literal at each call site, because three call sites
 * that must agree on a string are three chances for one of them to drift and
 * silently become its own scope again.
 */
const DASHBOARD_LOAD_SCOPE = 'dashboard';

/** ISO-8601 timestamps render in the operator's own locale, as the legacy page did. */
function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

/**
 * The source circuit-breaker state, in operator language (UX candidates 8+9).
 *
 * Keyed against `StatusEndpoint.cs`'s `ToStateLabel`, which emits exactly one of
 * `closed` | `open` | `half-open` -- the .NET enum's own names, not a vocabulary an
 * operator was ever meant to read. This table is the one and only place those three
 * wire values are given a label and a colour; there used to be a `stateBadgeClass`
 * here that matched `healthy`/`failed`/`unhealthy` instead, which `ToStateLabel`
 * never emits, so every source rendered the warn treatment regardless of its real
 * state. That defect is why this is a name-matched table rather than a range check
 * or a heuristic: each of the three real values is listed once, explicitly, against
 * the endpoint that produces it.
 *
 * A `Map`, not a plain object: `state` is a server-supplied string, and a plain-object
 * lookup resolves inherited names (`constructor`, `toString`, `__proto__`, ...) against
 * `Object.prototype` rather than failing the `undefined` check below, so one of those
 * wire values would silently render an inherited function/object instead of falling
 * through to the verbatim-unknown-state branch. A `Map` has no prototype entries to
 * collide with, so only a genuine own entry here ever matches.
 */
const SOURCE_STATE_BADGES: Map<string, { label: string; className: string }> = new Map([
  ['closed', { label: 'Healthy', className: styles.badgeOk }],
  ['open', { label: 'Paused after failures', className: styles.badgeDanger }],
  ['half-open', { label: 'Retrying', className: styles.badgeWarn }],
]);

/**
 * An unknown state (a future `CircuitState` member `ToStateLabel` learns to emit
 * before this table does) renders the server's string VERBATIM with a neutral
 * treatment -- no ok/warn/danger colour, because none of those three claims is
 * substantiated for a value this table does not recognise, and no switch default
 * that invents new operator wording for a state nobody has named yet.
 */
function SourceStateBadge({ state }: { state: string }) {
  const known = SOURCE_STATE_BADGES.get(state);
  if (known === undefined) {
    return <span className={styles.badge}>{state}</span>;
  }
  return (
    <span className={`${styles.badge} ${known.className}`} title={state}>
      {known.label}
    </span>
  );
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

/**
 * The blocking health banners (arb-ln0).
 *
 * Renders NOTHING for an empty list — the absence of a banner is the healthy state, and a
 * "no problems" panel would train the operator to skim past exactly the region that matters.
 *
 * The wording states the fix, not just the fault: ADR 0014 records that the original incident's
 * whole cost was invisibility, with the setting name buried in event text nobody was watching.
 * "Observed since" names when the condition began, not when this process happened to notice it:
 * arb-v3w persisted health items (see the HealthItem doc), so `observedSinceUtc` survives a
 * restart rather than resetting with it.
 *
 * `role="alert"` rather than a bare div: this appears after the page has already rendered, when
 * the status query resolves, so a screen-reader user would otherwise never be told.
 */
function HealthBanners({ status }: { status: StatusResponse }) {
  // Defaulted rather than dereferenced: `health` is additive, so a response produced before it
  // existed (an older host behind a proxy, a cached body) carries no such key. Reading .length off
  // that undefined throws during render, and because this sits inside the Status panel it takes
  // the whole panel down — turning a missing optional field into a blank surface. An absent list
  // and an empty list mean the same thing here: nothing is refused, so render no banner.
  const items = status.health ?? [];
  if (items.length === 0) {
    return null;
  }

  return (
    <>
      {items.map((item) => (
        <div key={`${item.key}-${item.sourceName}`} className={styles.banner} role="alert">
          {item.sourceName} refused a download: it redirected instead of serving the file —
          observed since {formatTimestamp(item.observedSinceUtc)} (survives a restart). Fix
          &lsquo;NZB access type&rsquo; in NZBHydra2 by setting it to Proxy rather than Redirect to
          indexer.
        </div>
      ))}
    </>
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
 * query -- already fetched for the Effective configuration disclosure below --
 * to tell them apart. That query is issued by this page unconditionally, and
 * NOT by the disclosure: arb-h9gd collapsed those rows behind a `<details>`, so
 * nothing about whether the operator opens it can reach this branch. If that
 * ever becomes a lazy fetch, this empty state loses the fact it distinguishes
 * on and silently falls back to the neutral wording below.
 * `nzbHydraConfigured === undefined` while that query is
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
 *
 * arb-72mf widened that predicate to ANY KIND: at least one enabled source of
 * any kind with a key, not only an NZBHydra one. An install whose only sources
 * are direct Newznab/Torznab rows (possible since #344) searched them correctly
 * while this empty state told the operator nothing was configured. The FIELD is
 * still named `nzbHydraConfigured` because that name is public API on
 * /api/config/effective; only its meaning moved. The copy below therefore names
 * sources generally and must not name NZBHydra again — doing so would send an
 * operator with a working Torznab setup looking for a product they do not use.
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
          No sources configured. Add a source URL and API key to start searching.{' '}
          {/*
            arb-mn12. ONLY this branch is a link. The other two are not calls to
            action — one says the setup is already working and the other is the
            still-pending case — so linking them would point an operator at a
            settings page with nothing to do there.

            The copy names the ACTION and deliberately does not promise this
            message will clear. Per the doc above, `nzbHydraConfigured` resolves
            once at startup, so an operator who follows this link and enables a
            source still reads "No sources configured" here until a restart.
            "Configure a source in Settings" is true regardless of when the flag
            catches up; "this will go away once you do" would not be.

            A react-router <Link>, not a bare <a href>: an anchor would do a full
            document navigation and drop the client-side router, exactly as
            Search.tsx's own no-sources hint already links.
          */}
          <Link to="/settings">Configure a source in Settings</Link>.
        </p>
      );
    }
    if (nzbHydraConfigured === true) {
      return (
        <p className={styles.empty}>
          A source is configured. Sources appear here after the first search runs.
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
                <SourceStateBadge state={source.state} />
              </td>
              <td>{source.consecutiveFailures}</td>
              <td>{source.lastError ?? '—'}</td>
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
      {/* arb-72mf: the LABEL, not the field. `nzbHydraConfigured` keeps its
          wire name (public API on /api/config/effective) while its meaning is
          now "at least one enabled source of any kind has a key", so a row
          reading "NZBHydra configured: Yes" on a Torznab-only install would be
          a plain falsehood in the one place built to state facts. */}
      <FactRow label="Source configured" value={config.nzbHydraConfigured ? 'Yes' : 'No'} />
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
 * Two top-level panels, not three (arb-h9gd): effective config is collapsed
 * behind a disclosure. All THREE queries still run on mount regardless -- see
 * `SourcesTable`'s doc for why the third one cannot become conditional.
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
          <QueryState
            isPending={status.isPending}
            error={status.error}
            data={status.data}
            announceScope={DASHBOARD_LOAD_SCOPE}
          >
            {(data) => (
              <>
                <HealthBanners status={data} />
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
          <QueryState
            isPending={searches.isPending}
            error={searches.error}
            data={searches.data}
            announceScope={DASHBOARD_LOAD_SCOPE}
          >
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
                          <td>{entry.resolvedIdentity ?? '—'}</td>
                          <td>{entry.resultCount}</td>
                          <td>{entry.elapsedMilliseconds}ms</td>
                          <td>{entry.band ?? '—'}</td>
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

      {/*
        arb-h9gd. Collapsed, and a `<details>` rather than a third `.panel`.

        The bead: three equally-weighted panels gave nine lines of static
        configuration the same prominence as indexer health, so nothing on the
        landing page said what to look at first. These rows change only when an
        operator changes a setting — they are reference material consulted
        deliberately, not a signal worth scanning — so they are demoted here
        while Status and Recent searches keep their weight.

        DEMOTED, NOT MOVED. Relocating this to System (beside its staleness
        envelope, which derives from the same values) was the alternative and
        was rejected: `SourcesTable`'s empty state reads `nzbHydraConfigured`
        from this very query, so relocating the panel would move the rendering
        and leave the query here regardless — two surfaces to reason about
        instead of one, for no reduction in what the Dashboard fetches.

        The QueryState stays INSIDE the disclosure, not outside it. Hoisting it
        would put "Loading…" and the error branch on the always-visible row,
        which would re-promote exactly the thing being demoted — a failing
        config fetch would shout from the landing page about rows nobody had
        asked to see. Collapsed, a failure is found by the operator who opens
        it, and the empty state above still reports the same query's outcome in
        the terms that actually matter to them.
      */}
      <Disclosure summary="Effective configuration">
        <QueryState
          isPending={config.isPending}
          error={config.error}
          data={config.data}
          announceScope={DASHBOARD_LOAD_SCOPE}
        >
          {(data) => <ConfigFacts config={data} />}
        </QueryState>
      </Disclosure>
    </>
  );
}
