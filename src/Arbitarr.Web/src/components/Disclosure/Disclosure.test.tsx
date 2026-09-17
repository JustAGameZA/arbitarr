import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';

import { Disclosure } from './Disclosure';
import styles from './Disclosure.module.css';

/**
 * The primitive's contract (arb-h9gd). Each assertion pins a property a caller
 * relies on and that a reimplementation would have to supply by hand.
 *
 * ON WHY NOTHING HERE ASSERTS VISIBILITY. jsdom implements none of `<details>`'s
 * hiding — no UA rule for the closed state, so collapsed content computes
 * `display: block`, `toBeVisible()` answers true for it, and `getByRole` reaches
 * controls inside a closed `<details>`. Every "the content is hidden" assertion
 * available in this environment passes whether or not the element collapses
 * anything, so writing one would record a guarantee no test here is checking.
 * The `open` attribute and its toggling ARE modelled faithfully, and they are
 * what these tests assert. The hiding itself is the platform's guarantee for
 * `<details>`, which is the substantive reason this component is a native
 * element and not a `useState` + `hidden` expander.
 */
describe('Disclosure', () => {
  it('starts collapsed and opens when the summary is activated', async () => {
    render(
      <Disclosure summary="Effective configuration">
        <p>the collapsed content</p>
      </Disclosure>,
    );

    const summary = screen.getByText('Effective configuration');
    const details = summary.closest('details');
    expect(details).not.toHaveAttribute('open');
    // The content is rendered into the element (not dropped while closed), so a
    // caller's children survive the collapsed state and appear the instant it
    // opens rather than mounting late.
    expect(details).toContainElement(screen.getByText('the collapsed content'));

    await userEvent.click(summary);

    expect(details).toHaveAttribute('open');
  });

  it('honours defaultOpen for a caller whose content is not secondary', () => {
    render(
      <Disclosure summary="Open from the start" defaultOpen>
        <p>the content</p>
      </Disclosure>,
    );

    expect(screen.getByText('the content').closest('details')).toHaveAttribute('open');
  });

  it('contributes no heading, so it cannot restore the DOM weight it removes', () => {
    render(
      <Disclosure summary="Effective configuration">
        <p>the content</p>
      </Disclosure>,
    );

    // Asserted rather than assumed: promoting the summary to an <h2> is the
    // obvious "make it look like the panel it replaced" change, and it would
    // reinstate exactly the heading weight collapsing the panel was meant to
    // remove -- while passing every other test in this file.
    expect(screen.queryAllByRole('heading')).toHaveLength(0);
  });

  it('carries no hand-rolled ARIA, leaving the semantics to the native element', () => {
    render(
      <Disclosure summary="Effective configuration">
        <p>the content</p>
      </Disclosure>,
    );

    const summary = screen.getByText('Effective configuration');
    // The native <summary> IS the disclosure button and already exposes the
    // expanded state to assistive technology. A hand-maintained
    // aria-expanded/aria-controls pair beside it is a second source of truth
    // that drifts the first time someone changes the open state without
    // updating both -- which is the retrofit #466 is currently doing for the
    // hand-rolled expanders elsewhere in this tree, and the cost this avoids.
    expect(summary).not.toHaveAttribute('aria-expanded');
    expect(summary).not.toHaveAttribute('aria-controls');
    expect(summary).not.toHaveAttribute('role');
    expect(summary.tagName).toBe('SUMMARY');
  });

  it('carries its own chrome classes, so it reads as the panel it replaced', () => {
    render(
      <Disclosure summary="Effective configuration">
        <p>the content</p>
      </Disclosure>,
    );

    // Keyed on the resolved hashed class names (css: true in vite.config.ts):
    // every other assertion in this file passes just as happily on an unstyled
    // <details>, so dropping the stylesheet would silently return this to the
    // browser default chrome beside styled panels.
    const summary = screen.getByText('Effective configuration');
    expect(summary.classList).toContain(styles.summary);
    expect(summary.closest('details')?.classList).toContain(styles.disclosure);
    expect(screen.getByText('the content').parentElement?.classList).toContain(styles.content);
  });
});
