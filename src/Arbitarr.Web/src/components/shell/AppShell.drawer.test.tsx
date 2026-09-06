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
