import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import userEvent from '@testing-library/user-event';
import { screen, waitFor } from '@testing-library/react';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi, signedIn, signedOut } from '../../test/mockApi';
import { ROUTES, LOGIN_TITLE } from '../../routes.titles';

/**
 * Distinct from pageTitle.test.tsx (AC2b, which asserts the in-page <h1>).
 * This suite asserts document.title -- the browser tab / history / bookmark
 * label -- which is a different surface entirely and must not be confused
 * with the content-pane heading.
 */
describe('document title per route', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    // arb-7m7: the shell (and its title effect) only mounts once
    // RequireSession has a definite answer.
    mockApi({ ...signedIn() });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(ROUTES)('%s sets document.title', async (path, label) => {
    renderApp(path);

    await waitFor(() => expect(document.title).toBe(`${label} — Arbitarr`));
  });

  it('sets "Page not found — Arbitarr" for an unknown path', async () => {
    renderApp('/does-not-exist');

    await waitFor(() => expect(document.title).toBe('Page not found — Arbitarr'));
  });

  it('updates the title on navigation without a full reload', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await waitFor(() => expect(document.title).toBe('Dashboard — Arbitarr'));

    await user.click(await screen.findByRole('link', { name: /search/i }));

    await waitFor(() => expect(document.title).toBe('Search — Arbitarr'));
  });

  it('sets the login title, not the requested route\'s, for a signed-out deep link (arb-7m7)', async () => {
    mockApi({ ...signedOut() });
    // the stale title the bug left behind; without it this assertion is
    // vacuous in isolation
    document.title = 'System — Arbitarr';

    renderApp('/system');

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to Arbitarr' })).toBeInTheDocument();
    expect(document.title).toBe(LOGIN_TITLE);
  });
});
