import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { renderSurface } from '../../test/renderSurface';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { mockApi, signedIn, signedOut } from '../../test/mockApi';
import { TopBar } from './TopBar';

/**
 * #44 REPLACED THE ADMIN-KEY BOX IN THIS BAR with a session-aware control.
 *
 * Five tests that used to live here drove that box -- typing a key, masking the
 * field, clearing it, rejecting whitespace. They are gone rather than adapted
 * because the affordance they described is gone: a human no longer holds a key,
 * so there is no field to type one into. The admin KEY itself is NOT gone --
 * `adminKeyStore` and `apiFetch`'s header attachment remain for machine callers
 * and the #43 bootstrap path -- it simply has no UI in the chrome any more.
 * One test below asserts that absence, so re-adding the box fails rather than
 * silently reintroducing a credential prompt for humans.
 *
 * The #43 `serverKeyUnset` tests in the second block are KEPT UNCHANGED: that
 * state is about the SERVER having no key configured, which is orthogonal to
 * whether a human is signed in, and its "do not strand the operator" property
 * still holds exactly as written.
 */
describe('TopBar session control (#44)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders the signed-in identity', async () => {
    mockApi({ ...signedIn('operator') });
    renderApp('/');

    expect(await screen.findByText('Signed in as operator')).toBeInTheDocument();
  });

  it('offers a sign-out control when signed in', async () => {
    mockApi({ ...signedIn('operator') });
    renderApp('/');

    expect(await screen.findByRole('button', { name: 'Sign out' })).toBeInTheDocument();
  });

  it('signs out through the server, not merely by dropping a cookie', async () => {
    const user = userEvent.setup();
    const api = mockApi({ ...signedIn('operator'), '/api/auth/logout': { status: 204 } });
    renderApp('/');

    await user.click(await screen.findByRole('button', { name: 'Sign out' }));

    // The POST is the assertion. Clearing a cookie client-side would leave a
    // copied token working, which is the "logout" the issue lists as a defect --
    // only the server revoking the session row actually ends the session.
    await waitFor(() => {
      expect(api.callsTo('/api/auth/logout').some((call) => call.method === 'POST')).toBe(true);
    });
  });

  it('offers no key field, because a human no longer holds a key', async () => {
    mockApi({ ...signedIn('operator') });
    renderApp('/');

    await screen.findByText('Signed in as operator');

    // The affordance #44 removed, asserted absent rather than merely deleted
    // along with its tests -- so re-adding a key box to the chrome fails here.
    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Set admin key' })).toBeNull();
  });

  it('renders no sign-in affordance of its own when not signed in', async () => {
    // RequireSession owns the redirect to /login, and (arb-7m7) no longer
    // mounts the shell at all for a definitively signed-out visitor -- so this
    // branch of TopBar is reached only in the pre-redirect window, not through
    // the guarded app. Mounted directly (renderSurface, no RequireSession) to
    // exercise it anyway: a second route to /login rendered from INSIDE the
    // guarded tree is how a redirect loop gets built by accident, so this
    // branch must keep offering nothing to click regardless of how it is hit.
    mockApi({ ...signedOut() });
    renderSurface(<TopBar />);

    expect(await screen.findByText('Not signed in')).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /sign in/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /sign in/i })).toBeNull();
  });
});

describe('TopBar server-key-unset state (AC6-503, #43)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    // Signed in, so these assertions are about the serverKeyUnset branch alone
    // and not about the guard redirecting an anonymous visitor away.
    mockApi({ ...signedIn('operator') });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('AC6-503: shows the server-side message and no key prompt when serverKeyUnset', async () => {
    // No key the operator types can satisfy a gate the server has none set for.
    // Offering the field anyway would send them round a loop that cannot close.
    useAdminKeyStore.setState({ key: 'k-1', serverKeyUnset: true });
    renderApp('/');

    expect(
      await screen.findByText(/No admin API key is configured on the server\./),
    ).toBeInTheDocument();
    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Set admin key' })).toBeNull();
  });

  it('#43: points the operator at Settings instead of dead-ending when serverKeyUnset', async () => {
    // Under the local-network bootstrap bypass the operator is no longer
    // stranded: they can reach Settings and set a key. The state must therefore
    // offer a way forward, not merely report the problem.
    useAdminKeyStore.setState({ key: null, serverKeyUnset: true });
    renderApp('/');

    const link = await screen.findByRole('link', { name: /Set one in Settings/i });
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute('href', '/settings');
  });

  it('#43: still offers no key field when serverKeyUnset, link or not', async () => {
    // The link is additive. A prompt would still be a loop that cannot close,
    // so the original reasoning for omitting the field survives this change.
    useAdminKeyStore.setState({ key: null, serverKeyUnset: true });
    renderApp('/');

    await screen.findByText(/No admin API key is configured on the server\./);
    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Set admin key' })).toBeNull();
  });
});
