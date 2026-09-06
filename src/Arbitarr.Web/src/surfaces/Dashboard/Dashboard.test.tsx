import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import DashboardPage from './Dashboard';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

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
    expect(screen.getByText('86400s')).toBeInTheDocument();
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
