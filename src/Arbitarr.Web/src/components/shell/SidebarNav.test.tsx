import { screen, within } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi, signedIn } from '../../test/mockApi';

/**
 * AC5's nav table, in order. Duplicated here on purpose: a test that imports
 * NAV_ENTRIES and iterates it asserts only that the component agrees with
 * itself, and would stay green if an entry were dropped from the source.
 */
const EXPECTED = [
  'Dashboard',
  'Search',
  'Rules',
  'Suppressions',
  'Activity',
  'Settings',
  'System',
];

describe('SidebarNav', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    // arb-7m7: the shell only mounts once RequireSession has a definite answer.
    mockApi({ ...signedIn() });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders exactly seven nav entries in AC5 order', async () => {
    renderApp('/');

    const nav = await screen.findByRole('navigation', { name: 'Main' });
    const links = within(nav).getAllByRole('link');

    // Exact count, not `>=`: a dropped entry and a smuggled-in one must both
    // fail. `toHaveLength(7)` catches the first; comparing the whole ordered
    // array catches the second and the reordering case as well.
    //
    // Seven since #55 added Activity -- see NAV_ENTRIES' comment for why that is
    // a product surface rather than a section.
    expect(links).toHaveLength(7);
    expect(links.map((link) => link.textContent?.trim())).toEqual(EXPECTED);
  });

  it('points each entry at its own path', async () => {
    renderApp('/');

    const nav = await screen.findByRole('navigation', { name: 'Main' });
    const links = within(nav).getAllByRole('link');

    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '/',
      '/search',
      '/rules',
      '/suppressions',
      '/activity',
      '/settings',
      '/system',
    ]);
  });

  it('marks only the current route active', async () => {
    renderApp('/rules');

    const nav = await screen.findByRole('navigation', { name: 'Main' });
    const current = within(nav)
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]).toHaveTextContent('Rules');
  });

  it('does not leave Dashboard active on a non-index route', async () => {
    // Without `end` on the index NavLink, "/" prefix-matches every path and the
    // Dashboard row stays lit on every surface.
    renderApp('/settings');

    const dashboard = await screen.findByRole('link', { name: 'Dashboard' });
    expect(dashboard).not.toHaveAttribute('aria-current', 'page');
  });
});
