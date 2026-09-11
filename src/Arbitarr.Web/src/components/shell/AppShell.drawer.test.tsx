import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

/**
 * The off-canvas drawer (#48). jsdom does not evaluate media queries, so the
 * toggle button is always in the DOM regardless of viewport -- CSS is what
 * makes it invisible/inert above 768px (see TopBar.module.css). These tests
 * therefore cover the JS behaviour (open/close state, Escape, backdrop,
 * auto-close-on-navigate, focus management, aria-expanded), not the
 * horizontal-scroll fix itself, which is Playwright/manual-only per the plan.
 */
describe('AppShell off-canvas drawer (#48)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it('is closed by default with aria-expanded=false', () => {
    renderApp('/');

    const toggle = screen.getByRole('button', { name: 'Open navigation' });
    expect(toggle).toHaveAttribute('aria-expanded', 'false');
  });

  it('opens the drawer and flips aria-expanded / accessible name on toggle click', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Open navigation' }));

    const toggle = screen.getByRole('button', { name: 'Close navigation' });
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
  });

  it('closes again on a second toggle click', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Open navigation' }));
    await user.click(screen.getByRole('button', { name: 'Close navigation' }));

    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });

  it('moves focus to the first nav link on open, and back to the toggle on close', async () => {
    const user = userEvent.setup();
    renderApp('/');

    const toggle = screen.getByRole('button', { name: 'Open navigation' });
    await user.click(toggle);

    expect(screen.getByRole('link', { name: 'Dashboard' })).toHaveFocus();

    await user.click(screen.getByRole('button', { name: 'Close navigation' }));

    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveFocus();
  });

  it('closes on Escape', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Open navigation' }));
    expect(screen.getByRole('button', { name: 'Close navigation' })).toBeInTheDocument();

    await user.keyboard('{Escape}');

    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });

  it('does not react to Escape while already closed', async () => {
    const user = userEvent.setup();
    renderApp('/');

    // Guards against a handler that fires unconditionally and, say, throws or
    // steals focus even when there is nothing open to close.
    await user.keyboard('{Escape}');

    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });

  it('closes on a backdrop click', async () => {
    const user = userEvent.setup();
    const { container } = renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Open navigation' }));

    const backdrop = container.querySelector('[aria-hidden="true"].backdrop, [class*="backdrop"]');
    expect(backdrop).not.toBeNull();
    await user.click(backdrop as Element);

    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });

  it('renders no backdrop while closed', () => {
    const { container } = renderApp('/');

    expect(container.querySelector('[class*="backdrop"]')).toBeNull();
  });

  /*
   * arb-759 regression cover. The defect was that the <aside> itself carried no
   * open/closed state at all -- only its child nav did -- so below 768px the
   * aside stayed a fixed, opaque, full-height panel over the left edge of every
   * page. jsdom evaluates no media queries and has no layout engine, so these
   * assert the DOM contract the mobile CSS keys off (`data-open` on the ASIDE,
   * the element that is position: fixed) rather than the resulting geometry;
   * the off-screen bounding box itself is manual/Playwright-only, as the note
   * at the top of this file says.
   */
  describe('off-canvas state marker on the aside (arb-759)', () => {
    function aside(container: HTMLElement) {
      const element = container.querySelector('aside');
      expect(element).not.toBeNull();
      return element as HTMLElement;
    }

    it('marks the aside closed by default', () => {
      const { container } = renderApp('/');

      expect(aside(container)).toHaveAttribute('data-open', 'false');
    });

    it('marks the aside open while the drawer is open, and closed again after', async () => {
      const user = userEvent.setup();
      const { container } = renderApp('/');

      await user.click(screen.getByRole('button', { name: 'Open navigation' }));
      expect(aside(container)).toHaveAttribute('data-open', 'true');

      await user.click(screen.getByRole('button', { name: 'Close navigation' }));
      expect(aside(container)).toHaveAttribute('data-open', 'false');
    });

    it('returns the aside to closed on a route change', async () => {
      const user = userEvent.setup();
      const { container } = renderApp('/');

      await user.click(screen.getByRole('button', { name: 'Open navigation' }));
      expect(aside(container)).toHaveAttribute('data-open', 'true');

      await user.click(screen.getByRole('link', { name: 'Rules' }));

      expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();
      expect(aside(container)).toHaveAttribute('data-open', 'false');
    });

    it('keeps the open/closed marker on the aside and not on the nav', async () => {
      const user = userEvent.setup();
      const { container } = renderApp('/');

      await user.click(screen.getByRole('button', { name: 'Open navigation' }));

      // The nav must NOT carry a competing state class: the transform lives on
      // exactly one element, and two nested transforms is how arb-759 is
      // recreated. Asserting only the aside's attribute would still pass with
      // the old .navOpen class present alongside it.
      expect(screen.getByRole('navigation', { name: 'Main' }).className).not.toMatch(/[Oo]pen/);
      expect(aside(container)).toHaveAttribute('data-open', 'true');
    });
  });

  it('auto-closes on navigation', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Open navigation' }));
    expect(screen.getByRole('button', { name: 'Close navigation' })).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Rules' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Open navigation' })).toHaveAttribute(
      'aria-expanded',
      'false',
    );
  });
});
