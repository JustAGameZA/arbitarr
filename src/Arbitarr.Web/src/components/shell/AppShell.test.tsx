import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

describe('AppShell', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it('exposes a navigation landmark', () => {
    renderApp('/');

    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('keeps the same sidebar element mounted across a route change', async () => {
    const user = userEvent.setup();
    renderApp('/');

    const before = screen.getByRole('navigation', { name: 'Main' });
    await user.click(screen.getByRole('link', { name: 'Rules' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();

    // Identity, not mere presence: `toBe` fails if the shell remounts, which is
    // what would reset sidebar scroll position and flash the nav between routes.
    expect(screen.getByRole('navigation', { name: 'Main' })).toBe(before);
  });

  it('renders route content inside the main landmark', () => {
    renderApp('/search');

    const main = screen.getByRole('main');
    expect(main).toContainElement(screen.getByRole('heading', { level: 1, name: 'Search' }));
  });
});
