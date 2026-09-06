import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SystemPage from './System';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const staleness = {
  worst_case_unjudged_age: '02:30:00',
  search_result_cache_band_bound: '00:15:00',
  classifier_queue_latency: '00:00:00',
  fresh_until: '00:45:00',
  refresh_lead_plus_worker_cycle_interval: '00:05:00',
  serve_until: '03:00:00',
};

const buildInfo = {
  commitSha: 'a1b2c3d',
  imageTag: 'arbitarr:a1b2c3d',
  buildTimestampUtc: '2026-09-06T12:00:00Z',
  informationalVersion: '1.2.3+a1b2c3d',
  uptimeSeconds: 3725,
};

const observability = {
  counters: {
    resultsIn: 1200,
    suppressedTotal: 340,
    suppressedBySourceAndReason: { 'DenyRule:no-cam': 200, 'ai:season-mismatch': 140 },
    llmCalls: 88,
    llmFailures: 3,
    verdictCache: { hits: 60, misses: 28, rate: 0.6818 },
    searchCache: {
      freshHits: 400,
      staleButValidHits: 120,
      fetchedMisses: 80,
      degradedMisses: 5,
      hitRate: 0.8646,
    },
    servedAgeDistribution: { '1m-5m': 300, '5m-30m': 90 },
  },
  metadataCache: { entries: 512, negativeEntries: 64, distinctSeries: 128 },
};

const bothRoutes = {
  '/api/system/build': { body: buildInfo },
  '/api/health/staleness': { body: staleness },
  '/api/admin/observability': { body: observability },
};

describe('System', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title', async () => {
    mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'System' })).toBeInTheDocument();
    // Await one async panel so the test does not end mid-flight and leak an
    // update into the next one.
    expect(await screen.findByText('02:30:00')).toBeInTheDocument();
  });

  it('renders the staleness envelope verbatim', async () => {
    mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    // The server sends TimeSpan.ToString() and the page must not reformat it.
    expect(await screen.findByText('02:30:00')).toBeInTheDocument();
    expect(screen.getByText('03:00:00')).toBeInTheDocument();
    expect(screen.getByText('Worst-case unjudged age')).toBeInTheDocument();
    expect(screen.getByText('Serve until')).toBeInTheDocument();
  });

  it('renders the observability counters and their breakdowns', async () => {
    mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    expect(await screen.findByText('1200')).toBeInTheDocument();
    expect(screen.getByText('340')).toBeInTheDocument();

    // Hit rates are ratios on the wire and percentages on screen.
    expect(screen.getByText('68.2%')).toBeInTheDocument();
    expect(screen.getByText('86.5%')).toBeInTheDocument();

    // Metadata-cache coverage.
    expect(screen.getByText('512')).toBeInTheDocument();
    expect(screen.getByText('128')).toBeInTheDocument();

    // Server-authored map keys survive verbatim -- they are data, not camelCased
    // property names.
    expect(screen.getByText('DenyRule:no-cam')).toBeInTheDocument();
    expect(screen.getByText('1m-5m')).toBeInTheDocument();
  });

  it('sorts each breakdown by descending count', async () => {
    mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    await screen.findByText('DenyRule:no-cam');
    const keys = screen
      .getAllByText(/^(DenyRule:no-cam|ai:season-mismatch)$/)
      .map((node) => node.textContent);

    // 200 before 140: the dominant cause reads first.
    expect(keys).toEqual(['DenyRule:no-cam', 'ai:season-mismatch']);
  });

  it('shows a dash rather than 0% when a rate has no traffic yet', async () => {
    mockApi({
      ...bothRoutes,
      '/api/admin/observability': {
        body: {
          ...observability,
          counters: {
            ...observability.counters,
            verdictCache: { hits: 0, misses: 0, rate: null },
            searchCache: { ...observability.counters.searchCache, hitRate: null },
          },
        },
      },
    });
    renderSurface(<SystemPage />);

    // "0%" would assert a measured zero hit rate; a fresh process has no data.
    expect(await screen.findAllByText('—')).toHaveLength(2);
  });

  it('sends the admin key to observability but not to staleness', async () => {
    const api = mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    await screen.findByText('1200');

    // /api/admin/ is gated by path prefix, so the key rides along here...
    expect(api.adminKeyOn('/api/admin/observability')).toBe('test-key');
    // ...and must not leak onto the PublicRead staleness endpoint. Assert the
    // request happened first: an absent header and an absent request both read
    // as undefined, and only one of those is the behaviour under test.
    expect(api.callsTo('/api/health/staleness')).toHaveLength(1);
    expect(api.adminKeyOn('/api/health/staleness')).toBeUndefined();
  });

  it('still renders staleness when the admin-gated counters are refused', async () => {
    mockApi({
      '/api/health/staleness': { body: staleness },
      '/api/admin/observability': { status: 503, body: { error: 'no key configured' } },
    });
    renderSurface(<SystemPage />);

    // The panels are independent queries precisely so the unauthenticated half
    // survives: this is the review environment's permanent state.
    expect(await screen.findByText('02:30:00')).toBeInTheDocument();
    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
  });

  it('renders the build panel from the mock fixture', async () => {
    mockApi(bothRoutes);
    renderSurface(<SystemPage />);

    expect(await screen.findByText('a1b2c3d')).toBeInTheDocument();
    expect(screen.getByText('arbitarr:a1b2c3d')).toBeInTheDocument();
    expect(screen.getByText('2026-09-06T12:00:00Z')).toBeInTheDocument();
    expect(screen.getByText('1.2.3+a1b2c3d')).toBeInTheDocument();
    // 3725s = 1h 2m 5s.
    expect(screen.getByText('1h 2m 5s')).toBeInTheDocument();
  });

  it('still renders the build panel when the admin-gated counters query fails', async () => {
    mockApi({
      '/api/system/build': { body: buildInfo },
      '/api/health/staleness': { body: staleness },
      '/api/admin/observability': { status: 503, body: { error: 'no key configured' } },
    });
    renderSurface(<SystemPage />);

    // The build panel is PublicRead and its own independent query, so it must
    // survive exactly like the staleness panel when observability 503s.
    expect(await screen.findByText('a1b2c3d')).toBeInTheDocument();
    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
  });

  it('renders an empty-state line for a breakdown with no rows', async () => {
    mockApi({
      ...bothRoutes,
      '/api/admin/observability': {
        body: {
          ...observability,
          counters: {
            ...observability.counters,
            suppressedBySourceAndReason: {},
            servedAgeDistribution: {},
          },
        },
      },
    });
    renderSurface(<SystemPage />);

    expect(await screen.findByText('No suppressions recorded yet.')).toBeInTheDocument();
    expect(screen.getByText('No served ages recorded yet.')).toBeInTheDocument();
  });
});
