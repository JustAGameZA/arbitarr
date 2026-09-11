import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import DashboardPage from './Dashboard';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';
import surfaceStyles from '../surface.module.css';

const status = {
  status: 'ok',
  sources: [
    { sourceName: 'nzbhydra', state: 'Healthy', consecutiveFailures: 0, lastError: null },
    {
      sourceName: 'flaky-indexer',
      state: 'Failed',
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

  it('renders its title and the three panels', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Status' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Recent searches' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Effective configuration' })).toBeInTheDocument();
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
    // "observed since" is the process-lifetime hedge: the server loses these on restart, so the
    // wording must not read as "the problem started at this time".
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

    expect(
      await screen.findByText(
        'No sources configured. Add an NZBHydra2 URL and API key to start searching.',
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(
        'NZBHydra2 is configured. Sources appear here after the first search runs.',
      ),
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
        'NZBHydra2 is configured. Sources appear here after the first search runs.',
      ),
    ).toBeInTheDocument();
    expect(
      screen.queryByText(
        'No sources configured. Add an NZBHydra2 URL and API key to start searching.',
      ),
    ).not.toBeInTheDocument();
  });

  it('renders the sources table, not an empty message, when sources are present', async () => {
    mockApi(allOk);
    renderSurface(<DashboardPage />);

    await screen.findByText('nzbhydra');
    expect(
      screen.queryByText(
        'No sources configured. Add an NZBHydra2 URL and API key to start searching.',
      ),
    ).not.toBeInTheDocument();
    expect(
      screen.queryByText(
        'NZBHydra2 is configured. Sources appear here after the first search runs.',
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
