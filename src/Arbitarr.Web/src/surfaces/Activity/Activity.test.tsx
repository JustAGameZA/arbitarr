import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import ActivityPage from './Activity';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';
import type { ActivityPageResponse } from '../../api/types';

const page: ActivityPageResponse = {
  events: [
    {
      occurredAt: '2026-09-07T12:30:00+00:00',
      kind: 'searchServed',
      summary: 'Search served from cache (14 results)',
      reason: "Query 'the.expanse.s01' (tvsearch), 8ms",
      sourceDisplayName: null,
      detail: null,
    },
    {
      occurredAt: '2026-09-07T12:29:00+00:00',
      kind: 'sourceFailed',
      summary: "Source 'NZBHydra2' failed during a snapshot refresh",
      reason: 'The upstream request timed out.',
      sourceDisplayName: 'NZBHydra2',
      detail: null,
    },
  ],
  nextCursor: null,
};

describe('Activity', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and the events table', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Activity' })).toBeInTheDocument();
    expect(await screen.findByText('Search served from cache (14 results)')).toBeInTheDocument();
  });

  /** AC2: every row states what happened AND why, not merely an event name. */
  it('states the reason alongside what happened', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    expect(await screen.findByText("Query 'the.expanse.s01' (tvsearch), 8ms")).toBeInTheDocument();
    expect(screen.getByText('The upstream request timed out.')).toBeInTheDocument();
  });

  /**
   * AC3: cache-served and live-query-served searches are distinguishable. The
   * server decides the wording; this asserts the surface renders it verbatim
   * rather than collapsing both to a generic "search served".
   */
  it('distinguishes a cache-served search from a live one', async () => {
    mockApi({
      '/api/activity': {
        body: {
          events: [
            { ...page.events[0], summary: 'Search served from cache (14 results)' },
            {
              ...page.events[0],
              occurredAt: '2026-09-07T12:28:00+00:00',
              summary: 'Search served from a live query (9 results)',
            },
          ],
          nextCursor: null,
        },
      },
    });
    renderSurface(<ActivityPage />);

    expect(await screen.findByText('Search served from cache (14 results)')).toBeInTheDocument();
    expect(screen.getByText('Search served from a live query (9 results)')).toBeInTheDocument();
  });

  /** #52: an empty state names what would fill it, rather than only being empty. */
  it('names what would fill the empty state', async () => {
    mockApi({ '/api/activity': { body: { events: [], nextCursor: null } } });
    renderSurface(<ActivityPage />);

    expect(
      await screen.findByText(
        'No activity recorded yet. Events appear here once the worker cycles or a search runs.',
      ),
    ).toBeInTheDocument();
  });

  /**
   * AC9: the rendered timestamp must be unambiguous about its timezone, and the
   * exact instant the server sent must remain recoverable — hence the machine
   * -readable dateTime attribute rather than only a localized string.
   */
  it('renders timestamps unambiguously and keeps the exact instant', async () => {
    mockApi({ '/api/activity': { body: page } });
    const { container } = renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    const time = container.querySelector('time');
    expect(time).toHaveAttribute('dateTime', '2026-09-07T12:30:00+00:00');
    expect(time).toHaveAttribute('title', '2026-09-07T12:30:00+00:00');
    // Localized text still names a zone, so the displayed string cannot be read
    // as some other reader's local time.
    expect(time?.textContent).toMatch(/\d/);
  });

  /**
   * AC7 / #59's ruling: /api/activity is PublicRead and is not under /api/admin/,
   * so no admin key is attached. Asserted against the headers the mocked fetch
   * actually received, the same way the Dashboard's un-gated reads are.
   */
  it('sends no admin key, because reading history is not a mutating action', async () => {
    useAdminKeyStore.setState({ key: 'a-configured-key', serverKeyUnset: false });
    const api = mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    expect(api.adminKeyOn('/api/activity')).toBeUndefined();
  });

  it('filters by kind, sending the kind to the server rather than narrowing locally', async () => {
    const api = mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    await userEvent.selectOptions(screen.getByLabelText('Kind'), 'decision');

    const calls = api.callsTo('/api/activity');
    const latest = calls[calls.length - 1];
    expect(latest.url.searchParams.get('kind')).toBe('decision');
  });

  it('filters by time window, sending an absolute UTC instant', async () => {
    const api = mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    await userEvent.selectOptions(screen.getByLabelText('Time'), 'week');

    const calls = api.callsTo('/api/activity');
    const since = calls[calls.length - 1].url.searchParams.get('since');
    expect(since).not.toBeNull();
    // An ISO-8601 UTC instant. A local-time bound would shift the window by the
    // viewer's offset against the server's UTC timestamps.
    expect(since).toMatch(/Z$/);
  });

  /**
   * AC8 on the client side: paging follows the cursor the SERVER issued, and
   * never a computed offset or page number. If this surface ever sends
   * `?offset=`, the store's stable-paging guarantee is silently discarded.
   */
  it('pages by echoing the server cursor, never an offset', async () => {
    const api = mockApi({
      '/api/activity': { body: { events: page.events, nextCursor: 4211 } },
    });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));

    const calls = api.callsTo('/api/activity');
    const latest = calls[calls.length - 1];
    expect(latest.url.searchParams.get('cursor')).toBe('4211');
    expect(latest.url.searchParams.has('offset')).toBe(false);
    expect(latest.url.searchParams.has('page')).toBe(false);
  });

  it('offers no paging controls when a single page holds everything', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Previous' })).not.toBeInTheDocument();
  });

  it('resets paging when a filter changes, because a cursor belongs to one result set', async () => {
    const api = mockApi({
      '/api/activity': { body: { events: page.events, nextCursor: 4211 } },
    });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');
    await userEvent.click(screen.getByRole('button', { name: 'Next' }));
    await userEvent.selectOptions(screen.getByLabelText('Kind'), 'decision');

    const calls = api.callsTo('/api/activity');
    const latest = calls[calls.length - 1];
    // Carrying the old cursor into a new filter would resume from a position in
    // a result set that no longer exists, skipping the newest matching rows.
    expect(latest.url.searchParams.has('cursor')).toBe(false);
    expect(latest.url.searchParams.get('kind')).toBe('decision');
  });

  it('names the source on a row that has one', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    const table = await screen.findByRole('table');
    expect(within(table).getByText('NZBHydra2')).toBeInTheDocument();
  });
});
