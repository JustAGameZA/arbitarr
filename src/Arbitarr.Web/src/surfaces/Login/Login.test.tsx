import { screen, waitFor } from '@testing-library/react';
import type { QueryClient } from '@tanstack/react-query';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi, setupRequired, signedIn, signedOut } from '../../test/mockApi';

/**
 * Patience, not a behaviour change.
 *
 * The vitest config sets `css: true`, so every render parses the real stylesheet,
 * and the sign-in tests drive a full mutation round trip -- and the redirect case
 * additionally waits on a route change and the destination's own query. On a
 * loaded machine that chain sits close enough to vitest's 5s default to turn an
 * ordinary render into a spurious failure. The same idiom, and the same reason,
 * as TEST_TIMEOUT_MS in Settings/ApiKeys/ApiKeys.test.tsx. Assertions unchanged.
 */
const TEST_TIMEOUT_MS = 20_000;

describe('Login and the route guard (#44)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('redirects an unauthenticated visitor from a guarded route to /login', async () => {
    mockApi({ ...signedOut() });
    renderApp('/rules');

    expect(await screen.findByRole('heading', { level: 1, name: 'Sign in to Arbitarr' }))
      .toBeInTheDocument();
  });

  it('redirects WITHOUT looping: the login page renders and stays rendered', async () => {
    // THE LOOP TEST the plan asks for, and the reason /login sits outside
    // RequireSession in routes.tsx. A guard wrapping its own redirect target
    // would bounce /login -> /login forever; in jsdom that shows up as the
    // heading never settling, or as React's update-depth error.
    //
    // The assertion is deliberately in two parts: the page appears, AND it is
    // still there after the router has had time to run further effects. A single
    // findBy would pass on the first frame of an infinite redirect.
    mockApi({ ...signedOut() });
    renderApp('/rules');

    const heading = await screen.findByRole('heading', { level: 1, name: 'Sign in to Arbitarr' });
    expect(heading).toBeInTheDocument();

    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(screen.getByRole('heading', { level: 1, name: 'Sign in to Arbitarr' })).toBeInTheDocument();
    // And the guarded surface is genuinely not rendered underneath it.
    expect(screen.queryByRole('heading', { level: 1, name: 'Rules' })).toBeNull();
  });

  it('sends a fresh install to /setup rather than to a login it cannot satisfy', async () => {
    // An instance with no accounts has nothing to sign into, so a login form
    // would be an affordance that cannot succeed -- the same "stranded operator"
    // shape TopBar's load-bearing comment warns about.
    mockApi({ ...setupRequired() });
    renderApp('/rules');

    expect(await screen.findByRole('heading', { level: 1, name: 'Set up Arbitarr' }))
      .toBeInTheDocument();
  });

  it('does not redirect an authenticated visitor', async () => {
    mockApi({ ...signedIn('operator'), '/api/admin/rules': { body: [] } });
    renderApp('/rules');

    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { level: 1, name: 'Sign in to Arbitarr' })).toBeNull();
  });

  it('renders the guarded app rather than stranding the operator when the session call fails', async () => {
    // Fail-open, deliberately: the guard is a convenience, not the security
    // boundary (every gated route is enforced server-side). Redirecting on error
    // would strand the operator at a login page during a backend hiccup -- on a
    // page whose own submit needs the same backend.
    mockApi({ '/api/auth/session': { status: 500, body: { error: 'boom' } }, '/api/admin/rules': { body: [] } });
    renderApp('/rules');

    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();
  });

  it('posts the credentials and lands the operator in the app', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      ...signedOut(),
      '/api/auth/login': { body: { authenticated: true, username: 'operator', setupRequired: false } },
      '/api/admin/rules': { body: [] },
    });
    renderApp('/rules');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), 'example-operator-passphrase');

    // THE SESSION ROUTE MUST START ANSWERING "signed in" HERE, as the real server
    // does once it has set the cookie. `useSessionQuery` sets
    // `refetchOnWindowFocus`, and clicking refocuses the jsdom window, so the
    // guard REFETCHES moments after the login seeds the cache. Left answering
    // `signedOut()`, that refetch overwrites the seeded identity with a stale
    // "no" and the guard bounces the operator straight back to /login -- so the
    // test would be asserting the mock's staleness rather than the app's
    // behaviour. Verified by probe: without this the redirect never completes.
    api.set('/api/auth/session', {
      body: { authenticated: true, username: 'operator', setupRequired: false },
    });

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    // Returned to where they were aiming, not dumped on the dashboard.
    expect(await screen.findByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();

    const login = api.callsTo('/api/auth/login');
    expect(login).toHaveLength(1);
    expect(login[0]!.method).toBe('POST');
  }, TEST_TIMEOUT_MS);

  it("shows the server's own message on a failed sign-in", async () => {
    const user = userEvent.setup();
    mockApi({
      ...signedOut(),
      // RFC 7807, which is what Results.Problem emits -- the reason lives in
      // `detail`. The message deliberately does not say WHICH half was wrong.
      '/api/auth/login': {
        status: 401,
        body: { title: 'Sign-in failed', detail: 'The username or password is incorrect.' },
      },
    });
    renderApp('/login');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), 'wrong-passphrase');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The username or password is incorrect.');
  });

  it('does not clear the admin key when a sign-in fails', async () => {
    // A 401 from /api/auth/login is not a rejected admin key. Before #44 scoped
    // that branch to key-bearing paths, a failed password would have cleared an
    // unrelated credential and told the operator to re-enter it in the top bar.
    const user = userEvent.setup();
    useAdminKeyStore.setState({ key: 'machine-key', serverKeyUnset: false });
    mockApi({
      ...signedOut(),
      '/api/auth/login': { status: 401, body: { detail: 'The username or password is incorrect.' } },
    });
    renderApp('/login');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), 'wrong-passphrase');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await screen.findByRole('alert');
    expect(useAdminKeyStore.getState().key).toBe('machine-key');
  });

  it('points to the recovery runbook instead of reciting it (arb-hhb)', async () => {
    // The runbook itself (stop container, edit arbitarr.db, start) moved to the
    // README -- a login screen should not carry operational instructions
    // permanently. What must stay is the signpost: an operator who fails to
    // sign in still learns, right here, that a way back exists.
    mockApi({ ...signedOut() });
    renderApp('/login');

    expect(screen.queryByText(/users table/i)).toBeNull();
    expect(screen.queryByText(/There is no password reset/i)).toBeNull();

    const link = await screen.findByRole('link', { name: 'Locked out?' });
    expect(link).toHaveAttribute(
      'href',
      'https://github.com/JustAGameZA/arbitarr#signing-in-and-what-to-do-when-you-cannot',
    );
  });

  it('Setup points lockout recovery at the README instead of reciting the database steps (arb-eg09)', async () => {
    // Setup states "There is no password reset" BEFORE the operator chooses a
    // passphrase -- that warning has a job and stays. What must go is the
    // database mechanics (arbitarr.db / users table); recovery is now a link
    // to the same README anchor Login.tsx uses after #236.
    mockApi({ ...setupRequired() });
    renderApp('/rules');

    expect(await screen.findByRole('heading', { level: 1, name: 'Set up Arbitarr' }))
      .toBeInTheDocument();

    expect(screen.getByText(/There is no password reset/i)).toBeInTheDocument();
    expect(screen.queryByText(/users table/i)).toBeNull();
    expect(screen.queryByText(/arbitarr\.db/i)).toBeNull();

    const link = screen.getByRole('link', { name: 'how to recover a locked-out instance' });
    expect(link).toHaveAttribute(
      'href',
      'https://github.com/JustAGameZA/arbitarr#signing-in-and-what-to-do-when-you-cannot',
    );
  });

  it('sends an already-signed-in visitor away from /login', async () => {
    mockApi({ ...signedIn('operator') });
    renderApp('/login');

    // Lands in the app rather than staring at a form for credentials already
    // supplied. The dashboard is the index route.
    expect(await screen.findByRole('heading', { level: 1, name: 'Dashboard' })).toBeInTheDocument();
  });
});

describe('session tokens never reach browser storage (#44)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('writes nothing to localStorage or sessionStorage on a successful sign-in', async () => {
    const user = userEvent.setup();
    mockApi({
      ...signedOut(),
      '/api/auth/login': { body: { authenticated: true, username: 'operator', setupRequired: false } },
    });
    renderApp('/login');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), 'example-operator-passphrase');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.queryByRole('heading', { level: 1, name: 'Sign in to Arbitarr' })).toBeNull();
    });

    // POSITIVE CONTROL FIRST. An "is it absent?" assertion passes just as
    // happily when nothing was ever in play, so this proves the check can
    // actually fail before asserting that it does not: a planted value IS
    // found by the same search that is then run against the real storage.
    localStorage.setItem('planted', 'a-planted-session-token');
    sessionStorage.setItem('planted', 'a-planted-session-token');
    expect(storageContains('a-planted-session-token')).toBe(true);
    localStorage.clear();
    sessionStorage.clear();

    // The real assertion: sign-in put NOTHING in either store. This holds
    // structurally rather than by discipline -- the token is in an HttpOnly
    // cookie that script cannot read, so no code here has a value to persist.
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });
});

describe('the password never outlives the request that carries it (#44)', () => {
  const PASSWORD = 'example-passphrase-worth-finding';

  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * THE CACHE THIS SEARCHES IS NOT AN IMPLEMENTATION DETAIL.
   *
   * react-query keeps each mutation's `variables` on the mutation object for as
   * long as that mutation is cached -- and `variables` here IS `{ username,
   * password }`. So without an explicit reset, the plaintext password stays
   * reachable from `queryClient` for the lifetime of the page: in a heap
   * snapshot, in a devtools panel, and to any script running on the origin.
   * Clearing the React state (which `Login` does) is not enough on its own,
   * because that is a different copy.
   */
  const cacheDump = (queryClient: QueryClient) =>
    JSON.stringify(queryClient.getMutationCache().getAll());

  it('leaves no copy of the password in the mutation cache after a successful sign-in', async () => {
    const user = userEvent.setup();
    mockApi({
      ...signedOut(),
      '/api/auth/login': { body: { authenticated: true, username: 'operator', setupRequired: false } },
    });
    const { queryClient } = renderApp('/login');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), PASSWORD);

    // POSITIVE CONTROL. An absence assertion over a value that was never in the
    // cache passes vacuously, so this proves the search CAN find the password:
    // the SAME cacheDump search, over the SAME cache, against a mutation holding
    // it in exactly the shape the login mutation uses. It is a separate probe
    // mutation rather than the real one caught mid-flight, because `user.click`
    // does not return until the login has settled and the eviction has already
    // run -- there is no window to observe. Without this control, a typo in the
    // search or a mutation that never ran would look just like a clean cache.
    await queryClient
      .getMutationCache()
      .build(queryClient, { mutationFn: async (vars: unknown) => vars })
      .execute({ username: 'operator', password: PASSWORD });
    expect(cacheDump(queryClient)).toContain(PASSWORD);
    queryClient.getMutationCache().clear();
    expect(cacheDump(queryClient)).not.toContain(PASSWORD);

    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.queryByRole('heading', { level: 1, name: 'Sign in to Arbitarr' })).toBeNull();
    });

    // The real assertion: once the request has settled, the credential is gone.
    await waitFor(() => {
      expect(cacheDump(queryClient)).not.toContain(PASSWORD);
    });
  });

  it('leaves no copy of the password in the mutation cache after a FAILED sign-in', async () => {
    // The failure path is the one that breaks. A same-tick reset() swallows the
    // rejection (react-query v5 runs the hook-level onSettled BEFORE the
    // per-call onError), so the naive fix trades this leak for a login that
    // silently shows no error. Both properties are asserted here together so
    // neither can be fixed by sacrificing the other.
    const user = userEvent.setup();
    mockApi({
      ...signedOut(),
      '/api/auth/login': { status: 401, body: { detail: 'The username or password is incorrect.' } },
    });
    const { queryClient } = renderApp('/login');

    await user.type(await screen.findByLabelText('Username'), 'operator');
    await user.type(screen.getByLabelText('Password'), PASSWORD);
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    // The rejection still reaches the operator...
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'The username or password is incorrect.',
    );

    // ...and the password is still not retained.
    await waitFor(() => {
      expect(cacheDump(queryClient)).not.toContain(PASSWORD);
    });
  });
});

/** Whether `needle` appears in any value held by either web storage. */
function storageContains(needle: string): boolean {
  for (const store of [localStorage, sessionStorage]) {
    for (let index = 0; index < store.length; index += 1) {
      const key = store.key(index);
      if (key !== null && (store.getItem(key) ?? '').includes(needle)) {
        return true;
      }
    }
  }
  return false;
}
