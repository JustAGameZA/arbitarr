import { screen } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

/**
 * AC-CHROME-4: the *arr shell has a persistent left sidebar next to the content.
 *
 * These are DOM-ORDER assertions, not measurements. jsdom has no layout engine:
 * offsetWidth is 0 for every element regardless of CSS, and a var()-valued
 * longhand reads back unsubstituted from getComputedStyle. A width assertion
 * here would either be vacuous or would pin the wrong thing. The pinned pixel
 * values live in theme.test.ts, which reads the tokens themselves; what this
 * file adds is that the sidebar exists and precedes the content in the document.
 */
describe('shell layout (AC-CHROME-4)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it('renders a sidebar that precedes the content pane in document order', () => {
    const { container } = renderApp('/');

    const sidebar = container.querySelector('aside');
    const content = screen.getByRole('main');

    expect(sidebar).not.toBeNull();
    expect(sidebar).toContainElement(screen.getByRole('navigation', { name: 'Main' }));
    expect(
      Boolean(
        (sidebar as HTMLElement).compareDocumentPosition(content) &
          Node.DOCUMENT_POSITION_FOLLOWING,
      ),
    ).toBe(true);
  });

  it('renders exactly one sidebar and one content pane', () => {
    const { container } = renderApp('/');

    expect(container.querySelectorAll('aside')).toHaveLength(1);
    expect(screen.getAllByRole('main')).toHaveLength(1);
  });

  it('keeps the sidebar and content as siblings under one shell root', () => {
    const { container } = renderApp('/');

    const sidebar = container.querySelector('aside') as HTMLElement;
    const content = screen.getByRole('main');

    // A two-column grid places both children on the same grid container. If the
    // content were nested inside the sidebar the CSS Grid areas would silently
    // stop applying while every "is it present" assertion kept passing.
    expect(content.parentElement).toBe(sidebar.parentElement);
  });
});
