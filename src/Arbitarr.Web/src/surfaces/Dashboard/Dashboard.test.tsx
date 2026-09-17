import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import DashboardPage from './Dashboard';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';
import surfaceStyles from '../surface.module.css';

const status = {
  status: 'ok',
  // `state` values match `StatusEndpoint.cs`'s `ToStateLabel` exactly: `closed` |
  // `open` | `half-open`, lower-case with a hyphen. The fixture previously used
  // `Healthy`/`Failed`, which that endpoint never emits -- so every source badge
  // rendered the warn treatment regardless of its real state, and no test caught
  // it because nothing here matched the real wire shape.
  sources: [
    { sourceName: 'nzbhydra', state: 'closed', consecutiveFailures: 0, lastError: null },
    {
      sourceName: 'flaky-indexer',
      state: 'open',
      consecutiveFailures: 3,
      lastError: 'upstream timed out',
    },
  ],
  worker: {
    enabled: true,
    lastCycleStartedUtc: '2026-09-06T10:00:00+00:00',
    lastCycleCompletedUtc: '2026-09-06T10:00:12+00:00',
    lastCycleCandidates: 42,
    lastCycleRefreshed: 40,
    lastCycleFailed: 2,
    lastError: null,
    consecutiveFailedCycles: 0,
  },
  // arb-ln0: the healthy default. The banner tests below override this rather than the fixture
  // carrying an item, so every OTHER test in this file also asserts, implicitly, that a healthy
  // payload renders no banner.
  health: [],
};

const blockingHealthItem = {
  key: 'download-refused-redirect',
  severity: 'blocking',
  sourceName: 'nzbhydra2',
  summary: 'Refused HTTP 302: the source redirected instead of serving the file.',
  observedSinceUtc: '2026-09-06T09:00:00+00:00',
  lastObservedUtc: '2026-09-06T10:00:00+00:00',
};

const recent = [
  {
    receivedAt: '2026-09-06T09:59:00+00:00',
    query: 'some series s01e02',
    resolvedIdentity: 'tvdb:12345',
    resultCount: 17,
    elapsedMilliseconds: 312,
    band: 'Fresh',
  },
];

const config = {
  nzbHydraConfigured: true,
  freshUntilSeconds: 300,
  serveUntilSeconds: 900,
  activeWindowSeconds: 3600,
  refreshLeadSeconds: 60,
  workerCycleIntervalSeconds: 120,
  workerEnabled: true,
  querySnapshotTtlSeconds: 86400,
  // #42: the default fixture reflects a real deployment, where shadow mode is never null (D3
  // default-ON). The contract still permits null -- see the dedicated null-branch test below.
  shadowMode: true,
};

const allOk = {
  '/api/status': { body: status },
  '/api/searches/recent': { body: recent },
  '/api/config/effective': { body: config },
};

describe('Dashboard', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and its two top-level panels', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Status' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Recent searches' })).toBeInTheDocument();
    // arb-h9gd: Effective configuration is deliberately NOT a third heading --
    // see the hierarchy block below for the assertions that pin why.
  });

  /**
   * arb-h9gd — the visual hierarchy of this surface.
   *
   * The bead: three equally-weighted top-level panels gave nine lines of static
   * configuration the same prominence as indexer health, so the surface told an
   * operator nothing about what to look at first. Effective configuration is
   * collapsed behind a disclosure; Status and Recent searches keep their weight.
   *
   * These assertions are on STRUCTURE, not text presence, and each excludes a
   * specific way the change could be made without making it:
   *
   *  - "not a heading" alone would pass against a <details open> that changed
   *    nothing, so the content's INITIAL INVISIBILITY is asserted too, and the
   *    same query is then shown to succeed after the summary is activated. That
   *    pairing is the positive control: it proves the absence assertion can
   *    detect the content, rather than passing because the rows never render at
   *    all (a deletion would also make them invisible).
   *  - Status and Recent searches are asserted to still be top-level <section>
   *    panels with their <h2>, because a "hierarchy change" that quietly
   *    demoted health as well would otherwise satisfy every other assertion
   *    here. The bead names the health banners as the one thing already
   *    correctly privileged.
   */
  describe('visual hierarchy (arb-h9gd)', () => {
    it('does not render Effective configuration as a heading-weight top-level panel', async () => {
      mockApi(allOk);
      renderSurface(<DashboardPage />);

      await screen.findByRole('heading', { name: 'Status' });

      expect(
        screen.queryByRole('heading', { name: 'Effective configuration' }),
      ).not.toBeInTheDocument();

      // Not merely "no heading": no top-level `.panel` section carries it
      // either. A <section class=panel> whose label was demoted to a <p> would
      // keep every bit of the DOM weight this bead is removing.
      for (const panel of document.querySelectorAll(`.${surfaceStyles.panel}`)) {
        expect(panel.textContent).not.toContain('Effective configuration');
      }
    });

    /**
     * DO NOT REWRITE THIS AS `expect(...).not.toBeVisible()`. It is the obvious
     * form and it is VACUOUS HERE, which was measured, not assumed: jsdom
     * implements none of `<details>`'s hiding. It ships no UA rule for the
     * closed state, so the collapsed rows compute `display: block`; `toBeVisible`
     * answers true for them, and `getByRole` finds controls inside a closed
     * `<details>` just as readily as inside an open one. A visibility assertion
     * would therefore pass both before and after this bead's change — it would
     * assert nothing at all.
     *
     * What jsdom DOES model faithfully is the `open` attribute and its toggling,
     * so that is what is asserted: closed on mount, open after the summary is
     * activated. The consequence for a real browser — that closed content is out
     * of the layout and out of the accessibility tree — is the platform's
     * guarantee for `<details>`, and choosing the native element instead of a
     * hand-rolled expander is precisely how this surface buys it. Pinning the
     * element and its state is the strongest claim this environment supports.
     */
    it('keeps the disclosure closed on mount and opens it when the summary is activated', async () => {
      mockApi(allOk);
      renderSurface(<DashboardPage />);

      // Wait for the config query to have RESOLVED first: the summary renders
      // synchronously, so querying the rows before the data arrives would fail
      // for the wrong reason entirely.
      await screen.findByText('1.00:00:00');

      const summary = screen.getByText('Effective configuration');
      const details = summary.closest('details');
      expect(details).not.toBeNull();
      expect(details).not.toHaveAttribute('open');

      // The rows are inside THAT element, not merely somewhere on the page --
      // without this the assertions above would hold for an empty <details>
      // rendered beside an untouched always-visible panel.
      expect(within(details as HTMLElement).getByText('Query snapshot TTL')).toBeInTheDocument();
      expect(within(details as HTMLElement).getByText('1.00:00:00')).toBeInTheDocument();

      await userEvent.click(summary);

      expect(details).toHaveAttribute('open');
    });

    it('keeps Status and Recent searches as top-level panels with their headings', async () => {
      mockApi(allOk);
      renderSurface(<DashboardPage />);

      for (const name of ['Status', 'Recent searches']) {
        const heading = await screen.findByRole('heading', { name });
        expect(heading.classList).toContain(surfaceStyles.panelHeading);
        expect(heading.closest('section')?.classList).toContain(surfaceStyles.panel);
        // Not inside a disclosure: a `<details>` ancestor would make these
        // collapsible too, which is the exact demotion this bead must not make.
        expect(heading.closest('details')).toBeNull();
      }
    });

    it('keeps the health banners inside the Status panel, alerting and visible', async () => {
      mockApi({
        ...allOk,
        '/api/status': { body: { ...status, health: [blockingHealthItem] } },
      });
      renderSurface(<DashboardPage />);

      const banner = await screen.findByRole('alert');
      expect(banner).toBeVisible();
      expect(banner.closest('details')).toBeNull();

      const statusPanel = screen
        .getByRole('heading', { name: 'Status' })
        .closest(`.${surfaceStyles.panel}`);
      expect(statusPanel).not.toBeNull();
      expect(statusPanel?.contains(banner)).toBe(true);
    });

    it('still resolves the not-configured empty state from the effective-config query', async () => {
      // The regression a hierarchy change most plausibly causes: SourcesTable
      // reads `nzbHydraConfigured` from the query fetched for the collapsed
      // panel. Collapsing the RENDERING must not stop the QUERY, and this
      // branch is the only place an operator would notice that it had.
      mockApi({
        ...allOk,
        '/api/status': { body: { ...status, sources: [] } },
        '/api/config/effective': { body: { ...config, nzbHydraConfigured: false } },
      });
      renderSurface(<DashboardPage />);

      const empty = await screen.findByText(/No sources configured/);
      expect(empty).toBeVisible();
      expect(empty.closest('details')).toBeNull();
    });
  });

  it('renders its fact rows through the shared surface fact grid', async () => {
    // arb-9zm: this surface had its own copy of the label/value grid in a local
    // Dashboard.module.css, now deleted in favour of surface.module.css. Nothing
    // else here would notice the regression: every text assertion in this file
    // passes just as happily on unstyled dt/dd, so dropping the shared classes
    // would silently return the page to an unformatted definition list.
    //
    // Keyed on the resolved hashed class name (css: true in vite.config.ts), not a
    // /fact/ substring, so it cannot be satisfied by some other similarly-named
    // class -- the vacuity that the same assertion in System.test.tsx fell into
    // once a same-named local .factValue existed alongside the shared one.
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    const value = await screen.findByText('42 candidates · 40 refreshed · 2 failed');
    expect(value.tagName).toBe('DD');
    expect(value.classList).toContain(surfaceStyles.factValue);

    const row = value.parentElement;
    expect(row?.classList).toContain(surfaceStyles.fact);
    expect(row?.querySelector('dt')?.classList).toContain(surfaceStyles.factLabel);
    expect(value.closest('dl')?.classList).toContain(surfaceStyles.facts);
  });

  it('renders worker health, sources and config from the fetched data', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    // The worker lives under `worker`, not under a `workerStatus` property --
    // the legacy wwwroot/app.js reads data.workerStatus, which StatusResponse
    // has never had, so it rendered nothing here. This asserts the real shape.
    expect(await screen.findByText('42 candidates · 40 refreshed · 2 failed')).toBeInTheDocument();

    expect(screen.getByText('nzbhydra')).toBeInTheDocument();
    expect(screen.getByText('upstream timed out')).toBeInTheDocument();
    expect(screen.getByText('some series s01e02')).toBeInTheDocument();
    expect(screen.getByText('17')).toBeInTheDocument();

    // Effective config renders named fields, so a secret-bearing field added to
    // the DTO later cannot appear on screen without a deliberate code change.
    expect(screen.getByText('Query snapshot TTL')).toBeInTheDocument();
    // 86400s = exactly one day: arb-rzx's formatter matches the server's own
    // TimeSpan.ToString() convention that Settings and System already render.
    expect(screen.getByText('1.00:00:00')).toBeInTheDocument();
  });

  it('renders a blocking banner for a health item, naming the source and the setting to change', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, health: [blockingHealthItem] } },
    });
    renderSurface(<DashboardPage />);

    const banner = await screen.findByRole('alert');
    expect(banner).toHaveTextContent('nzbhydra2');
    expect(banner).toHaveTextContent('NZB access type');
    // "observed since" names when the condition began: arb-v3w persisted health items, so this
    // timestamp survives a restart rather than resetting with it.
    expect(banner).toHaveTextContent(/observed since/i);
    expect(banner.classList).toContain(surfaceStyles.banner);
  });

  it('renders no banner when the health list is empty', async () => {
    // Paired with the test above deliberately: that one proves a banner IS detectable by this
    // query, so this absence assertion cannot pass vacuously against a component that renders no
    // alert under any circumstances.
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    // Wait for the status panel to have actually resolved before asserting the absence, or this
    // passes merely because nothing has rendered yet.
    expect(await screen.findByText('42 candidates · 40 refreshed · 2 failed')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('keeps the status panel readable when the response carries no health field at all', async () => {
    // `health` is additive, so a body produced before it existed omits the key entirely. Reading
    // .length off that undefined throws during render and, because the banners sit inside the
    // Status panel, takes the whole panel down with it -- a missing optional field becoming a
    // blank surface. This is the regression the DecisionReview suite caught, pinned here at the
    // component rather than left to depend on some other file's fixture staying stale.
    const statusWithoutHealth = { ...status };
    delete (statusWithoutHealth as Partial<typeof status>).health;
    mockApi({ ...allOk, '/api/status': { body: statusWithoutHealth } });
    renderSurface(<DashboardPage />);

    expect(await screen.findByText('42 candidates · 40 refreshed · 2 failed')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('renders one banner per affected source', async () => {
    mockApi({
      ...allOk,
      '/api/status': {
        body: {
          ...status,
          health: [blockingHealthItem, { ...blockingHealthItem, sourceName: 'second-source' }],
        },
      },
    });
    renderSurface(<DashboardPage />);

    const banners = await screen.findAllByRole('alert');
    expect(banners).toHaveLength(2);
    expect(banners[1]).toHaveTextContent('second-source');
  });

  it('shows the server reason when a panel fails, and keeps the others', async () => {
    mockApi({
      ...allOk,
      '/api/status': { status: 500, body: { error: 'status probe unavailable' } },
    });
    renderSurface(<DashboardPage />);

    expect(await screen.findByText('status probe unavailable')).toBeInTheDocument();
    // One panel failing must not blank the page: the other two still render.
    expect(await screen.findByText('some series s01e02')).toBeInTheDocument();
  });

  it('shows the not-configured empty state when nzbHydraConfigured is false and no sources reported', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, sources: [] } },
      '/api/config/effective': { body: { ...config, nzbHydraConfigured: false } },
    });
    renderSurface(<DashboardPage />);

    // A substring match, not the whole sentence: arb-mn12 appends a <Link> to
    // this paragraph, so its text now spans two nodes and an exact whole-string
    // match would find no single element carrying it.
    expect(await screen.findByText(/No sources configured/)).toBeInTheDocument();
    expect(
      screen.queryByText(
        'A source is configured. Sources appear here after the first search runs.',
      ),
    ).not.toBeInTheDocument();
  });

  /**
   * arb-mn12. The link belongs to ONE of the three empty-state branches.
   *
   * Asserted per branch rather than once: a change that linked every branch — or
   * that moved the link to the wrong one — would pass a single positive
   * assertion, which is exactly the shape this suite has to exclude. The
   * destination is asserted, not just that an anchor exists, because a link to
   * the wrong route is the same dead end as no link at all.
   */
  it('links the not-configured empty state to Settings, and only that branch', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, sources: [] } },
      '/api/config/effective': { body: { ...config, nzbHydraConfigured: false } },
    });
    renderSurface(<DashboardPage />);

    const link = await screen.findByRole('link', { name: /Configure a source in Settings/i });
    expect(link).toHaveAttribute('href', '/settings');

    // The sentence names SOURCES, not NZBHydra (arb-72mf). arb-mn12 added the
    // affordance and left the Hydra framing to arb-x7w8.17, which shipped as
    // docs-only with no behaviour change, so it fell to the bead that widened
    // the predicate this copy describes: the flag is now true for an enabled
    // source of ANY kind, and naming one product here would send an operator
    // with a working Torznab setup looking for something they do not run.
    expect(
      screen.getByText(/No sources configured\. Add a source URL and API key to start searching\./),
    ).toBeInTheDocument();
  });

  it('leaves the configured-idle empty state unlinked', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, sources: [] } },
      '/api/config/effective': { body: { ...config, nzbHydraConfigured: true } },
    });
    renderSurface(<DashboardPage />);

    const empty = await screen.findByText(
      'A source is configured. Sources appear here after the first search runs.',
    );
    expect(within(empty).queryByRole('link')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: /Configure a source in Settings/i }),
    ).not.toBeInTheDocument();
  });

  it('leaves the still-pending empty state unlinked while the config query has not resolved', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, sources: [] } },
      // The effective-config query FAILS, so `nzbHydraConfigured` stays
      // undefined — the same state the component sees while that query is
      // pending, and the only one reachable deterministically from a test.
      '/api/config/effective': { status: 503, body: { error: 'config unavailable' } },
    });
    renderSurface(<DashboardPage />);

    const empty = await screen.findByText('No sources reporting yet.');
    expect(within(empty).queryByRole('link')).not.toBeInTheDocument();
    expect(
      screen.queryByRole('link', { name: /Configure a source in Settings/i }),
    ).not.toBeInTheDocument();
  });

  it('shows the configured-idle empty state when nzbHydraConfigured is true and no sources reported', async () => {
    mockApi({
      ...allOk,
      '/api/status': { body: { ...status, sources: [] } },
      '/api/config/effective': { body: { ...config, nzbHydraConfigured: true } },
    });
    renderSurface(<DashboardPage />);

    expect(
      await screen.findByText(
        'A source is configured. Sources appear here after the first search runs.',
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/No sources configured/)).not.toBeInTheDocument();
  });

  /**
   * UX candidates 8+9. `stateBadgeClass` used to match `healthy`/`failed`/`unhealthy`,
   * which `StatusEndpoint.cs`'s `ToStateLabel` never emits -- it emits exactly
   * `closed` | `open` | `half-open` -- so every source rendered the warn badge
   * regardless of its real state. Asserted PER ROW, with three rows in different
   * states in the same render, because a table-wide assertion ("some row says
   * Healthy") would still pass an implementation that wrote one label to every row.
   */
  describe('source state badge (UX candidates 8+9)', () => {
    const threeStates = {
      ...status,
      sources: [
        { sourceName: 'closed-source', state: 'closed', consecutiveFailures: 0, lastError: null },
        { sourceName: 'open-source', state: 'open', consecutiveFailures: 5, lastError: 'boom' },
        {
          sourceName: 'half-open-source',
          state: 'half-open',
          consecutiveFailures: 1,
          lastError: null,
        },
      ],
    };

    it('maps each of the endpoint three states to its own operator label and badge class, per row', async () => {
      mockApi({ ...allOk, '/api/status': { body: threeStates } });
      renderSurface(<DashboardPage />);

      const closedRow = (await screen.findByText('closed-source')).closest('tr');
      const openRow = screen.getByText('open-source').closest('tr');
      const halfOpenRow = screen.getByText('half-open-source').closest('tr');
      expect(closedRow).not.toBeNull();
      expect(openRow).not.toBeNull();
      expect(halfOpenRow).not.toBeNull();

      const closedBadge = within(closedRow as HTMLElement).getByText('Healthy');
      expect(closedBadge.classList).toContain(surfaceStyles.badge);
      expect(closedBadge.classList).toContain(surfaceStyles.badgeOk);
      expect(closedBadge.classList).not.toContain(surfaceStyles.badgeWarn);
      expect(closedBadge.classList).not.toContain(surfaceStyles.badgeDanger);
      // The raw wire value stays available per row via the title attribute.
      expect(closedBadge).toHaveAttribute('title', 'closed');

      const openBadge = within(openRow as HTMLElement).getByText('Paused after failures');
      expect(openBadge.classList).toContain(surfaceStyles.badge);
      expect(openBadge.classList).toContain(surfaceStyles.badgeDanger);
      expect(openBadge.classList).not.toContain(surfaceStyles.badgeOk);
      expect(openBadge.classList).not.toContain(surfaceStyles.badgeWarn);
      expect(openBadge).toHaveAttribute('title', 'open');

      const halfOpenBadge = within(halfOpenRow as HTMLElement).getByText('Retrying');
      expect(halfOpenBadge.classList).toContain(surfaceStyles.badge);
      expect(halfOpenBadge.classList).toContain(surfaceStyles.badgeWarn);
      expect(halfOpenBadge.classList).not.toContain(surfaceStyles.badgeOk);
      expect(halfOpenBadge.classList).not.toContain(surfaceStyles.badgeDanger);
      expect(halfOpenBadge).toHaveAttribute('title', 'half-open');
    });

    it('renders an unrecognised state verbatim with no ok/warn/danger badge class', async () => {
      // A distinctive marker string rather than a plausible-looking state name, so
      // this cannot pass by accident if some future rename of a KNOWN state happens
      // to collide with the fixture.
      const marker = 'quarantined-zzq47';
      mockApi({
        ...allOk,
        '/api/status': {
          body: {
            ...status,
            sources: [
              { sourceName: 'mystery-source', state: marker, consecutiveFailures: 0, lastError: null },
            ],
          },
        },
      });
      renderSurface(<DashboardPage />);

      const badge = await screen.findByText(marker);
      expect(badge.classList).toContain(surfaceStyles.badge);
      expect(badge.classList).not.toContain(surfaceStyles.badgeOk);
      expect(badge.classList).not.toContain(surfaceStyles.badgeWarn);
      expect(badge.classList).not.toContain(surfaceStyles.badgeDanger);
    });

    /**
     * `constructor` and `__proto__` are own-property misses on a plain-object
     * lookup table that nonetheless resolve through `Object.prototype`, so a
     * naive `table[state]` reads `Object.prototype.constructor` /
     * `Object.prototype.__proto__` instead of hitting `=== undefined` and
     * falling through to the verbatim-unknown-state branch. Both are asserted
     * beside a known state in the SAME render (arb-dash-state-badge review
     * fixup): the sibling row is the positive control proving a real state
     * still resolves through this table while the two adversarial names do
     * not borrow anything from its prototype chain.
     */
    it('renders constructor and __proto__ state values verbatim, not an inherited prototype member', async () => {
      const withPrototypeNames = {
        ...status,
        sources: [
          { sourceName: 'closed-source', state: 'closed', consecutiveFailures: 0, lastError: null },
          {
            sourceName: 'constructor-source',
            state: 'constructor',
            consecutiveFailures: 0,
            lastError: null,
          },
          {
            sourceName: 'proto-source',
            state: '__proto__',
            consecutiveFailures: 0,
            lastError: null,
          },
        ],
      };
      mockApi({ ...allOk, '/api/status': { body: withPrototypeNames } });
      renderSurface(<DashboardPage />);

      // Positive control: a known state in a sibling row still renders its mapped label.
      const closedRow = (await screen.findByText('closed-source')).closest('tr');
      expect(closedRow).not.toBeNull();
      const closedBadge = within(closedRow as HTMLElement).getByText('Healthy');
      expect(closedBadge.classList).toContain(surfaceStyles.badgeOk);

      const constructorRow = screen.getByText('constructor-source').closest('tr');
      expect(constructorRow).not.toBeNull();
      const constructorBadge = within(constructorRow as HTMLElement).getByText('constructor');
      expect(constructorBadge.classList).toContain(surfaceStyles.badge);
      expect(constructorBadge.classList).not.toContain(surfaceStyles.badgeOk);
      expect(constructorBadge.classList).not.toContain(surfaceStyles.badgeWarn);
      expect(constructorBadge.classList).not.toContain(surfaceStyles.badgeDanger);
      expect(constructorRow?.textContent).not.toContain('undefined');

      const protoRow = screen.getByText('proto-source').closest('tr');
      expect(protoRow).not.toBeNull();
      const protoBadge = within(protoRow as HTMLElement).getByText('__proto__');
      expect(protoBadge.classList).toContain(surfaceStyles.badge);
      expect(protoBadge.classList).not.toContain(surfaceStyles.badgeOk);
      expect(protoBadge.classList).not.toContain(surfaceStyles.badgeWarn);
      expect(protoBadge.classList).not.toContain(surfaceStyles.badgeDanger);
      expect(protoRow?.textContent).not.toContain('undefined');
    });
  });

  /**
   * The blank-cell convention (`format.ts`'s U+2014) applies to all three `?? ''`
   * cells this surface used to render, or none -- Last error, Resolved identity and
   * Band. Asserted together so a fix that only reaches one of the three cannot pass.
   */
  it('renders the em-dash, not a blank cell, for a missing last error, identity and band', async () => {
    mockApi({
      ...allOk,
      '/api/status': {
        body: {
          ...status,
          sources: [
            { sourceName: 'quiet-source', state: 'closed', consecutiveFailures: 0, lastError: null },
          ],
        },
      },
      '/api/searches/recent': {
        body: [
          {
            receivedAt: '2026-09-06T09:59:00+00:00',
            query: 'no identity or band',
            resolvedIdentity: null,
            resultCount: 0,
            elapsedMilliseconds: 5,
            band: null,
          },
        ],
      },
    });
    renderSurface(<DashboardPage />);

    await screen.findByText('quiet-source');
    // Asserted per cell, not as a page-wide count: a global toHaveLength(3)
    // proves only that three em-dashes exist SOMEWHERE, which still passes if
    // one of the three intended cells renders something else and an unrelated
    // fourth cell happens to also render '—'. Naming each cell's row and
    // header column pins the dash to the specific field this bead touches.
    const sourceRow = screen.getByText('quiet-source').closest('tr');
    expect(sourceRow).not.toBeNull();
    const [, , , lastErrorCell] = within(sourceRow as HTMLElement).getAllByRole('cell');
    expect(lastErrorCell).toHaveTextContent('—');

    const searchRow = screen.getByText('no identity or band').closest('tr');
    expect(searchRow).not.toBeNull();
    const [, , identityCell, , , bandCell] = within(searchRow as HTMLElement).getAllByRole('cell');
    expect(identityCell).toHaveTextContent('—');
    expect(bandCell).toHaveTextContent('—');
  });

  /**
   * arb-p94u: the recent-searches row's timestamp used to be a bare
   * `new Date(v).toLocaleString()`, which is exactly the ambiguity Activity's
   * AC9 forbids for the same underlying data an operator correlates against
   * logs. The `title` assertion below is what actually pins the fix; the text
   * assertion is an EXACT match against `toLocaleString(undefined, {
   * timeZoneName: 'short' })`, with a positive control proving that string
   * differs from the bare rendering on the CURRENT runner -- a `/[A-Za-z]/`
   * "has a letter" check would be vacuous on an AM/PM (en-US-ish) locale, where
   * the bare rendering already contains letters with no zone information.
   */
  it('renders the recent-search timestamp with a timezone abbreviation and the raw instant in title', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    const searchRow = await screen.findByText('some series s01e02');
    const row = searchRow.closest('tr');
    expect(row).not.toBeNull();
    const [timeCell] = within(row as HTMLElement).getAllByRole('cell');

    const iso = '2026-09-06T09:59:00+00:00';
    const parsed = new Date(iso);
    const expected = parsed.toLocaleString(undefined, { timeZoneName: 'short' });
    // Positive control: proves the exact-match assertion below could not have
    // passed against the old bare rendering on this runner.
    expect(expected).not.toBe(parsed.toLocaleString(undefined));

    expect(timeCell).toHaveAttribute('title', iso);
    expect(timeCell.textContent).toBe(expected);
  });

  it('renders the sources table, not an empty message, when sources are present', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    await screen.findByText('nzbhydra');
    expect(screen.queryByText(/No sources configured/)).not.toBeInTheDocument();
    expect(
      screen.queryByText(
        'A source is configured. Sources appear here after the first search runs.',
      ),
    ).not.toBeInTheDocument();
  });

  it('shows the empty state for recent searches when none are recorded', async () => {
    mockApi({
      ...allOk,
      '/api/searches/recent': { body: [] },
    });
    renderSurface(<DashboardPage />);

    expect(
      await screen.findByText('No searches recorded yet. Entries appear here once a search runs.'),
    ).toBeInTheDocument();
  });

  it('renders "Not set" for shadow mode when the field is null', async () => {
    // The response contract still permits shadowMode: null (bool?) even though a real deployment
    // never sends it (D3 default-ON) -- this keeps that branch covered per #42.
    mockApi({
      ...allOk,
      '/api/config/effective': { body: { ...config, shadowMode: null } },
    });
    renderSurface(<DashboardPage />);

    expect(await screen.findByText('Not set')).toBeInTheDocument();
  });

  it('sends no admin key header, because none of its endpoints is admin-gated', async () => {
    // The inverse of the four admin surfaces' assertion. A key is present in
    // the store precisely so the absence proves the path rule, not an empty
    // store: /api/status, /api/searches/recent and /api/config/effective are
    // all PublicRead, and attaching the key would leak it to routes that never
    // asked for it.
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi(allOk);
    renderSurface(<DashboardPage />);

    await screen.findByText('some series s01e02');

    expect(api.calls.length).toBeGreaterThan(0);
    for (const call of api.calls) {
      expect(call.headers[ADMIN_KEY_HEADER]).toBeUndefined();
    }
  });
});
