import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { RouteError } from './RouteError';
import { APP_NAME } from '../../routes.titles';

const MARKER = 'route-error-test-marker-do-not-render';
const ERROR_TITLE = `Something went wrong — ${APP_NAME}`;

function NonThrowingChild() {
  return <p>child content</p>;
}

function ThrowingChild(): never {
  // Asserted directly on the thrown Error object (see the test below), not
  // just relied on to "not appear" -- a marker that was never rendered would
  // trivially pass an absence check without proving the boundary swallowed it.
  throw new Error(MARKER);
}

describe('RouteError', () => {
  beforeEach(() => {
    // React logs a caught render error to console.error by default (which this
    // boundary deliberately leaves alone -- see RouteError.tsx). Silenced only
    // in the throwing test, not globally, so an unrelated console.error would
    // still surface.
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders a non-throwing child unchanged (positive control)', () => {
    render(
      <RouteError>
        <NonThrowingChild />
      </RouteError>,
    );

    expect(screen.getByText('child content')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('catches a throwing child, shows the fixed panel, and never renders the error text', () => {
    // Prove the marker really would be found if it leaked: render it directly
    // first, in the same query the assertion below relies on.
    expect(() => render(<ThrowingChild />)).toThrow(MARKER);

    render(
      <RouteError>
        <ThrowingChild />
      </RouteError>,
    );

    const alert = screen.getByRole('alert');
    expect(alert).toBeInTheDocument();
    expect(screen.getAllByRole('heading', { level: 1 })).toHaveLength(1);
    expect(screen.getByText('Something went wrong')).toBeInTheDocument();

    // The marker is absent from the whole document, not just from the alert
    // panel's visible copy.
    expect(document.body.textContent).not.toContain(MARKER);
  });

  it('renders a reload control', () => {
    render(
      <RouteError>
        <ThrowingChild />
      </RouteError>,
    );

    expect(screen.getByRole('button', { name: 'Reload' })).toBeInTheDocument();
  });

  it('renders a link back to the dashboard', () => {
    render(
      <RouteError>
        <ThrowingChild />
      </RouteError>,
    );

    expect(screen.getByRole('link', { name: 'Back to the dashboard' })).toHaveAttribute(
      'href',
      '/',
    );
  });

  describe('document title', () => {
    it('changes the tab title while the panel is shown', () => {
      // Positive control: prove the surface's own title is really there first,
      // so the assertion after mount is a change and not a value that was
      // already ERROR_TITLE by coincidence.
      document.title = 'Rules — Arbitarr';
      expect(document.title).toBe('Rules — Arbitarr');

      render(
        <RouteError>
          <ThrowingChild />
        </RouteError>,
      );

      expect(document.title).toBe(ERROR_TITLE);
    });

    it('restores the previous title after the boundary unmounts (navigation away)', () => {
      document.title = 'Rules — Arbitarr';

      const { unmount } = render(
        <RouteError>
          <ThrowingChild />
        </RouteError>,
      );
      expect(document.title).toBe(ERROR_TITLE);

      unmount();

      expect(document.title).toBe('Rules — Arbitarr');
    });

    it('never touches the title when nothing was caught', () => {
      document.title = 'Rules — Arbitarr';

      const { unmount } = render(
        <RouteError>
          <NonThrowingChild />
        </RouteError>,
      );
      expect(document.title).toBe('Rules — Arbitarr');

      unmount();

      expect(document.title).toBe('Rules — Arbitarr');
    });
  });
});
