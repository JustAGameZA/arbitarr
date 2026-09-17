import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SettingsPage from './Settings';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

/*
 * arb-tk0r: every fixture group's `groupDisplayName` is DELIBERATELY different
 * from its `group` identifier, and that difference is the positive control for
 * the heading and nav-label assertions below. With a fixture whose two names
 * matched, a page that rendered the raw identifier -- the exact defect arb-tk0r
 * fixes -- would satisfy every "the heading is X" assertion here while being
 * completely wrong, so the test would prove nothing. Keep them distinct, and
 * keep the identifiers in the code-identifier shape the real catalog serves
 * (CacheWindow, not "Caching"), so the slug assertions exercise the real input.
 */
const settings = [
  {
    key: 'Cache.FreshUntil',
    group: 'CacheWindow',
    groupDisplayName: 'Caching',
    displayName: 'Fresh until',
    rationale: 'How long a cached snapshot is served without revalidation.',
    requiresRestart: false,
    isBoolean: false,
    value: '00:05:00',
    min: '00:01:00',
    max: '01:00:00',
    noMaximumReason: null,
    restartReason: null,
    governedTable: null,
    governedTableRows: null,
  },
  {
    key: 'Search.RecentLogSize',
    group: 'SearchObservability',
    groupDisplayName: 'Observability',
    displayName: 'Recent search log size',
    rationale: 'How many recent searches are retained for the dashboard.',
    requiresRestart: true,
    isBoolean: false,
    value: '200',
    min: '10',
    max: null,
    noMaximumReason: 'The log is bounded by the retention window, not by a row ceiling.',
    restartReason: 'The ring buffer is sized once at startup.',
    governedTable: 'RecentSearches',
    governedTableRows: 143,
  },
];

describe('Settings', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and one panel per group', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Caching' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Observability' })).toBeInTheDocument();
  });

  /*
   * arb-tk0r. The two assertions are a pair and neither is sufficient alone:
   * the first says the display name IS rendered, the second says the identifier
   * is NOT. Without the second, a page rendering both (a heading plus a stray
   * identifier caption) would pass; without the first, a page rendering neither
   * would. Asserted PER GROUP over both fixture groups rather than "some
   * heading is a display name", because a single group left on the identifier
   * is exactly the defect, and it is what a one-group check would miss.
   */
  it('renders the server display name as each group heading, never the identifier', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    await screen.findByRole('heading', { name: 'Caching' });

    for (const group of settings) {
      expect(
        screen.getByRole('heading', { name: group.groupDisplayName }),
        `no heading for display name ${group.groupDisplayName}`,
      ).toBeInTheDocument();
      expect(
        screen.queryByRole('heading', { name: group.group }),
        `identifier ${group.group} is rendered as a heading`,
      ).toBeNull();
    }
  });

  /*
   * The identifier keeps driving the anchor (arb-tk0r), so rewording a heading
   * cannot move a bookmarked `#section` link. Asserted against the identifier's
   * slug specifically, and paired with the display name's slug being absent:
   * the fixture names differ, so "cachewindow" and "caching" are distinct ids
   * and a switch to slugifying the display name fails this rather than passing
   * by coincidence.
   *
   * Note the identifier slugs have no hyphen. slugifyGroup splits on
   * NON-ALPHANUMERICS, and a camel-case identifier contains none, so
   * "CacheWindow" lowercases whole to "cachewindow". That is the real behaviour
   * against the real input shape (the server sends enum member names), which is
   * exactly why the fixtures use that shape rather than prose.
   */
  it('derives each section anchor from the group identifier, not the display name', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    await screen.findByRole('heading', { name: 'Caching' });

    expect(document.getElementById('cachewindow')).not.toBeNull();
    expect(document.getElementById('searchobservability')).not.toBeNull();
    expect(document.getElementById('caching')).toBeNull();
    expect(document.getElementById('observability')).toBeNull();
  });

  /*
   * Section nav (arb-5oe). jsdom evaluates no media queries and implements no
   * IntersectionObserver, so neither the sticky layout nor the active-entry
   * highlight is exercised here -- those are manual/browser checks. What these
   * assert is the part jsdom CAN see and the part that actually rots: that the
   * nav and the rendered sections come from one source, so every link has a
   * real target and no section is missing from the list.
   */
  describe('section nav', () => {
    it('lists every static section and every catalog group, in page order', async () => {
      mockApi({ '/api/admin/settings': { body: settings } });
      renderSurface(<SettingsPage />);

      // Awaited: the dynamic entries only exist once the catalog resolves.
      await screen.findByRole('heading', { name: 'Caching' });

      const nav = screen.getByRole('navigation', { name: 'Settings sections' });
      const labels = within(nav)
        .getAllByRole('link')
        .map((link) => link.textContent);

      expect(labels).toEqual([
        'Account',
        'Sources',
        'Sonarr',
        // Immediately after Sonarr: the two *arr instances read as a pair in the
        // nav even though the server keeps their contracts apart (arb-6l9b.2).
        'Radarr',
        'API keys',
        'Notifications',
        'AI backend',
        'Caching',
        'Observability',
      ]);
    });

    it('points every nav link at an element that exists on the page', async () => {
      mockApi({ '/api/admin/settings': { body: settings } });
      renderSurface(<SettingsPage />);

      await screen.findByRole('heading', { name: 'Caching' });

      const nav = screen.getByRole('navigation', { name: 'Settings sections' });
      const links = within(nav).getAllByRole('link');

      // Asserted PER LINK, not "some link resolves": a single dead anchor is
      // exactly the defect this guards, and a loop that stopped at the first
      // match would pass with five of nine broken. The count is pinned too,
      // so an empty list cannot satisfy a per-item assertion vacuously.
      expect(links).toHaveLength(9);
      for (const link of links) {
        const href = link.getAttribute('href');
        expect(href).toMatch(/^#.+/);
        const target = document.getElementById((href as string).slice(1));
        expect(target, `no element with id for ${href}`).not.toBeNull();
      }
    });

    it('adds a nav entry automatically when the catalog grows a group', async () => {
      // The load-bearing property: the nav is derived from the same
      // groupSettings() call that renders the panels, so a group the server
      // adds appears here with no frontend change. A hand-written second list
      // would pass every other test in this file and fail only this one.
      mockApi({
        '/api/admin/settings': {
          body: [
            ...settings,
            {
              ...settings[0],
              key: 'Ingest.BatchSize',
              group: 'IngestPipeline',
              groupDisplayName: 'Ingest pipeline',
              displayName: 'Batch size',
            },
          ],
        },
      });
      renderSurface(<SettingsPage />);

      await screen.findByRole('heading', { name: 'Ingest pipeline' });

      const nav = screen.getByRole('navigation', { name: 'Settings sections' });
      const link = within(nav).getByRole('link', { name: 'Ingest pipeline' });

      // The slug is derived from the IDENTIFIER, so it is the identifier's
      // casing that collapses ("IngestPipeline" -> "ingestpipeline") and the
      // display name's space plays no part -- the anchor is stable against a
      // reworded heading, which is the arb-tk0r property. A derived slug is
      // also the case a known-names map would not have covered.
      expect(link).toHaveAttribute('href', '#ingestpipeline');
      expect(document.getElementById('ingestpipeline')).not.toBeNull();
    });

    it('still renders exactly one h1', async () => {
      mockApi({ '/api/admin/settings': { body: settings } });
      renderSurface(<SettingsPage />);

      await screen.findByRole('heading', { name: 'Caching' });

      // AC2b. The nav is a list of links with an aria-label, never a heading;
      // captioning it is the easiest way to break this.
      expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    });
  });

  it('renders bounds, rationale and every explanatory field the DTO carries', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    expect(await screen.findByText('Fresh until')).toBeInTheDocument();
    expect(screen.getByText('00:01:00')).toBeInTheDocument();
    expect(screen.getByText('01:00:00')).toBeInTheDocument();

    // The four fields the legacy admin-settings.js never rendered. Without
    // them the operator sees a bound with no account of why it is there --
    // which is precisely what AC10 asks for.
    expect(
      screen.getByText('The log is bounded by the retention window, not by a row ceiling.'),
    ).toBeInTheDocument();
    expect(screen.getByText('The ring buffer is sized once at startup.')).toBeInTheDocument();
    expect(screen.getByText('RecentSearches')).toBeInTheDocument();
    expect(screen.getByText(/143 rows today/)).toBeInTheDocument();
    // Scoped to this setting's own row (arb-yeg): the Sources section below
    // renders its own "Restart required" badge with the same wording once
    // that convention is shared, so a bare screen.getByText would ambiguously
    // match either and fail on the duplicate.
    const recentLogSetting = screen.getByText('Recent search log size').closest('div');
    expect(recentLogSetting).not.toBeNull();
    expect(within(recentLogSetting as HTMLElement).getByText('Restart required')).toBeInTheDocument();
  });

  it('sends an out-of-bounds value to the server and shows its rejection unchanged', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      '/api/admin/settings': { body: settings },
      '/api/admin/settings/Cache.FreshUntil': {
        status: 400,
        body: { error: "Cache.FreshUntil must be between 00:01:00 and 01:00:00; got 10:00:00." },
      },
    });
    renderSurface(<SettingsPage />);

    const field = await screen.findByLabelText('Fresh until value');
    await user.clear(field);
    await user.type(field, '10:00:00');
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]);

    // The server's exact words -- AC10 forbids paraphrasing or inventing one -- and they are
    // ANNOUNCED, not merely rendered: an operator who has just pressed Save is not necessarily
    // looking at the field that failed, so the message carries role="alert" (Settings.tsx) to
    // reach a screen reader without one.
    //
    // Found by text and then asserted to BE an alert, rather than queried by role: this surface
    // mounts the API-keys section (#88) alongside the settings panels, and that section raises its
    // own alert here because this test does not mock /api/admin/keys. A bare findByRole would
    // therefore be ambiguous, and widening the mock to silence it would couple every settings test
    // to an unrelated section's endpoints. Asserting the message IS an alert -- rather than just
    // finding the text -- is the point, so that dropping role="alert" still fails here.
    const rejection = await screen.findByText(
      'Cache.FreshUntil must be between 00:01:00 and 01:00:00; got 10:00:00.',
    );
    expect(rejection).toHaveAttribute('role', 'alert');

    // The request went out unaltered. The legacy page had an isWithinBounds()
    // pre-check that blocked this call entirely and displayed a message of its
    // own, so the server's real answer was never seen and a value it would
    // have accepted could not be set when the two disagreed.
    const put = api.calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe('/api/admin/settings/Cache.FreshUntil');
    expect(JSON.parse(put!.body!)).toEqual({ value: '10:00:00' });

    // And the rejected entry stays put, so it can be corrected rather than retyped.
    expect(field).toHaveValue('10:00:00');
  });

  it('attaches the admin key to its GET and its PUT', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    await user.click((await screen.findAllByRole('button', { name: 'Save' }))[0]);

    expect(api.calls.find((call) => call.method === 'GET')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
    expect(api.calls.find((call) => call.method === 'PUT')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
  });

  it('keeps the affordance and the stored key on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({ '/api/admin/settings': { status: 503, body: { error: 'admin key not configured' } } });
    renderSurface(<SettingsPage />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the page explains the server-side gap and does not ask for a key
    // the operator has already supplied.
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });
});
