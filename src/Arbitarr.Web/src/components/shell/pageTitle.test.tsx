import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

const ROUTES: ReadonlyArray<[string, string]> = [
  ['/', 'Dashboard'],
  ['/search', 'Search'],
  ['/rules', 'Rules'],
  ['/suppressions', 'Suppressions'],
  ['/settings', 'Settings'],
  ['/system', 'System'],
];

/**
 * AC2b: exactly one page title per view, and it lives in the content pane.
 *
 * The failure this guards against is a title in the top bar *and* a title in
 * the page: two <h1>s, a doubled announcement for screen readers, and the
 * classic *arr-clone look of the same words printed twice on screen.
 */
describe('page title (AC2b)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it.each(ROUTES)('%s has exactly one h1 reading %s', (path, title) => {
    renderApp(path);

    const headings = screen.getAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent(title);
  });

  it('places the title inside the content pane, not the top bar', () => {
    renderApp('/');

    const heading = screen.getByRole('heading', { level: 1 });
    expect(screen.getByRole('main')).toContainElement(heading);
    expect(screen.getByRole('banner')).not.toContainElement(heading);
  });
});
