import { screen, within } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

/**
 * AC5's nav table, in order. Duplicated here on purpose: a test that imports
 * NAV_ENTRIES and iterates it asserts only that the component agrees with
 * itself, and would stay green if an entry were dropped from the source.
 */
const EXPECTED = ['Dashboard', 'Search', 'Rules', 'Suppressions', 'Settings', 'System'];

describe('SidebarNav', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it('renders exactly six nav entries in AC5 order', () => {
    renderApp('/');

    const nav = screen.getByRole('navigation', { name: 'Main' });
    const links = within(nav).getAllByRole('link');

    // Exact count, not `>=`: a dropped entry and a smuggled-in one must both
    // fail. `toHaveLength(6)` catches the first; comparing the whole ordered
    // array catches the second and the reordering case as well.
    expect(links).toHaveLength(6);
    expect(links.map((link) => link.textContent?.trim())).toEqual(EXPECTED);
  });

  it('points each entry at its own path', () => {
    renderApp('/');

    const nav = screen.getByRole('navigation', { name: 'Main' });
    const links = within(nav).getAllByRole('link');

    expect(links.map((link) => link.getAttribute('href'))).toEqual([
      '/',
      '/search',
      '/rules',
      '/suppressions',
      '/settings',
      '/system',
    ]);
  });

  it('marks only the current route active', () => {
    renderApp('/rules');

    const nav = screen.getByRole('navigation', { name: 'Main' });
    const current = within(nav)
      .getAllByRole('link')
      .filter((link) => link.getAttribute('aria-current') === 'page');

    expect(current).toHaveLength(1);
    expect(current[0]).toHaveTextContent('Rules');
  });

  it('does not leave Dashboard active on a non-index route', () => {
    // Without `end` on the index NavLink, "/" prefix-matches every path and the
    // Dashboard row stays lit on every surface.
    renderApp('/settings');

    const dashboard = screen.getByRole('link', { name: 'Dashboard' });
    expect(dashboard).not.toHaveAttribute('aria-current', 'page');
  });
});
