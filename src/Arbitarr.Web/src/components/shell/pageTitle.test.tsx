import { screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi, signedIn } from '../../test/mockApi';
import { ROUTES } from '../../routes.titles';

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
    // arb-7m7: the shell only mounts once RequireSession has a definite answer.
    mockApi({ ...signedIn() });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(ROUTES)('%s has exactly one h1 reading %s', async (path, title) => {
    renderApp(path);

    const headings = await screen.findAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent(title);
  });

  it('places the title inside the content pane, not the top bar', async () => {
    renderApp('/');

    const heading = await screen.findByRole('heading', { level: 1 });
    expect(screen.getByRole('main')).toContainElement(heading);
    expect(screen.getByRole('banner')).not.toContainElement(heading);
  });
});
