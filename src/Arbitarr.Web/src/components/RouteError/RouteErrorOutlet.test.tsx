import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, Link, Outlet } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { RouteErrorOutlet } from './RouteErrorOutlet';

const MARKER = 'route-error-outlet-test-marker-do-not-render';

/**
 * Stands in for AppShell: a persistent nav landmark plus an <Outlet /> for
 * the routed page, without pulling in the real AppShell (out of scope for
 * this bead -- see RouteError.tsx's placement note).
 */
function StubShell() {
  return (
    <div>
      <nav aria-label="Main">
        <Link to="/one">One</Link>
        <Link to="/two">Two</Link>
      </nav>
      <Outlet />
    </div>
  );
}

function NonThrowingPage() {
  return <p>page one content</p>;
}

function ThrowingPage(): never {
  // Same non-vacuity approach as RouteError.test.tsx: the marker is asserted
  // directly on the thrown Error, not only relied on to "not appear".
  throw new Error(MARKER);
}

function SiblingPage() {
  return <p>page two content</p>;
}

function renderAtRoute(initialEntry: string) {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route element={<StubShell />}>
          <Route element={<RouteErrorOutlet />}>
            <Route path="/one" element={<NonThrowingPage />} />
            <Route path="/throws" element={<ThrowingPage />} />
            <Route path="/two" element={<SiblingPage />} />
          </Route>
        </Route>
      </Routes>
    </MemoryRouter>,
  );
}

describe('RouteErrorOutlet', () => {
  beforeEach(() => {
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('renders a non-throwing page alongside the shell nav (positive control)', () => {
    renderAtRoute('/one');

    expect(screen.getByText('page one content')).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('catches a throwing page below the shell: panel shown, nav still present, marker absent', () => {
    expect(() => render(<ThrowingPage />)).toThrow(MARKER);

    renderAtRoute('/throws');

    expect(screen.getByRole('alert')).toBeInTheDocument();
    // The nav survives the caught error -- this is the whole point of nesting
    // the boundary below the shell instead of wrapping the whole route tree.
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
    expect(document.body.textContent).not.toContain(MARKER);
  });

  it('resets on navigation to a sibling route: panel gone, sibling renders', async () => {
    const user = userEvent.setup();
    renderAtRoute('/throws');

    expect(screen.getByRole('alert')).toBeInTheDocument();

    await user.click(screen.getByRole('link', { name: 'Two' }));

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByText('page two content')).toBeInTheDocument();
  });
});
