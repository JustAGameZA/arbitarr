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
      repeatCount: 1,
      lastRepeatedAt: null,
    },
    {
      occurredAt: '2026-09-07T12:29:00+00:00',
      kind: 'sourceFailed',
      summary: "Source 'NZBHydra2' failed during a snapshot refresh",
      reason: 'The upstream request timed out.',
      sourceDisplayName: 'NZBHydra2',
      detail: null,
      repeatCount: 1,
      lastRepeatedAt: null,
    },
  ],
  nextCursor: null,
};

/**
 * Opens a toolbar filter menu by its trigger and picks an item.
 *
 * The trigger name carries the ACTIVE value ("Kind: Everything"), which is what
 * replaced the visible <select>. Addressing it that way means a regression that
 * stops the trigger reflecting the current filter fails here rather than
 * shipping a menu whose label is permanently stale.
 */
async function chooseFilter(trigger: string, item: string) {
  await userEvent.click(screen.getByRole('button', { name: trigger }));
  await userEvent.click(screen.getByRole('menuitemradio', { name: item }));
}

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
   * arb-itw: repeats are folded onto one row by the SERVER, and the row reports the
   * count it was given. The badge appears only above 1 — rendering "×1" on every row
   * would add a column of noise to say nothing, and it is the departure from 1 that
   * is the operational signal.
   *
   * Asserted PER ROW rather than "the badge is somewhere on the page", because the
   * implementation most likely to be wrong is one that renders the badge for every
   * row once any row repeats.
   */
  it('badges a repeated row with its count and leaves single occurrences unbadged', async () => {
    mockApi({
      '/api/activity': {
        body: {
          events: [
            {
              ...page.events[1],
              summary: 'Source failed to respond',
              repeatCount: 71,
              lastRepeatedAt: '2026-09-07T12:35:00+00:00',
            },
            { ...page.events[0], summary: 'Search served from cache (14 results)' },
          ],
          nextCursor: null,
        },
      },
    });
    renderSurface(<ActivityPage />);

    const repeated = (await screen.findByText('Source failed to respond')).closest('tr');
    expect(repeated).not.toBeNull();
    expect(within(repeated!).getByText('×71')).toBeInTheDocument();

    // The single-occurrence row in the SAME table carries no badge, which is what makes
    // the assertion above about this row rather than about the page.
    const single = screen.getByText('Search served from cache (14 results)').closest('tr');
    expect(single).not.toBeNull();
    expect(within(single!).queryByText(/^×/)).toBeNull();
  });

  /**
   * The tooltip pairs the count with the last repeat. "71 times" alone cannot
   * distinguish a storm that has stopped from one still going, which is the
   * question an operator is actually asking when they see a large count.
   */
  it('explains the repeat count and when it last happened in a tooltip', async () => {
    mockApi({
      '/api/activity': {
        body: {
          events: [
            {
              ...page.events[1],
              summary: 'Source failed to respond',
              repeatCount: 71,
              lastRepeatedAt: '2026-09-07T12:35:00+00:00',
            },
          ],
          nextCursor: null,
        },
      },
    });
    renderSurface(<ActivityPage />);

    const badge = await screen.findByText('×71');
    expect(badge).toHaveAttribute('title', expect.stringContaining('repeated 71 times'));
    expect(badge).toHaveAttribute('title', expect.stringContaining('last at'));
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

    await chooseFilter('Kind: Everything', 'Decisions');

    const calls = api.callsTo('/api/activity');
    const latest = calls[calls.length - 1];
    expect(latest.url.searchParams.get('kind')).toBe('decision');
  });

  it('filters by time window, sending an absolute UTC instant', async () => {
    const api = mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    await chooseFilter('Time: Last 24 hours', 'Last 7 days');

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
    await chooseFilter('Kind: Everything', 'Decisions');

    const calls = api.callsTo('/api/activity');
    const latest = calls[calls.length - 1];
    // Carrying the old cursor into a new filter would resume from a position in
    // a result set that no longer exists, skipping the newest matching rows.
    expect(latest.url.searchParams.has('cursor')).toBe(false);
    expect(latest.url.searchParams.get('kind')).toBe('decision');
  });

  /**
   * The two <select>s lived in a panel headed "Filter". Moving them into the
   * toolbar must remove that panel, not leave it as an empty shell.
   *
   * The positive control comes first (CLAUDE.md §4): `queryByRole('heading',
   * {name})` returns null just as happily when the query shape is wrong or the
   * surface never rendered, so the assertion that "Filter" is absent proves
   * nothing on its own. Finding "Events" by the SAME query shape, in the same
   * render, demonstrates that a surviving heading WOULD be detected.
   */
  it('leaves no Filter panel heading behind, the filters having moved into the toolbar', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    await screen.findByText('Search served from cache (14 results)');

    expect(screen.queryByRole('heading', { name: 'Events' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Filter' })).not.toBeInTheDocument();

    // And the controls really did survive the move, rather than vanishing with
    // their panel: absence alone would also pass if the filters were deleted.
    expect(screen.getByRole('button', { name: 'Kind: Everything' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Time: Last 24 hours' })).toBeInTheDocument();
  });

  it('names the source on a row that has one', async () => {
    mockApi({ '/api/activity': { body: page } });
    renderSurface(<ActivityPage />);

    const table = await screen.findByRole('table');
    expect(within(table).getByText('NZBHydra2')).toBeInTheDocument();
  });
});
