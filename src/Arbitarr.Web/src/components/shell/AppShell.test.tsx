import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { useTableDensityStore } from '../../state/tableDensityStore';
import { mockApi, signedIn } from '../../test/mockApi';
import { AppShell } from './AppShell';
import { RouteError } from '../RouteError/RouteError';
import { RouteErrorOutlet } from '../RouteError/RouteErrorOutlet';

describe('AppShell', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    useTableDensityStore.setState({ density: 'expanded' });
    // arb-7m7: the shell only mounts once RequireSession has a definite answer.
    mockApi({ ...signedIn() });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('exposes a navigation landmark', async () => {
    renderApp('/');

    expect(await screen.findByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('keeps the same sidebar element mounted across a route change', async () => {
    const user = userEvent.setup();
    renderApp('/');

    const before = await screen.findByRole('navigation', { name: 'Main' });
    await user.click(screen.getByRole('link', { name: 'Rules' }));

    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();

    // Identity, not mere presence: `toBe` fails if the shell remounts, which is
    // what would reset sidebar scroll position and flash the nav between routes.
    expect(screen.getByRole('navigation', { name: 'Main' })).toBe(before);
  });

  it('renders route content inside the main landmark', async () => {
    renderApp('/search');

    const heading = await screen.findByRole('heading', { level: 1, name: 'Search' });
    expect(screen.getByRole('main')).toContainElement(heading);
  });

  // arb-br4. The attribute is asserted rather than the resulting padding: jsdom
  // has no layout engine and does not apply CSS modules, so a computed padding
  // reads back as the empty string whatever the rule says. The contract this
  // component actually owns is "the wrapper carries the store's density", and
  // the single rule in surface.module.css is what turns that into row height.
  it('marks the content wrapper with the default density', async () => {
    renderApp('/search');

    await screen.findByRole('heading', { level: 1, name: 'Search' });
    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toHaveAttribute('data-density', 'expanded');
  });

  it('reflects a compact density from the store onto the content wrapper', async () => {
    useTableDensityStore.setState({ density: 'compact' });
    renderApp('/search');

    await screen.findByRole('heading', { level: 1, name: 'Search' });
    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toHaveAttribute('data-density', 'compact');
  });

  // The wrapper must be an ANCESTOR of the routed content, not a sibling: the
  // compact rule is a descendant selector, so an attribute written on an
  // element beside the page would satisfy the two assertions above and still
  // style nothing.
  it('puts the density wrapper above the routed content', async () => {
    renderApp('/search');

    const heading = await screen.findByRole('heading', { level: 1, name: 'Search' });
    const wrapper = screen.getByRole('main').querySelector('[data-density]');
    expect(wrapper).toContainElement(heading);
  });
});

/**
 * document.title while RouteError's panel is up, mounted through the REAL
 * AppShell and RouteErrorOutlet -- not RouteError.test.tsx's isolated render,
 * which has no AppShell competing for `document.title` and so cannot prove
 * anything about the shipped configuration (arb-penl review fixup). AppShell
 * is the sole writer of `document.title`; RouteError only ever signals it via
 * RouteErrorContext (see AppShell.tsx and RouteErrorContext.tsx).
 *
 * This uses a small test-only route table rather than the production one
 * (routes.tsx), since the real route table has no route that throws -- but it
 * mounts the real AppShell and the real RouteErrorOutlet/RouteError, so
 * AppShell's title effect is genuinely in the tree and genuinely races
 * RouteError's commit-phase signal, exactly as it does in production.
 */
describe('AppShell + RouteError document title (arb-penl)', () => {
  const THROW_TITLE_ROUTE = '/probe-throws';
  // The real sidebar nav's "Rules" link, reused so the navigate-away test can
  // drive navigation through the always-mounted chrome (see below) rather
  // than jsdom's own router-unaware history.
  const OTHER_ROUTE = '/rules';

  function OtherPage() {
    return (
      <p>
        other page content
        <Link to={THROW_TITLE_ROUTE}>Go to the throwing route</Link>
      </p>
    );
  }

  function ThrowingPage(): never {
    throw new Error('appshell-title-test-marker');
  }

  function renderShellAtRoute(initialEntry: string) {
    const queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    mockApi({ ...signedIn() });

    return render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={[initialEntry]}>
          <RouteError>
            <Routes>
              <Route element={<AppShell />}>
                <Route element={<RouteErrorOutlet />}>
                  <Route path={OTHER_ROUTE} element={<OtherPage />} />
                  <Route path={THROW_TITLE_ROUTE} element={<ThrowingPage />} />
                </Route>
              </Route>
            </Routes>
          </RouteError>
        </MemoryRouter>
      </QueryClientProvider>,
    );
  }

  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('sets a route-specific title at a non-throwing route (positive control)', async () => {
    renderShellAtRoute(OTHER_ROUTE);

    await screen.findByText('other page content');
    // OTHER_ROUTE is '/rules', a real row in routes.titles.ts's ROUTES table,
    // so this is a real, specific title -- and specifically NOT the error
    // title, which is the contrast the next test needs to not pass vacuously.
    expect(document.title).toBe('Rules — Arbitarr');
  });

  it('shows the error title at a throwing route, with the real AppShell mounted', async () => {
    renderShellAtRoute(THROW_TITLE_ROUTE);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(document.title).toBe('Something went wrong — Arbitarr');
  });

  it('shows the error title for a throw on first load (mount-time, not only via later navigation)', async () => {
    // Distinct from the test above only in intent: this route is the FIRST
    // and ONLY entry, so the throw happens on the very first render, not
    // reached by an in-app navigation from an already-mounted, non-throwing
    // route. componentDidCatch and the context signal it sends behave
    // identically either way, but that equivalence is exactly the thing a
    // regression here could quietly break.
    renderShellAtRoute(THROW_TITLE_ROUTE);

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(document.title).toBe('Something went wrong — Arbitarr');
  });

  it('restores the destination route\'s own title after navigating away from the error', async () => {
    const user = userEvent.setup();
    // Starts on the non-throwing route and navigates IN to the throwing one,
    // then back out, via in-app links -- RouteError's own "Back to the
    // dashboard" link is a plain <a href> (by design: it must also work from
    // the login/setup-outside-shell backstop placement), so following it here
    // would trigger jsdom's full-navigation fallback instead of an in-app
    // route change, and never re-run AppShell's effect within this render.
    renderShellAtRoute(OTHER_ROUTE);
    await screen.findByText('other page content');
    expect(document.title).toBe('Rules — Arbitarr');

    await user.click(screen.getByRole('link', { name: 'Go to the throwing route' }));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(document.title).toBe('Something went wrong — Arbitarr');

    // RouteErrorOutlet keys RouteError on pathname, so navigating away from
    // the errored route unmounts it (running componentWillUnmount, which
    // signals routeErrored back to false) in the same commit that changes
    // the pathname AppShell's effect is keyed on. The error panel itself has
    // no in-app link back to OTHER_ROUTE, but the real sidebar nav (which
    // this test's AppShell renders unconditionally) survives the caught
    // error -- see RouteErrorOutlet.test.tsx's own sibling-navigation test --
    // so this drives the navigation through it, the same as an operator
    // would, rather than reaching for jsdom's own (router-unaware) history.
    await user.click(screen.getByRole('link', { name: 'Rules' }));

    expect(await screen.findByText('other page content')).toBeInTheDocument();
    expect(document.title).toBe('Rules — Arbitarr');

    // No frame left the destination showing the error title, and no
    // "setState on an unmounted component" warning was logged -- the
    // console.error spy installed in beforeEach would have recorded it.
    const errorCalls = (console.error as unknown as { mock: { calls: unknown[][] } }).mock.calls;
    const setStateWarning = errorCalls.find((args) =>
      args.some((arg) => typeof arg === 'string' && arg.includes('unmounted component')),
    );
    expect(setStateWarning).toBeUndefined();
  });
});
