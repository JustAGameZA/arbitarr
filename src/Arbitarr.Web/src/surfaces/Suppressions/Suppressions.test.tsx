import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SuppressionsPage from './Suppressions';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const entries = [
  {
    occurredAt: '2026-09-01T12:30:00+00:00',
    releaseIdentifier: 'upstream-guid-1',
    queryKey: 'tvdbid=1234&season=2',
    layer: 'block-cam',
    reason: 'Pattern CAM|TS matched the release title.',
    shadowMode: false,
  },
  {
    occurredAt: '2026-09-01T12:29:00+00:00',
    releaseIdentifier: 'upstream-guid-2',
    queryKey: 'tvdbid=1234&season=2',
    layer: 'ai',
    reason: 'Verdict Reject: title does not match the requested season.',
    shadowMode: true,
  },
];

describe('Suppressions', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and the decisions table', async () => {
    mockApi({ '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Suppressions' })).toBeInTheDocument();
    expect(await screen.findByText('upstream-guid-1')).toBeInTheDocument();
    expect(screen.getByText('Pattern CAM|TS matched the release title.')).toBeInTheDocument();
  });

  it('reads as a healthy quiet state, not a missing thing, when nothing has been suppressed', async () => {
    mockApi({ '/api/admin/suppressions': { body: [] } });
    renderSurface(<SuppressionsPage />);

    expect(
      await screen.findByText(
        'Nothing suppressed or de-ranked yet. Entries appear here once a rule or the AI layer acts on a release.',
      ),
    ).toBeInTheDocument();
  });

  it('attributes each row to the acting layer and distinguishes shadow mode', async () => {
    mockApi({ '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);

    // AC11: the layer that acted -- a rule name for the rule-engine layers, a
    // stable label for the others.
    expect(await screen.findByText('block-cam')).toBeInTheDocument();
    expect(screen.getByText('ai')).toBeInTheDocument();

    // A shadow-mode row was recorded but NOT enforced, and the two must not
    // read alike: the legacy page printed "yes"/"no" under a "Shadow Mode"
    // heading, where "yes" looked like the suppression had happened.
    expect(screen.getByText('Suppressed')).toBeInTheDocument();
    expect(screen.getByText('Shadow only')).toBeInTheDocument();
  });

  it('shows both title forms for a row that resolves (AC11)', async () => {
    const user = userEvent.setup();
    mockApi({
      '/api/admin/suppressions': { body: entries },
      '/api/admin/search/upstream-guid-1/explanation': {
        body: { title: 'Some Show S02E01 1080p', originalTitle: 'Some.Show.S02E01.1080p.WEB' },
      },
    });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    await user.click(screen.getAllByRole('button', { name: 'Show titles' })[0]);

    // Original vs rewritten, side by side -- the pair AC11 asks for.
    expect(await screen.findByText('Some Show S02E01 1080p')).toBeInTheDocument();
    expect(screen.getByText('Some.Show.S02E01.1080p.WEB')).toBeInTheDocument();
  });

  it('filters by query key server-side and attaches the admin key to its GET', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ '/api/admin/suppressions': { body: entries } });
    renderSurface(<SuppressionsPage />);
    await screen.findByText('upstream-guid-1');

    // The unfiltered load carries no queryKey at all, rather than an empty one.
    expect(api.calls[0].url.searchParams.has('queryKey')).toBe(false);

    await user.type(screen.getByLabelText('Query key'), 'tvdbid=1234&season=2');
    await user.click(screen.getByRole('button', { name: 'Apply' }));

    // The filter is a server-side WHERE on QueryKey, not a client-side
    // narrowing of a full fetch, so it must reach the wire.
    const filtered = api.calls[api.calls.length - 1];
    expect(filtered.url.searchParams.get('queryKey')).toBe('tvdbid=1234&season=2');

    // Admin-gated, and its only verb is GET -- gating by verb rather than by
    // the /api/admin/ path prefix would leave this surface unauthenticated.
    expect(api.adminKeyOn('/api/admin/suppressions')).toBe('operator-key');
  });

  it('keeps the affordance and the stored key on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({
      '/api/admin/suppressions': { status: 503, body: { error: 'admin key not configured' } },
    });
    renderSurface(<SuppressionsPage />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the filter still works and no key prompt appears -- the
    // operator's key is not what is wrong.
    expect(screen.getByRole('button', { name: 'Apply' })).toBeInTheDocument();
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });
});
