import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp, renderAppWithBrowserHistory } from './test/renderApp';
import { useAdminKeyStore } from './state/adminKeyStore';
import { mockApi, signedIn } from './test/mockApi';

describe('routing', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    // These tests mount the real surfaces, each of which fetches on mount.
    // Every assertion here targets a PageHeader <h1>, which renders above the
    // query state, so they would pass against a rejected fetch too -- but
    // "passes without a network" should be a property of this file, not a
    // coincidence of where the headings sit relative to the data.
    //
    // #44: signedIn() is required, not decorative. These tests navigate WITHIN
    // the guarded shell, so an unmocked /api/auth/session would answer 501, and
    // RequireSession would render the children anyway (it fails open on error)
    // -- meaning the tests would still pass while exercising a state the app
    // does not normally serve. Mocking a real session keeps them about routing
    // under the conditions an operator actually meets.
    mockApi({ ...signedIn() });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('resolves a deep link directly to its surface', async () => {
    renderApp('/suppressions');

    expect(
      await screen.findByRole('heading', { level: 1, name: 'Suppressions' }),
    ).toBeInTheDocument();
  });

  it('navigates forward and back through history', async () => {
    const user = userEvent.setup();
    // Browser history, not MemoryRouter: MemoryRouter ignores popstate, so
    // history.back() there would move the browser and leave the router put --
    // the assertions below would pass without the router having navigated.
    renderAppWithBrowserHistory('/');

    await user.click(screen.getByRole('link', { name: 'Search' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'Search' })).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'System' }));
    expect(await screen.findByRole('heading', { level: 1, name: 'System' })).toBeInTheDocument();

    window.history.back();
    expect(await screen.findByRole('heading', { level: 1, name: 'Search' })).toBeInTheDocument();

    window.history.forward();
    expect(await screen.findByRole('heading', { level: 1, name: 'System' })).toBeInTheDocument();
  });

  it('renders the 404 inside the shell so navigation is still available', () => {
    renderApp('/no-such-page');

    expect(screen.getByRole('heading', { level: 1, name: 'Page not found' })).toBeInTheDocument();
    // The point of a 404 inside the shell: the operator is not stranded on a
    // bare error page with no way back.
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('routes every sidebar destination to something other than the 404', () => {
    // Catches a nav entry whose path has no matching route -- the link would
    // render fine and quietly land on the not-found page.
    for (const path of ['/', '/search', '/rules', '/suppressions', '/settings', '/system']) {
      const { unmount } = renderApp(path);
      expect(screen.queryByRole('heading', { level: 1, name: 'Page not found' })).toBeNull();
      unmount();
    }
  });
});
