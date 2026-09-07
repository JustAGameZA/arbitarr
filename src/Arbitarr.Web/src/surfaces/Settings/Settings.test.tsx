import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SettingsPage from './Settings';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const settings = [
  {
    key: 'Cache.FreshUntil',
    group: 'Caching',
    displayName: 'Fresh until',
    rationale: 'How long a cached snapshot is served without revalidation.',
    requiresRestart: false,
    isBoolean: false,
    value: '00:05:00',
    min: '00:01:00',
    max: '01:00:00',
    noMaximumReason: null,
    restartReason: null,
    governedTable: null,
    governedTableRows: null,
  },
  {
    key: 'Search.RecentLogSize',
    group: 'Observability',
    displayName: 'Recent search log size',
    rationale: 'How many recent searches are retained for the dashboard.',
    requiresRestart: true,
    isBoolean: false,
    value: '200',
    min: '10',
    max: null,
    noMaximumReason: 'The log is bounded by the retention window, not by a row ceiling.',
    restartReason: 'The ring buffer is sized once at startup.',
    governedTable: 'RecentSearches',
    governedTableRows: 143,
  },
];

describe('Settings', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and one panel per group', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Settings' })).toBeInTheDocument();
    expect(await screen.findByRole('heading', { name: 'Caching' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Observability' })).toBeInTheDocument();
  });

  it('renders bounds, rationale and every explanatory field the DTO carries', async () => {
    mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    expect(await screen.findByText('Fresh until')).toBeInTheDocument();
    expect(screen.getByText('00:01:00')).toBeInTheDocument();
    expect(screen.getByText('01:00:00')).toBeInTheDocument();

    // The four fields the legacy admin-settings.js never rendered. Without
    // them the operator sees a bound with no account of why it is there --
    // which is precisely what AC10 asks for.
    expect(
      screen.getByText('The log is bounded by the retention window, not by a row ceiling.'),
    ).toBeInTheDocument();
    expect(screen.getByText('The ring buffer is sized once at startup.')).toBeInTheDocument();
    expect(screen.getByText('RecentSearches')).toBeInTheDocument();
    expect(screen.getByText(/143 rows today/)).toBeInTheDocument();
    expect(screen.getByText('Restart required')).toBeInTheDocument();
  });

  it('sends an out-of-bounds value to the server and shows its rejection unchanged', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      '/api/admin/settings': { body: settings },
      '/api/admin/settings/Cache.FreshUntil': {
        status: 400,
        body: { error: "Cache.FreshUntil must be between 00:01:00 and 01:00:00; got 10:00:00." },
      },
    });
    renderSurface(<SettingsPage />);

    const field = await screen.findByLabelText('Fresh until value');
    await user.clear(field);
    await user.type(field, '10:00:00');
    await user.click(screen.getAllByRole('button', { name: 'Save' })[0]);

    // The server's exact words -- AC10 forbids paraphrasing or inventing one -- and they are
    // ANNOUNCED, not merely rendered: an operator who has just pressed Save is not necessarily
    // looking at the field that failed, so the message carries role="alert" (Settings.tsx) to
    // reach a screen reader without one.
    //
    // Found by text and then asserted to BE an alert, rather than queried by role: this surface
    // mounts the API-keys section (#88) alongside the settings panels, and that section raises its
    // own alert here because this test does not mock /api/admin/keys. A bare findByRole would
    // therefore be ambiguous, and widening the mock to silence it would couple every settings test
    // to an unrelated section's endpoints. Asserting the message IS an alert -- rather than just
    // finding the text -- is the point, so that dropping role="alert" still fails here.
    const rejection = await screen.findByText(
      'Cache.FreshUntil must be between 00:01:00 and 01:00:00; got 10:00:00.',
    );
    expect(rejection).toHaveAttribute('role', 'alert');

    // The request went out unaltered. The legacy page had an isWithinBounds()
    // pre-check that blocked this call entirely and displayed a message of its
    // own, so the server's real answer was never seen and a value it would
    // have accepted could not be set when the two disagreed.
    const put = api.calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe('/api/admin/settings/Cache.FreshUntil');
    expect(JSON.parse(put!.body!)).toEqual({ value: '10:00:00' });

    // And the rejected entry stays put, so it can be corrected rather than retyped.
    expect(field).toHaveValue('10:00:00');
  });

  it('attaches the admin key to its GET and its PUT', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ '/api/admin/settings': { body: settings } });
    renderSurface(<SettingsPage />);

    await user.click((await screen.findAllByRole('button', { name: 'Save' }))[0]);

    expect(api.calls.find((call) => call.method === 'GET')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
    expect(api.calls.find((call) => call.method === 'PUT')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
  });

  it('keeps the affordance and the stored key on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({ '/api/admin/settings': { status: 503, body: { error: 'admin key not configured' } } });
    renderSurface(<SettingsPage />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the page explains the server-side gap and does not ask for a key
    // the operator has already supplied.
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });
});
