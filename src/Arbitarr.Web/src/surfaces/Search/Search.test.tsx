import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import SearchPage, { formatCacheAge } from './Search';
import { buildSearchQuery, EMPTY_CRITERIA } from './queries';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const response = {
  releases: [
    {
      title: 'Some.Series.S01E02.1080p',
      guid: 'upstream-guid-1',
      size: 1610612736,
      category: [5000, 5040],
      sourceName: 'nzbhydra',
      pubDate: '2026-09-05T22:10:00+00:00',
      aiVerdict: null,
    },
  ],
  provenance: {
    cacheAge: '00:01:30.5000000',
    cacheBand: 0,
    rateLimitedSources: [],
  },
};

async function runSearch(user: ReturnType<typeof userEvent.setup>, query = 'some series') {
  await user.type(screen.getByLabelText('Query'), query);
  await user.click(screen.getByRole('button', { name: 'Search' }));
}

describe('buildSearchQuery', () => {
  it('omits empty fields and includes runAiSync only when opted in', () => {
    // AC8: the AI opt-in is off by default and must not appear at all when it
    // was not requested -- sending runAiSync=false is still a flag the operator
    // never set, and absence is what the endpoint's `bool?` treats as default.
    const off = new URLSearchParams(buildSearchQuery({ ...EMPTY_CRITERIA, q: 'abc' }));
    expect(off.get('q')).toBe('abc');
    expect(off.has('runAiSync')).toBe(false);
    expect(off.has('tvdbid')).toBe(false);

    const on = new URLSearchParams(
      buildSearchQuery({ ...EMPTY_CRITERIA, q: 'abc', runAiSync: true }),
    );
    expect(on.get('runAiSync')).toBe('true');
  });
});

describe('formatCacheAge', () => {
  it('reads the .NET TimeSpan wire form rather than treating it as seconds', () => {
    // "00:01:30.5000000" is what JsonSerializerDefaults.Web emits for a
    // TimeSpan. parseFloat on it is 0, so a naive reading shows every cache as
    // brand new.
    expect(formatCacheAge('00:01:30.5000000')).toBe('1m 30s');
    expect(formatCacheAge('00:00:12.0000000')).toBe('12s');
    expect(formatCacheAge(null)).toBe('not cached');
  });
});

describe('Search', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and searches nothing until asked', () => {
    const api = mockApi({ '/api/admin/search': { body: response } });
    renderSurface(<SearchPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Search' })).toBeInTheDocument();
    expect(screen.getByText('Enter a query above to search.')).toBeInTheDocument();
    // Mounting the surface must not fire an upstream search on its own.
    expect(api.calls).toHaveLength(0);
  });

  it('renders results and provenance from the fetched data', async () => {
    const user = userEvent.setup();
    mockApi({ '/api/admin/search': { body: response } });
    renderSurface(<SearchPage />);

    await runSearch(user);

    expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
    expect(screen.getByText('1.5 GB')).toBeInTheDocument();
    expect(screen.getByText('5000, 5040')).toBeInTheDocument();
    // cacheBand 0 is a NUMBER on the wire and must be labelled, not printed.
    // The legacy admin-search.js read provenance.fromCache, which the DTO has
    // never carried, so its cache strip was always blank.
    expect(screen.getByText('Fresh')).toBeInTheDocument();
    expect(screen.getByText('1m 30s')).toBeInTheDocument();
  });

  it('toggling the AI opt-in changes the outgoing request', async () => {
    const user = userEvent.setup();
    const api = mockApi({ '/api/admin/search': { body: response } });
    renderSurface(<SearchPage />);

    await runSearch(user);
    await screen.findByText('Some.Series.S01E02.1080p');
    expect(api.callsTo('/api/admin/search')[0].url.searchParams.has('runAiSync')).toBe(false);

    await user.click(screen.getByRole('checkbox', { name: /Run AI arbitration/ }));
    await user.click(screen.getByRole('button', { name: 'Search' }));

    const second = api.callsTo('/api/admin/search')[1];
    expect(second.url.searchParams.get('runAiSync')).toBe('true');
    expect(second.url.searchParams.get('q')).toBe('some series');
  });

  it('attaches the admin key to its GET, and shows the server reason on failure', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      '/api/admin/search': { status: 400, body: { error: 'q, tvdbid or tmdbid is required.' } },
    });
    renderSurface(<SearchPage />);

    await runSearch(user);

    // The server's own words, not an invented message.
    expect(await screen.findByText('q, tvdbid or tmdbid is required.')).toBeInTheDocument();
    // A GET, and the key still goes on it: gating attachment by verb would
    // 401 this whole surface in production.
    expect(api.callsTo('/api/admin/search')[0].method).toBe('GET');
    expect(api.adminKeyOn('/api/admin/search')).toBe('operator-key');
  });

  it('keeps the search affordance and the stored key on a 503 fresh install', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({ '/api/admin/search': { status: 503, body: { error: 'admin key not configured' } } });
    renderSurface(<SearchPage />);

    await runSearch(user);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the form stays usable and no key prompt appears, because the
    // operator's key is not the problem -- the server has none configured.
    expect(screen.getByRole('button', { name: 'Search' })).toBeEnabled();
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });

  it('explains a release, and reports the missing lookup entry without crashing', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      '/api/admin/search': { body: response },
      // The known backend gap: /api/admin/search returns the raw upstream guid,
      // while IReleaseLookup is keyed on ProxyGuid and only the Torznab path
      // ever records into it, so this 404s for an ad-hoc result today.
      '/api/admin/search/upstream-guid-1/explanation': { status: 404 },
    });
    renderSurface(<SearchPage />);

    await runSearch(user);
    await user.click(await screen.findByRole('button', { name: 'Explain' }));

    expect(await screen.findByText(/No stored explanation for this release/)).toBeInTheDocument();
    expect(api.callsTo('/api/admin/search/upstream-guid-1/explanation')).toHaveLength(1);
    // Also admin-gated, and also a GET.
    expect(api.adminKeyOn('/api/admin/search/upstream-guid-1/explanation')).toBe('operator-key');
  });
});
