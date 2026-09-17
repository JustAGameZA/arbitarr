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

/**
 * Picks an option from one of the toolbar's filter menus (arb-ajrv).
 *
 * The two level/logger filters were native <select>s driven by
 * `user.selectOptions` until the toolbar migration; they are now disclosure
 * buttons over a role="menu" panel, so choosing an option is open-then-click.
 * The trigger is matched by its `Level:`/`Logger:` PREFIX because its label also
 * carries the active selection, which is the thing several of these cases are
 * about to change.
 */
async function chooseFilter(
  user: ReturnType<typeof userEvent.setup>,
  filter: 'Level' | 'Logger',
  option: string,
) {
  await user.click(screen.getByRole('button', { name: new RegExp(`^${filter}:`) }));
  await user.click(
    within(screen.getByRole('menu', { name: new RegExp(`^${filter}:`) })).getByRole(
      'menuitemradio',
      { name: option },
    ),
  );
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

  it('defaults the first request to the Warning level', async () => {
    // The server applies `level` as a MINIMUM severity (arb-pw7r), so this one parameter
    // is the whole "Warning and above" default -- the wire value stays the bare level
    // name, and widening it here would ask for something the API does not accept.
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    const request = api.callsTo('/api/admin/logs').at(0);
    expect(request?.url.searchParams.get('level')).toBe('Warning');
    // The trigger states the active selection, which is where the default is
    // now visible without opening the menu -- the readability the <select> this
    // replaced gave for free.
    expect(
      screen.getByRole('button', { name: 'Level: Warning and above' }),
    ).toBeInTheDocument();
  });

  it('labels each level as "and above", except Critical which has nothing above it', async () => {
    // The label is the only place the minimum-severity semantic is visible to an
    // operator. A bare "Warning" reads as an exact filter, which is the misreading that
    // made the previous behaviour hide Error and Critical from the default view.
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.click(screen.getByRole('button', { name: /^Level:/ }));
    const labels = within(screen.getByRole('menu', { name: /^Level:/ }))
      .getAllByRole('menuitemradio')
      .map((option) => option.textContent);

    expect(labels).toEqual([
      'All levels',
      'Information and above',
      'Warning and above',
      'Error and above',
      'Critical',
    ]);
  });

  it('re-issues without the level filter when All levels is chosen', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await chooseFilter(user, 'Level', 'All levels');

    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.has('level')).toBe(false);
  });

  it('asks the server for the chosen level', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await chooseFilter(user, 'Level', 'Error and above');

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
    await chooseFilter(user, 'Logger', 'Search.SearchService');

    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.get('logger')).toBe('Search.SearchService');
  });

  it('omits a filter that is set back to all', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await chooseFilter(user, 'Level', 'Warning and above');
    await chooseFilter(user, 'Level', 'All levels');

    // An omitted parameter, not `level=all` -- the store would match no row spelled
    // "all" and would silently return an empty table.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.has('level')).toBe(false);
  });
});

describe('System logs message filter', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
    // The Message input debounces what reaches the query (arb-x64p): fake timers let a test
    // fast-forward past the ~250ms window instead of racing a real one, and userEvent's
    // `delay: null` (below) keeps its own keystroke-pacing timers from needing the same
    // real clock. `shouldAdvanceTime` lets Testing Library's own polling (findByRole, etc.)
    // keep making progress against the fake clock instead of deadlocking against it.
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('asks the server for the message rather than filtering the rows in hand', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.type(screen.getByLabelText('Message'), 'probe');
    await vi.advanceTimersByTimeAsync(250);

    // arb-w8ju: searching is the SERVER's job now, so the assertion is on the request.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.get('message')).toBe('probe');
  });

  it('debounces the message so three quick keystrokes cost one request', async () => {
    // arb-x64p: typing "probe" character by character must not issue five admin round
    // trips. Advancing the fake clock only after the whole word lands proves the requests
    // in between never fired, not merely that the LAST one carried the right value.
    const api = mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    const before = api.callsTo('/api/admin/logs').length;
    await user.type(screen.getByLabelText('Message'), 'pro');
    // No new request yet -- the debounce window has not elapsed.
    expect(api.callsTo('/api/admin/logs')).toHaveLength(before);

    await vi.advanceTimersByTimeAsync(250);

    const after = api.callsTo('/api/admin/logs');
    expect(after).toHaveLength(before + 1);
    expect(after.at(-1)?.url.searchParams.get('message')).toBe('pro');
  });

  it('renders every row the server returned, without filtering them again', async () => {
    // The mock keeps returning all three fixture rows whatever is typed, which is what a
    // server that ignored the parameter would look like. Any surviving client-side filter
    // would drop two of them here -- so this fails if the visibleEntries derivation comes
    // back, and it is the reason the assertion is a row COUNT rather than a presence check.
    mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    const table = await openLogsTab(user);

    expect(within(table).getAllByRole('row')).toHaveLength(entries.length + 1);

    await user.type(screen.getByLabelText('Message'), 'probe');
    await vi.advanceTimersByTimeAsync(250);

    const after = await screen.findByRole('table');
    expect(within(after).getAllByRole('row')).toHaveLength(entries.length + 1);
    expect(within(after).getByText('Refresh cycle completed.')).toBeInTheDocument();
  });

  it('drops the message parameter when the filter is cleared', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    const input = screen.getByLabelText('Message');
    await user.type(input, 'probe');
    await vi.advanceTimersByTimeAsync(250);

    // Positive control: the parameter is on the wire while filtered, so its absence after
    // clearing is evidence the filter changed rather than evidence it was never sent.
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.get('message')).toBe('probe');

    await user.clear(input);
    await vi.advanceTimersByTimeAsync(250);

    // Omitted, not `message=` -- an empty string would be a filter the store treats as
    // absent only by accident.
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.has('message')).toBe(false);
  });

  it('returns to page one when the message filter changes', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.type(screen.getByLabelText('Message'), 'probe');
    await vi.advanceTimersByTimeAsync(250);

    // A server-side search changes which rows exist, so page 3 of the old result set is not
    // page 3 of the new one -- and may be past its end entirely.
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.has('page')).toBe(false);
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

  it('stays on the requested page when the message debounce elapses after the click', async () => {
    // arb-6l13. The debounce effect also runs on MOUNT, and its timer used to reset the page
    // unconditionally when it fired ~250ms later -- so clicking Next inside that window put
    // the operator back on page 1 a quarter-second after arriving. The case above only caught
    // it by accident: it normally finishes before the deadline, and did not under full-suite
    // load, which is what made it look order-dependent rather than simply wrong.
    //
    // Fake timers make the ordering the assertion instead of the race: the clock is driven
    // PAST the debounce deadline after the click, so the mount timer has certainly fired by
    // the time the request is inspected. Nothing is typed, so a correct debounce has nothing
    // to commit and must leave both the page and the request alone.
    vi.useFakeTimers({ shouldAdvanceTime: true });

    try {
      const api = mockApi(pagedRoutes);
      const user = userEvent.setup({ delay: null, advanceTimers: vi.advanceTimersByTime });
      renderSurface(<SystemPage />);
      await openLogsTab(user);

      api.set('/api/admin/logs', {
        body: { entries: fullPage, total: 120, page: 2, pageSize: LOG_PAGE_SIZE, loggers },
      });
      await user.click(screen.getByRole('button', { name: 'Next' }));
      await screen.findByText('Page 2 of 3');

      await vi.advanceTimersByTimeAsync(500);

      // Still page 2, and no page-1 request issued behind it.
      expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
      const request = api.callsTo('/api/admin/logs').at(-1);
      expect(request?.url.searchParams.get('page')).toBe('2');
    } finally {
      vi.useRealTimers();
    }
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
    await chooseFilter(user, 'Level', 'Warning and above');

    // Page 3 of an unfiltered store is a different set of rows from page 3 of a
    // filtered one, and may be past the new end entirely.
    const request = api.callsTo('/api/admin/logs').at(-1);
    expect(request?.url.searchParams.has('page')).toBe(false);
    expect(request?.url.searchParams.get('level')).toBe('Warning');
  });
});

describe('buildLogsQuery', () => {
  it('omits every filter and the page when nothing is narrowed', () => {
    // Tested directly rather than only through the surface: the filter-to-URL mapping
    // is what breaks silently, since a dropped parameter still renders a plausible
    // table of the wrong rows.
    expect(buildLogsQuery({ level: 'all', logger: '', message: '' }, 1)).toBe(
      `/api/admin/logs?pageSize=${LOG_PAGE_SIZE}`,
    );
  });

  it('sends every filter when they are set', () => {
    const query = buildLogsQuery(
      { level: 'Error', logger: 'Search.SearchService', message: 'probe' },
      1,
    );
    const params = new URL(query, 'http://localhost').searchParams;

    expect(params.get('level')).toBe('Error');
    expect(params.get('logger')).toBe('Search.SearchService');
    expect(params.get('message')).toBe('probe');
  });

  it('trims a whitespace-only logger or message rather than sending it', () => {
    // The store treats a whitespace filter as absent, so sending one would work by
    // accident; omitting it keeps the request honest about what was asked.
    const params = new URL(
      buildLogsQuery({ level: 'all', logger: '   ', message: '   ' }, 1),
      'http://localhost',
    ).searchParams;

    expect(params.has('logger')).toBe(false);
    expect(params.has('message')).toBe(false);
  });

  it('trims the surrounding whitespace off a message it does send', () => {
    const params = new URL(
      buildLogsQuery({ level: 'all', logger: '', message: '  probe  ' }, 1),
      'http://localhost',
    ).searchParams;

    expect(params.get('message')).toBe('probe');
  });

  it('sends the page only past the first', () => {
    expect(
      new URL(
        buildLogsQuery({ level: 'all', logger: '', message: '' }, 3),
        'http://localhost',
      ).searchParams.get('page'),
    ).toBe('3');
  });
});

/**
 * arb-ajrv: the Logs filter moved out of a bordered `.panel` headed "Filter" and
 * onto a PageToolbar row that is the tab panel's first child.
 *
 * This is the surface the toolbar contract had to be amended for (owner ruling):
 * LogsTab is a TAB PANEL, so "directly under PageHeader as its sibling" is
 * unsatisfiable here -- System owns the route's single <h1>. The amendment in
 * design-system/README.md permits a panel to carry its own toolbar because Tabs
 * partition a surface into sub-surfaces, each governing its own data set.
 *
 * Every assertion fails against the pre-migration markup: there was no
 * role="toolbar" in this tab, the controls were native <select>s, and the
 * "Filter" heading was present.
 */
describe('System logs filter toolbar', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('puts all three filter controls inside the toolbar, not beside it', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // CONTAINMENT: an empty toolbar rendered next to an untouched `.panel`
    // satisfies a bare getByRole('toolbar'), so each control is looked up
    // `within` the row.
    const toolbar = screen.getByRole('toolbar', { name: 'Log filters' });
    expect(within(toolbar).getByRole('button', { name: /^Level:/ })).toBeInTheDocument();
    expect(within(toolbar).getByRole('button', { name: /^Logger:/ })).toBeInTheDocument();
    expect(within(toolbar).getByRole('textbox', { name: 'Message' })).toBeInTheDocument();
  });

  it('carries a toolbar label distinct from any other on the route', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // PageToolbar requires an accessible name precisely so a page that grew a
    // second toolbar would not give a screen-reader user two indistinguishable
    // ones. A toolbar inside a tab panel is exactly that situation, so the name
    // says which data set it governs rather than naming the route.
    const toolbars = screen.getAllByRole('toolbar');
    const names = toolbars.map((toolbar) => toolbar.getAttribute('aria-label'));
    expect(names).toContain('Log filters');
    expect(new Set(names).size).toBe(names.length);
  });

  it('no longer renders the bordered Filter panel the toolbar replaced', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // Without this, adding a toolbar and leaving the panel would still pass.
    expect(screen.queryByRole('heading', { name: 'Filter' })).not.toBeInTheDocument();
    // The sibling panel is still there, so the absence above is not the page
    // having failed to render.
    expect(screen.getByRole('heading', { name: 'Logs' })).toBeInTheDocument();
  });

  it('contributes no heading from the toolbar row', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // The per-surface half of the no-heading rule. The amendment permits the
    // toolbar inside a tab panel; it does not relax this.
    const toolbar = screen.getByRole('toolbar', { name: 'Log filters' });
    expect(within(toolbar).queryAllByRole('heading')).toHaveLength(0);
  });

  it('keeps "and above" on every level, and not on Critical, in the menu items', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // The one place this migration could most plausibly lose meaning silently.
    // The minimum-severity semantic is visible ONLY in the option text, and a
    // migration that turned these into bare level names would reintroduce the
    // exact bug that made the old exact filter hide Error and Critical. The
    // strings are asserted as an exact sequence, not searched for.
    await user.click(screen.getByRole('button', { name: /^Level:/ }));

    const menu = screen.getByRole('menu', { name: /^Level:/ });
    const labels = within(menu)
      .getAllByRole('menuitemradio')
      .map((item) => item.textContent);

    expect(labels).toEqual([
      'All levels',
      'Information and above',
      'Warning and above',
      'Error and above',
      'Critical',
    ]);
  });

  it('states the active level on the trigger, so the filter is readable while closed', async () => {
    mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    // A <select> shows its value without being opened; a button reading only
    // "Level" loses that. The default is Warning, which the API applies as a
    // minimum -- so the trigger carries the same "and above" wording the item
    // does rather than a bare level name.
    expect(
      screen.getByRole('button', { name: 'Level: Warning and above' }),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /^Level:/ }));
    await user.click(screen.getByRole('menuitemradio', { name: 'Error and above' }));

    expect(screen.getByRole('button', { name: 'Level: Error and above' })).toBeInTheDocument();
  });

  it('asks the server for a level chosen from the menu', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.click(screen.getByRole('button', { name: /^Level:/ }));
    await user.click(screen.getByRole('menuitemradio', { name: 'Error and above' }));

    // Behaviour preserved control by control: the wire value stays the bare
    // level name even though the label says "and above".
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.get('level')).toBe('Error');
  });

  it('drops the level parameter when All levels is chosen from the menu', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.click(screen.getByRole('button', { name: /^Level:/ }));
    await user.click(screen.getByRole('menuitemradio', { name: 'All levels' }));

    // Omitted, not `level=all`: the store would match no row spelled "all".
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.has('level')).toBe(false);
  });

  it('populates the logger menu from the loggers the page returned', async () => {
    const api = mockApi(allRoutes);
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    await user.click(screen.getByRole('button', { name: /^Logger:/ }));

    const menu = screen.getByRole('menu', { name: /^Logger:/ });
    expect(
      within(menu)
        .getAllByRole('menuitemradio')
        .map((item) => item.textContent),
    ).toEqual(['All loggers', ...loggers]);

    await user.click(within(menu).getByRole('menuitemradio', { name: 'Search.SearchService' }));
    expect(api.callsTo('/api/admin/logs').at(-1)?.url.searchParams.get('logger')).toBe(
      'Search.SearchService',
    );
  });

  it('reads "Logger: All loggers" before any response has named one', async () => {
    // The logger options come from the query RESPONSE, unlike Activity's static
    // arrays -- so this trigger's label depends on loaded data and the menu is
    // empty of real loggers until the first page lands. The pending state shows
    // the unfiltered selection rather than a blank or a spinner, because "no
    // logger filter" is true before the response as well as after it.
    mockApi({ ...statusRoutes, '/api/admin/logs': { body: { ...logsBody, loggers: [] } } });
    const user = userEvent.setup();
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    expect(screen.getByRole('button', { name: 'Logger: All loggers' })).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /^Logger:/ }));
    const menu = screen.getByRole('menu', { name: /^Logger:/ });
    // Never an empty panel: the "All loggers" escape hatch is always offered.
    expect(
      within(menu)
        .getAllByRole('menuitemradio')
        .map((item) => item.textContent),
    ).toEqual(['All loggers']);
  });
});

describe('System logs message filter in the toolbar', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('still debounces from inside the toolbar, and still resets the page', async () => {
    // The 250ms debounce and the page reset stayed in LogsTab as caller logic
    // (owner ruling): PageToolbarInput owns no timer. This is the assertion that
    // the move did not take the behaviour with it -- three keystrokes still cost
    // one round trip, and the committed change still returns to page 1.
    const api = mockApi(allRoutes);
    const user = userEvent.setup({ delay: null });
    renderSurface(<SystemPage />);
    await openLogsTab(user);

    const before = api.callsTo('/api/admin/logs').length;
    await user.type(screen.getByRole('textbox', { name: 'Message' }), 'pro');
    expect(api.callsTo('/api/admin/logs')).toHaveLength(before);

    await vi.advanceTimersByTimeAsync(250);

    const after = api.callsTo('/api/admin/logs');
    expect(after).toHaveLength(before + 1);
    expect(after.at(-1)?.url.searchParams.get('message')).toBe('pro');
    // Page 1 is the omitted parameter, not `page=1`.
    expect(after.at(-1)?.url.searchParams.has('page')).toBe(false);
  });
});
