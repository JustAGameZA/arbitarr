import { beforeEach, describe, expect, it } from 'vitest';
import userEvent from '@testing-library/user-event';
import { screen } from '@testing-library/react';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { ROUTES } from '../../routes.titles';

/**
 * Distinct from pageTitle.test.tsx (AC2b, which asserts the in-page <h1>).
 * This suite asserts document.title -- the browser tab / history / bookmark
 * label -- which is a different surface entirely and must not be confused
 * with the content-pane heading.
 */
describe('document title per route', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it.each(ROUTES)('%s sets document.title', (path, label) => {
    renderApp(path);

    expect(document.title).toBe(`${label} — Arbitarr`);
  });

  it('sets "Page not found — Arbitarr" for an unknown path', () => {
    renderApp('/does-not-exist');

    expect(document.title).toBe('Page not found — Arbitarr');
  });

  it('updates the title on navigation without a full reload', async () => {
    const user = userEvent.setup();
    renderApp('/');

    expect(document.title).toBe('Dashboard — Arbitarr');

    await user.click(screen.getByRole('link', { name: /search/i }));

    expect(document.title).toBe('Search — Arbitarr');
  });
});
