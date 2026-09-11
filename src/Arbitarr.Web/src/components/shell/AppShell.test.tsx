import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { useTableDensityStore } from '../../state/tableDensityStore';

describe('AppShell', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    useTableDensityStore.setState({ density: 'expanded' });
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

  // arb-br4. The attribute is asserted rather than the resulting padding: jsdom
  // has no layout engine and does not apply CSS modules, so a computed padding
  // reads back as the empty string whatever the rule says. The contract this
  // component actually owns is "the wrapper carries the store's density", and
  // the single rule in surface.module.css is what turns that into row height.
  it('marks the content wrapper with the default density', () => {
    renderApp('/search');

    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toHaveAttribute('data-density', 'expanded');
  });

  it('reflects a compact density from the store onto the content wrapper', () => {
    useTableDensityStore.setState({ density: 'compact' });
    renderApp('/search');

    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toHaveAttribute('data-density', 'compact');
  });

  // The wrapper must be an ANCESTOR of the routed content, not a sibling: the
  // compact rule is a descendant selector, so an attribute written on an
  // element beside the page would satisfy the two assertions above and still
  // style nothing.
  it('puts the density wrapper above the routed content', () => {
    renderApp('/search');

    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toContainElement(screen.getByRole('heading', { level: 1, name: 'Search' }));
  });
});
