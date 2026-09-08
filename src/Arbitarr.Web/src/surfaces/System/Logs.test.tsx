import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SystemPage from './System';
import { buildLogsQuery, LOG_PAGE_SIZE } from './queries';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

/**
 * Fixture log rows.
 *
 * The message text is deliberately mundane and carries NOTHING that reads as a real
 * secret or a real address -- this is a log viewer, so a fixture that looked like a
 * leaked value would be indistinguishable from one. Where an address is needed it is
 * 192.0.2.x (RFC 5737 documentation range), which is the repo's placeholder convention.
 */
const entries = [
  {
    id: 3,
    time: '2026-09-07T09:15:00+00:00',
    level: 'Error',
    logger: 'Search.SearchService',
    message: 'Source probe failed for source 4.',
    exception: 'System.Net.Http.HttpRequestException: Connection refused (192.0.2.10:9117)',
    exceptionType: 'System.Net.Http.HttpRequestException',
  },
  {
    id: 2,
    time: '2026-09-07T09:10:00+00:00',
    level: 'Warning',
    logger: 'Workers.RefreshWorker',
    message: 'Refresh cycle took longer than its interval.',
    exception: null,
    exceptionType: null,
  },
  {
    id: 1,
    time: '2026-09-07T09:05:00+00:00',
    level: 'Information',
    logger: 'Workers.RefreshWorker',
    message: 'Refresh cycle completed.',
    exception: null,
    exceptionType: null,
  },
];

const loggers = ['Search.SearchService', 'Workers.RefreshWorker'];

const logsBody = {
  entries,
  total: entries.length,
  page: 1,
  pageSize: LOG_PAGE_SIZE,
  loggers,
};

/**
 * The Status tab's three routes are mocked in every case even when the assertions are
 * about the Logs tab: the page mounts on Status, and an unrouted request renders a 501
 * error where the test expects a table.
 */
const statusRoutes = {
  '/api/system/build': {
    body: {
      commitSha: 'a1b2c3d',
      imageTag: 'arbitarr:a1b2c3d',
      buildTimestampUtc: '2026-09-06T12:00:00Z',
      informationalVersion: '1.2.3+a1b2c3d',
      uptimeSeconds: 3725,
    },
  },
  '/api/health/staleness': {
    body: {
      worst_case_unjudged_age: '02:30:00',
      search_result_cache_band_bound: '00:15:00',
      classifier_queue_latency: '00:00:00',
      fresh_until: '00:45:00',
      refresh_lead_plus_worker_cycle_interval: '00:05:00',
      serve_until: '03:00:00',
    },
  },
  '/api/admin/observability': {
    body: {
      counters: {
        resultsIn: 1200,
        suppressedTotal: 340,
        suppressedBySourceAndReason: {},
        llmCalls: 88,
        llmFailures: 3,
        verdictCache: { hits: 60, misses: 28, rate: 0.68 },
        searchCache: {
          freshHits: 400,
          staleButValidHits: 120,
          fetchedMisses: 80,
          degradedMisses: 5,
          hitRate: 0.86,
        },
        servedAgeDistribution: {},
      },
      metadataCache: { entries: 512, negativeEntries: 64, distinctSeries: 128 },
    },
  },
};

const allRoutes = { ...statusRoutes, '/api/admin/logs': { body: logsBody } };

/** Switches to the Logs tab and waits for its first page to land. */
async function openLogsTab(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('tab', { name: 'Logs' }));
  return screen.findByRole('table');
}

describe('System tabs', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('exposes exactly the Status, Logs and Backup tabs', async () => {
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    const tabs = screen.getAllByRole('tab').map((tab) => tab.textContent);

    // Exactly these three, asserted as an equality rather than a presence check: an
    // Updates or Events tab is a plan §3 ruling to reopen, not something to add
    // silently. Backup joined them with #56, filling the slot §3 reserved.
    expect(tabs).toEqual(['Status', 'Logs', 'Backup']);
    await screen.findByText('02:30:00');
  });

  it('opens on Status with the pre-existing panels intact', async () => {
    mockApi(allRoutes);
    renderSurface(<SystemPage />);

    // AC6: the panels that were the System page before #65 are still the landing view.
    expect(screen.getByRole('tab', { name: 'Status' })).toHaveAttribute('aria-selected', 'true');
    expect(await screen.findByText('02:30:00')).toBeInTheDocument();
    expect(screen.getByText('a1b2c3d')).toBeInTheDocument();
    expect(screen.getByText('1200')).toBeInTheDocument();
  });

  it('does not request logs until the Logs tab is opened', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);

    await screen.findByText('02:30:00');
    expect(api.callsTo('/api/admin/logs')).toHaveLength(0);

    await openLogsTab(user);
    expect(api.callsTo('/api/admin/logs')).toHaveLength(1);
  });

  it('moves between tabs with the arrow keys', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await screen.findByText('02:30:00');

    // The WAI-ARIA tabs pattern: only the active tab is a tab stop, so a keyboard
    // operator reaches the strip once and then arrows within it.
    await user.tab();
    expect(screen.getByRole('tab', { name: 'Status' })).toHaveFocus();

    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Logs' })).toHaveAttribute('aria-selected', 'true');

    // Steps through every tab in order rather than stopping at the second, so a tab added
    // to TABS without being reachable by keyboard fails here.
    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Backup' })).toHaveAttribute('aria-selected', 'true');

    // Wraps rather than dead-ending at the last tab.
    await user.keyboard('{ArrowRight}');
    expect(screen.getByRole('tab', { name: 'Status' })).toHaveAttribute('aria-selected', 'true');

    // And wraps backwards too -- the other end of the same rule.
    await user.keyboard('{ArrowLeft}');
    expect(screen.getByRole('tab', { name: 'Backup' })).toHaveAttribute('aria-selected', 'true');
  });
});

describe('System logs tab', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders a row per entry with its level, logger and message', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    const table = await openLogsTab(user);

    // Three data rows plus the header row.
    expect(within(table).getAllByRole('row')).toHaveLength(entries.length + 1);
    expect(within(table).getByText('Refresh cycle completed.')).toBeInTheDocument();
    expect(within(table).getAllByText('Workers.RefreshWorker')).toHaveLength(2);
    expect(within(table).getByText('Error')).toBeInTheDocument();
  });

  it('sends the admin key on the logs request', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // /api/admin/logs is gated by path prefix. This route being gated while
    // /api/activity is public is deliberate -- LogsEndpoint.cs records why -- so this
    // asserts the key IS attached rather than that it is withheld.
    expect(api.adminKeyOn('/api/admin/logs')).toBe('test-key');
  });

  it('renders the empty state, not a zero, when no rows match', async () => {
    mockApi({
      ...statusRoutes,
      '/api/admin/logs': { body: { ...logsBody, entries: [], total: 0 } },
    });
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await user.click(screen.getByRole('tab', { name: 'Logs' }));

    // Plan §5 names this case explicitly: the zero-rows response must render the
    // empty state and never the number 0.
    expect(
      await screen.findByText(/No log entries match these filters/),
    ).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByText('0')).not.toBeInTheDocument();
  });

  it('names what would fill the table rather than saying it is empty', async () => {
    mockApi({
      ...statusRoutes,
      '/api/admin/logs': { body: { ...logsBody, entries: [], total: 0 } },
    });
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await user.click(screen.getByRole('tab', { name: 'Logs' }));

    // #52: the copy has to teach. It names the cause an operator can act on (the
    // filters) and the level the sink actually records at.
    const empty = await screen.findByText(/No log entries match these filters/);
    expect(empty.textContent).toContain('Information');
    expect(empty.textContent).toContain('widen the level');
  });

  it('hides an exception behind a disclosure and shows it on demand', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // The type name is the always-visible summary; the trace is not rendered until
    // the disclosure is opened.
    const summary = screen.getByText('System.Net.Http.HttpRequestException');
    expect(screen.getByText(/Connection refused/)).not.toBeVisible();

    await user.click(summary);
    expect(screen.getByText(/Connection refused/)).toBeVisible();
  });

  it('renders no disclosure on a row without an exception', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // Exactly one of the three fixture rows carries an exception.
    expect(screen.getAllByRole('group')).toHaveLength(1);
  });

  it('keeps the server ISO instant on the timestamp', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // AC9: the rendered form is the viewer's local time, but the exact instant the
    // server sent stays available for comparison against a docker logs line.
    const [newest] = screen.getAllByRole('time');
    expect(newest).toHaveAttribute('dateTime', '2026-09-07T09:15:00+00:00');
    expect(newest).toHaveAttribute('title', '2026-09-07T09:15:00+00:00');
  });

  it('surfaces the 503 affordance when the server has no admin key', async () => {
    mockApi({
      ...statusRoutes,
      '/api/admin/logs': { status: 503, body: { error: 'no key configured' } },
    });
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await user.click(screen.getByRole('tab', { name: 'Logs' }));

    // The permanent state of the review environment: the tab must explain itself
    // rather than render an empty table.
    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
  });
});

describe('System logs filtering', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('asks the server for the chosen level', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.selectOptions(screen.getByLabelText('Level'), 'Error');

    // Filtering is the SERVER's job -- the store matches Level exactly and
    // case-insensitively -- so the assertion is on the request, not on which rows
    // survived a client-side filter that must not exist.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.get('level')).toBe('Error');
  });

  it('populates the logger filter from the loggers the page returned', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // The options come back with the rows rather than from a second request, so the
    // filter can never render empty beside a table already showing those loggers.
    await user.selectOptions(screen.getByLabelText('Logger'), 'Search.SearchService');

    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.get('logger')).toBe('Search.SearchService');
  });

  it('omits a filter that is set back to all', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.selectOptions(screen.getByLabelText('Level'), 'Warning');
    await user.selectOptions(screen.getByLabelText('Level'), 'all');

    // An omitted parameter, not `level=all` -- the store would match no row spelled
    // "all" and would silently return an empty table.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.has('level')).toBe(false);
  });
});

describe('System logs paging', () => {
  /** 50 rows, so `total` exceeds one page and the paging controls render. */
  const fullPage = Array.from({ length: LOG_PAGE_SIZE }, (_, index) => ({
    id: 500 - index,
    time: '2026-09-07T09:15:00+00:00',
    level: 'Information',
    logger: 'Workers.RefreshWorker',
    message: `Refresh cycle ${index} completed.`,
    exception: null,
    exceptionType: null,
  }));

  const pagedRoutes = {
    ...statusRoutes,
    '/api/admin/logs': {
      body: { entries: fullPage, total: 120, page: 1, pageSize: LOG_PAGE_SIZE, loggers },
    },
  };

  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('hides the paging controls when everything fits on one page', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // A disabled Next under a three-row table is noise.
    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();
  });

  it('derives the page count from the server total', async () => {
    mockApi(pagedRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // 120 rows at 50 per page is 3 pages -- a ceiling, not a truncation.
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
  });

  it('requests the next page and disables Previous on the first', async () => {
    const api = mockApi(pagedRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    api.set('/api/admin/logs', {
      body: { entries: fullPage, total: 120, page: 2, pageSize: LOG_PAGE_SIZE, loggers },
    });
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Page 2 of 3')).toBeInTheDocument();
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.get('page')).toBe('2');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeEnabled();
  });

  it('disables Next on the last page', async () => {
    mockApi({
      ...statusRoutes,
      '/api/admin/logs': {
        body: { entries: fullPage, total: 120, page: 3, pageSize: LOG_PAGE_SIZE, loggers },
      },
    });
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // Driven by the server's own `page`, so a clamped page number still disables the
    // control rather than offering a fourth page that does not exist.
    expect(screen.getByText('Page 3 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });

  it('returns to page one when a filter changes', async () => {
    const api = mockApi(pagedRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    api.set('/api/admin/logs', {
      body: { entries: fullPage, total: 120, page: 2, pageSize: LOG_PAGE_SIZE, loggers },
    });
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('Page 2 of 3');

    api.set('/api/admin/logs', {
      body: { entries: fullPage, total: 60, page: 1, pageSize: LOG_PAGE_SIZE, loggers },
    });
    await user.selectOptions(screen.getByLabelText('Level'), 'Warning');

    // Page 3 of an unfiltered store is a different set of rows from page 3 of a
    // filtered one, and may be past the new end entirely.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.has('page')).toBe(false);
    expect(request?.url.searchParams.get('level')).toBe('Warning');
  });
});

describe('buildLogsQuery', () => {
  it('omits both filters and the page when nothing is narrowed', () => {
    // Tested directly rather than only through the surface: the filter-to-URL mapping
    // is what breaks silently, since a dropped parameter still renders a plausible
    // table of the wrong rows.
    expect(buildLogsQuery({ level: 'all', logger: '' }, 1)).toBe(
      `/api/admin/logs?pageSize=${LOG_PAGE_SIZE}`,
    );
  });

  it('sends both filters when they are set', () => {
    const query = buildLogsQuery({ level: 'Error', logger: 'Search.SearchService' }, 1);
    const params = new URL(query, 'http://localhost').searchParams;

    expect(params.get('level')).toBe('Error');
    expect(params.get('logger')).toBe('Search.SearchService');
  });

  it('trims a whitespace-only logger rather than sending it', () => {
    // The store treats a whitespace filter as absent, so sending one would work by
    // accident; omitting it keeps the request honest about what was asked.
    const params = new URL(
      buildLogsQuery({ level: 'all', logger: '   ' }, 1),
      'http://localhost',
    ).searchParams;

    expect(params.has('logger')).toBe(false);
  });

  it('sends the page only past the first', () => {
    expect(
      new URL(buildLogsQuery({ level: 'all', logger: '' }, 3), 'http://localhost').searchParams.get(
        'page',
      ),
    ).toBe('3');
  });
});
