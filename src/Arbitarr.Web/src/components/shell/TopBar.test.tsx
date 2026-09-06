import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it } from 'vitest';

import { renderApp } from '../../test/renderApp';
import { useAdminKeyStore } from '../../state/adminKeyStore';

describe('TopBar admin key control (AC6, AC6-503)', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  it('offers a key field when no key is held', () => {
    renderApp('/');

    expect(screen.getByLabelText('Admin API key')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Set admin key' })).toBeInTheDocument();
  });

  it('stores a submitted key and stops showing the field', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.type(screen.getByLabelText('Admin API key'), 'k-1');
    await user.click(screen.getByRole('button', { name: 'Set admin key' }));

    expect(useAdminKeyStore.getState().key).toBe('k-1');
    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.getByText('Admin key set')).toBeInTheDocument();
  });

  it('masks the key field', () => {
    renderApp('/');

    // A visible key is shoulder-surfable and lands in screenshots pasted into
    // issues; autoComplete=off keeps the browser from offering to save it.
    const input = screen.getByLabelText('Admin API key');
    expect(input).toHaveAttribute('type', 'password');
    expect(input).toHaveAttribute('autocomplete', 'off');
  });

  it('clears the key on request', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.setState({ key: 'k-1', serverKeyUnset: false });
    renderApp('/');

    await user.click(screen.getByRole('button', { name: 'Clear admin key' }));

    expect(useAdminKeyStore.getState().key).toBeNull();
    expect(screen.getByLabelText('Admin API key')).toBeInTheDocument();
  });

  it('ignores a whitespace-only submission', async () => {
    const user = userEvent.setup();
    renderApp('/');

    await user.type(screen.getByLabelText('Admin API key'), '   ');
    await user.click(screen.getByRole('button', { name: 'Set admin key' }));

    expect(useAdminKeyStore.getState().key).toBeNull();
  });

  it('AC6-503: shows the server-side message and no key prompt when serverKeyUnset', () => {
    // No key the operator types can satisfy a gate the server has none set for.
    // Offering the field anyway would send them round a loop that cannot close.
    useAdminKeyStore.setState({ key: 'k-1', serverKeyUnset: true });
    renderApp('/');

    expect(screen.getByText(/No admin API key is configured on the server\./)).toBeInTheDocument();
    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Set admin key' })).toBeNull();
  });

  it('#43: points the operator at Settings instead of dead-ending when serverKeyUnset', () => {
    // Under the local-network bootstrap bypass the operator is no longer
    // stranded: they can reach Settings and set a key. The state must therefore
    // offer a way forward, not merely report the problem.
    useAdminKeyStore.setState({ key: null, serverKeyUnset: true });
    renderApp('/');

    const link = screen.getByRole('link', { name: /Set one in Settings/i });
    expect(link).toBeInTheDocument();
    expect(link).toHaveAttribute('href', '/settings');
  });

  it('#43: still offers no key field when serverKeyUnset, link or not', () => {
    // The link is additive. A prompt would still be a loop that cannot close,
    // so the original reasoning for omitting the field survives this change.
    useAdminKeyStore.setState({ key: null, serverKeyUnset: true });
    renderApp('/');

    expect(screen.queryByLabelText('Admin API key')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Set admin key' })).toBeNull();
  });
});
