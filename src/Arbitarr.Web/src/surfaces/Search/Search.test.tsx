import { screen, waitFor } from '@testing-library/react';
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
    timedOutSources: [],
    failedSources: [],
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
    const api = mockApi({
      '/api/admin/search': { body: response },
      // Mounting also fires the no-sources hint's own config read (F-020b);
      // mocked here so this test's assertion stays about the search call.
      '/api/config/effective': { body: { nzbHydraConfigured: true } },
    });
    renderSurface(<SearchPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Search' })).toBeInTheDocument();
    expect(screen.getByText('Enter a query above to search.')).toBeInTheDocument();
    // Mounting the surface must not fire an upstream search on its own.
    expect(api.callsTo('/api/admin/search')).toHaveLength(0);
  });

  describe('the no-sources hint (F-020b)', () => {
    it('shows a hint linking to Settings > Sources when no source is configured', async () => {
      mockApi({
        '/api/admin/search': { body: response },
        '/api/config/effective': { body: { nzbHydraConfigured: false } },
      });
      renderSurface(<SearchPage />);

      expect(
        // Names sources generally, matching the Dashboard's hint word for word
        // (arb-72mf): the flag behind both is now true for an enabled source of
        // any kind, so neither surface may name one product.
        await screen.findByText(/No sources configured\. Add a source URL and API key/),
      ).toBeInTheDocument();
      expect(screen.getByRole('link', { name: 'Settings > Sources' })).toHaveAttribute(
        'href',
        '/settings',
      );
    });

    // Positive control: the same assertion that must find nothing once a
    // source IS configured, proving the hint is genuinely conditional and not
    // just always-absent copy that happened to satisfy the test above.
    it('does not show the hint when a source is configured', async () => {
      const api = mockApi({
        '/api/admin/search': { body: response },
        '/api/config/effective': { body: { nzbHydraConfigured: true } },
      });
      renderSurface(<SearchPage />);

      // Wait for the effective-config fetch itself to be made -- proof the
      // query actually settled, not just that the initial render passed --
      // before asserting the hint stays absent.
      await waitFor(() => expect(api.callsTo('/api/config/effective')).toHaveLength(1));
      expect(screen.queryByText(/No sources configured/)).toBeNull();
    });
  });

  it('renders results and provenance from the fetched data', async () => {
    const user = userEvent.setup();
    mockApi({ '/api/admin/search': { body: response } });
    renderSurface(<SearchPage />);

    await runSearch(user);

    expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
    expect(screen.getByText('1.5 GiB')).toBeInTheDocument();
    expect(screen.getByText('5000, 5040')).toBeInTheDocument();
    // cacheBand 0 is a NUMBER on the wire and must be labelled, not printed.
    // The legacy admin-search.js read provenance.fromCache, which the DTO has
    // never carried, so its cache strip was always blank.
    expect(screen.getByText('Fresh')).toBeInTheDocument();
    expect(screen.getByText('1m 30s')).toBeInTheDocument();
  });

  describe('size rendering (arb-i3v7: binary units via the shared formatBytes)', () => {
    // TorznabFeedParser leaves `size` at its 0 default when neither the
    // torznab `size` attribute nor the `<size>` element parses, so a 0 on this
    // wire means "unknown", not "a zero-byte release" -- unlike Library's
    // sizes, which are real measurements and render "0 B" as a real 0. A
    // negative size is included alongside 0 since AdHocSearchEndpoint's
    // non-nullable `size: number` cannot itself distinguish "unknown" from
    // "impossible"; both must render as absent here.
    it.each([0, -1])(
      'renders a non-positive size (%d) as the absence dash, not "0 B"',
      async (size) => {
        const user = userEvent.setup();
        mockApi({
          '/api/admin/search': {
            body: {
              ...response,
              releases: [
                { ...response.releases[0], size, guid: 'g-unknown-size' },
                // Positive control in the same render: a real positive size
                // still gets its unit label, so the em dash above cannot pass
                // merely because the table rendered nothing.
                {
                  ...response.releases[0],
                  size: 2048,
                  guid: 'g-known-size',
                  title: 'Known.Size.Release',
                },
              ],
            },
          },
        });
        renderSurface(<SearchPage />);

        await runSearch(user);

        expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
        expect(screen.getByText('—')).toBeInTheDocument();
        expect(screen.getByText('2.0 KiB')).toBeInTheDocument();
      },
    );

    it('renders the em dash only for a genuinely absent size', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            ...response,
            // The DTO types `size` as non-nullable; this simulates a server
            // response drifting from that contract, which formatBytes must
            // still resolve to the absence dash rather than throwing or
            // printing a fabricated figure.
            releases: [{ ...response.releases[0], size: null as unknown as number }],
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
      expect(screen.getByText('—')).toBeInTheDocument();
    });

    it('crosses the KiB boundary at 1024 bytes, labelled binary not decimal', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            ...response,
            releases: [{ ...response.releases[0], size: 1024 }],
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
      expect(screen.getByText('1.0 KiB')).toBeInTheDocument();
    });
  });

  it('tells the operator a query ran and matched nothing', async () => {
    const user = userEvent.setup();
    mockApi({
      '/api/admin/search': {
        body: { releases: [], provenance: response.provenance },
      },
    });
    renderSurface(<SearchPage />);

    await runSearch(user);

    // Distinct from the pre-search prompt ("Enter a query above to search."):
    // a query has already run here, so the copy must say the result, not
    // repeat the instruction to search.
    expect(
      await screen.findByText('No releases matched. Try a broader query or different search terms.'),
    ).toBeInTheDocument();
  });

  describe('the no-source-answered empty state (arb-cy1y)', () => {
    // The control for the whole group is the test directly above: the SAME zero
    // releases with both failure lists empty must keep the broaden-the-query
    // copy, so an empty state that always claimed an outage would fail it.

    it('names every timed-out source and does not tell the operator to broaden the query', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [],
            provenance: {
              ...response.provenance,
              // Two sources, asserted one by one: a join that rendered only the
              // first (or only the last) still satisfies "some source is named".
              timedOutSources: ['nzbhydra-a', 'nzbhydra-b'],
            },
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(
        await screen.findByText(/No source answered this search/),
      ).toBeInTheDocument();
      const named = screen.getByText(/Did not answer in time:/);
      expect(named).toHaveTextContent('nzbhydra-a');
      expect(named).toHaveTextContent('nzbhydra-b');
      // The copy must never assert the source is down: a whole-fan-out ceiling
      // hit names a healthy-but-slow source here too, so "down" is a claim this
      // list cannot support. Asserted by reading the phrasing the names actually
      // carry rather than by querying for the forbidden word: nothing renders
      // "is down" today, so queryByText(/is down/) would pass against an empty
      // document and keep passing after the copy was reworded to use it.
      expect(named.textContent).toMatch(/did not answer in time/i);
      expect(named.textContent).not.toMatch(/down|offline|unavailable/i);
      expect(
        screen.queryByText('No releases matched. Try a broader query or different search terms.'),
      ).toBeNull();
    });

    it('names every failed source and does not tell the operator to broaden the query', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [],
            provenance: {
              ...response.provenance,
              failedSources: ['nzbhydra-c', 'nzbhydra-d'],
            },
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(
        await screen.findByText(/No source answered this search/),
      ).toBeInTheDocument();
      const named = screen.getByText(/^Failed:/);
      expect(named).toHaveTextContent('nzbhydra-c');
      expect(named).toHaveTextContent('nzbhydra-d');
      expect(
        screen.queryByText('No releases matched. Try a broader query or different search terms.'),
      ).toBeNull();
    });

    // Both lists at once, which is the shape that catches a component merging
    // them into one sentence: a timeout and a failure mean different things to
    // an operator, so each source must appear under its OWN heading and never
    // under the other's.
    it('reports a timed-out source and a failed source under their own headings', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [],
            provenance: {
              ...response.provenance,
              timedOutSources: ['nzbhydra-slow'],
              failedSources: ['nzbhydra-broken'],
            },
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      const timedOut = await screen.findByText(/Did not answer in time:/);
      expect(timedOut).toHaveTextContent('nzbhydra-slow');
      expect(timedOut).not.toHaveTextContent('nzbhydra-broken');

      const failed = screen.getByText(/^Failed:/);
      expect(failed).toHaveTextContent('nzbhydra-broken');
      expect(failed).not.toHaveTextContent('nzbhydra-slow');
    });

    // Positive control for the branch itself: a PARTIAL degradation is not an
    // outage. The releases that did arrive must still render, with the timed-out
    // source reported on the provenance strip rather than replacing them.
    it('keeps rendering releases when some sources timed out but others answered', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            ...response,
            provenance: { ...response.provenance, timedOutSources: ['nzbhydra-slow'] },
          },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(await screen.findByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
      // The provenance strip carries the chip; the empty state does not appear.
      expect(screen.getByText(/Did not answer in time:/)).toHaveTextContent('nzbhydra-slow');
      expect(screen.queryByText(/No source answered this search/)).toBeNull();
    });
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

  describe('result count (po-gate-plan-cpdo ruling 1)', () => {
    // Positive control: the limit-hit notice must genuinely depend on the page
    // equalling the limit ACTUALLY SENT, not just always render for any page.
    it('shows the limit-hit notice when the result count equals the submitted limit', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [response.releases[0], response.releases[0], response.releases[0]],
            provenance: response.provenance,
          },
        },
      });
      renderSurface(<SearchPage />);

      await user.type(screen.getByLabelText('Query'), 'some series');
      await user.clear(screen.getByLabelText('Limit'));
      await user.type(screen.getByLabelText('Limit'), '3');
      await user.click(screen.getByRole('button', { name: 'Search' }));

      expect(
        await screen.findByText('Showing 3 results, the limit you asked for. There may be more.'),
      ).toBeInTheDocument();
    });

    it('does not show the limit-hit notice when the page is short of the submitted limit', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [response.releases[0], response.releases[0]],
            provenance: response.provenance,
          },
        },
      });
      renderSurface(<SearchPage />);

      await user.type(screen.getByLabelText('Query'), 'some series');
      await user.clear(screen.getByLabelText('Limit'));
      await user.type(screen.getByLabelText('Limit'), '3');
      await user.click(screen.getByRole('button', { name: 'Search' }));

      expect(await screen.findByText('Showing 2 results.')).toBeInTheDocument();
      expect(screen.queryByText(/the limit you asked for/)).toBeNull();
    });

    it('renders singular wording for exactly one result', async () => {
      const user = userEvent.setup();
      mockApi({ '/api/admin/search': { body: response } });
      renderSurface(<SearchPage />);

      await runSearch(user);

      expect(await screen.findByText('Showing 1 result.')).toBeInTheDocument();
    });

    it('renders zero results without the limit-hit notice', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: { releases: [], provenance: response.provenance },
        },
      });
      renderSurface(<SearchPage />);

      await runSearch(user);

      // Zero releases takes the "no releases matched" empty-state branch, which
      // never renders ResultCount at all -- asserted here as the absence of the
      // limit-hit wording, since the empty state has its own dedicated test.
      expect(screen.queryByText(/the limit you asked for/)).toBeNull();
    });

    // The notice is read from the SUBMITTED criteria, never the live form: an
    // edit to the limit field after a search must not change what is shown for
    // results that were already fetched under the old limit.
    it('keeps the notice for on-screen results unchanged after editing the limit field post-submit', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [response.releases[0], response.releases[0], response.releases[0]],
            provenance: response.provenance,
          },
        },
      });
      renderSurface(<SearchPage />);

      await user.type(screen.getByLabelText('Query'), 'some series');
      await user.clear(screen.getByLabelText('Limit'));
      await user.type(screen.getByLabelText('Limit'), '3');
      await user.click(screen.getByRole('button', { name: 'Search' }));

      await screen.findByText('Showing 3 results, the limit you asked for. There may be more.');

      await user.clear(screen.getByLabelText('Limit'));
      await user.type(screen.getByLabelText('Limit'), '5');

      expect(
        screen.getByText('Showing 3 results, the limit you asked for. There may be more.'),
      ).toBeInTheDocument();
    });

    // Same guard for Clear: pressing it must not alter the notice for results
    // already on screen, since Clear only resets the form (ruling 4).
    it('keeps the notice for on-screen results unchanged after pressing Clear', async () => {
      const user = userEvent.setup();
      mockApi({
        '/api/admin/search': {
          body: {
            releases: [response.releases[0], response.releases[0], response.releases[0]],
            provenance: response.provenance,
          },
        },
      });
      renderSurface(<SearchPage />);

      await user.type(screen.getByLabelText('Query'), 'some series');
      await user.clear(screen.getByLabelText('Limit'));
      await user.type(screen.getByLabelText('Limit'), '3');
      await user.click(screen.getByRole('button', { name: 'Search' }));

      await screen.findByText('Showing 3 results, the limit you asked for. There may be more.');

      await user.click(screen.getByRole('button', { name: 'Clear' }));

      expect(
        screen.getByText('Showing 3 results, the limit you asked for. There may be more.'),
      ).toBeInTheDocument();
    });
  });

  describe('sorting (po-gate-plan-cpdo ruling 2/3)', () => {
    const sortResponse = {
      releases: [
        { ...response.releases[0], title: 'Bravo', size: 200, pubDate: '2026-09-02T00:00:00+00:00', guid: 'g-bravo' },
        { ...response.releases[0], title: 'Alpha', size: 0, pubDate: '2026-09-01T00:00:00+00:00', guid: 'g-alpha' },
        { ...response.releases[0], title: 'Charlie', size: 100, pubDate: 'not-a-date', guid: 'g-charlie' },
        { ...response.releases[0], title: 'Alpha', size: 300, pubDate: '2026-09-03T00:00:00+00:00', guid: 'g-alpha-2' },
      ],
      provenance: response.provenance,
    };

    function rowTitles() {
      return screen.getAllByRole('row').slice(1).map((row) => row.children[0].textContent);
    }

    it('renders in server order on first render with aria-sort="none" on every sortable header', async () => {
      const user = userEvent.setup();
      mockApi({ '/api/admin/search': { body: sortResponse } });
      renderSurface(<SearchPage />);

      await runSearch(user);
      await screen.findByText('Bravo');

      expect(rowTitles()).toEqual(['Bravo', 'Alpha', 'Charlie', 'Alpha']);
      expect(screen.getByRole('columnheader', { name: 'Title' })).toHaveAttribute('aria-sort', 'none');
      expect(screen.getByRole('columnheader', { name: 'Size' })).toHaveAttribute('aria-sort', 'none');
      expect(screen.getByRole('columnheader', { name: 'Published' })).toHaveAttribute(
        'aria-sort',
        'none',
      );
    });

    it('cycles Title asc -> desc -> server order on three clicks, updating aria-sort each time', async () => {
      const user = userEvent.setup();
      mockApi({ '/api/admin/search': { body: sortResponse } });
      renderSurface(<SearchPage />);

      await runSearch(user);
      await screen.findByText('Bravo');

      const titleHeader = screen.getByRole('columnheader', { name: 'Title' });
      const titleButton = screen.getByRole('button', { name: 'Title' });

      await user.click(titleButton);
      expect(titleHeader).toHaveAttribute('aria-sort', 'ascending');
      // Stability: the two 'Alpha' rows (g-alpha, g-alpha-2) keep their
      // server-order relative position under an equal sort key.
      expect(rowTitles()).toEqual(['Alpha', 'Alpha', 'Bravo', 'Charlie']);

      await user.click(titleButton);
      expect(titleHeader).toHaveAttribute('aria-sort', 'descending');
      expect(rowTitles()).toEqual(['Charlie', 'Bravo', 'Alpha', 'Alpha']);

      await user.click(titleButton);
      expect(titleHeader).toHaveAttribute('aria-sort', 'none');
      expect(rowTitles()).toEqual(['Bravo', 'Alpha', 'Charlie', 'Alpha']);
    });

    it('sorts Size on raw bytes with an absent size last in both directions', async () => {
      const user = userEvent.setup();
      mockApi({ '/api/admin/search': { body: sortResponse } });
      renderSurface(<SearchPage />);

      await runSearch(user);
      await screen.findByText('Bravo');

      const sizeButton = screen.getByRole('button', { name: 'Size' });

      await user.click(sizeButton);
      // The fixture's asc byte order is Charlie(100) < Bravo(200) < Alpha(300),
      // with the OTHER Alpha (size 0, sitting in the MIDDLE of the fixture
      // array) sorting last -- not merely excluded from the front.
      expect(rowTitles()).toEqual(['Charlie', 'Bravo', 'Alpha', 'Alpha']);

      await user.click(sizeButton);
      // desc: Alpha(300) > Bravo(200) > Charlie(100), the zero-size Alpha still last.
      expect(rowTitles()).toEqual(['Alpha', 'Bravo', 'Charlie', 'Alpha']);
    });

    it('sorts Published on the raw date with an unparseable date last in both directions', async () => {
      const user = userEvent.setup();
      // A dedicated fixture with four DISTINCT titles, one per date, so the asc
      // and desc expectations below are actually different arrays: the shared
      // `sortResponse` has two rows both titled 'Alpha' (g-alpha, g-alpha-2),
      // and since only their relative order (not membership) flips between
      // directions, a title-only assertion could not tell asc from desc apart.
      const publishedFixture = {
        releases: [
          { ...response.releases[0], title: 'Bravo', pubDate: '2026-09-02T00:00:00+00:00', guid: 'g-bravo' },
          { ...response.releases[0], title: 'Delta', pubDate: '2026-09-01T00:00:00+00:00', guid: 'g-delta' },
          { ...response.releases[0], title: 'Charlie', pubDate: 'not-a-date', guid: 'g-charlie' },
          { ...response.releases[0], title: 'Echo', pubDate: '2026-09-03T00:00:00+00:00', guid: 'g-echo' },
        ],
        provenance: response.provenance,
      };
      mockApi({ '/api/admin/search': { body: publishedFixture } });
      renderSurface(<SearchPage />);

      await runSearch(user);
      await screen.findByText('Bravo');

      const publishedButton = screen.getByRole('button', { name: 'Published' });

      await user.click(publishedButton);
      // asc: Delta(09-01) < Bravo(09-02) < Echo(09-03), Charlie (unparseable) last.
      expect(rowTitles()).toEqual(['Delta', 'Bravo', 'Echo', 'Charlie']);

      await user.click(publishedButton);
      // desc: Echo(09-03) > Bravo(09-02) > Delta(09-01), Charlie still last.
      expect(rowTitles()).toEqual(['Echo', 'Bravo', 'Delta', 'Charlie']);
    });
  });

  describe('Clear (ruling 4)', () => {
    it('resets the form and the selection but leaves results in the DOM without issuing a request', async () => {
      const user = userEvent.setup();
      const api = mockApi({
        '/api/admin/search': { body: response },
        '/api/admin/search/upstream-guid-1/explanation': {
          body: { title: 'x', originalTitle: 'y' },
        },
      });
      renderSurface(<SearchPage />);

      await user.type(screen.getByLabelText('TVDB id'), '12345');
      await runSearch(user);
      await screen.findByText('Some.Series.S01E02.1080p');

      await user.click(await screen.findByRole('button', { name: 'Explain' }));
      await screen.findByText('Match explanation');

      const callsBeforeClear = api.callsTo('/api/admin/search').length;

      await user.click(screen.getByRole('button', { name: 'Clear' }));

      expect(screen.getByLabelText('Query')).toHaveValue('');
      expect(screen.getByLabelText('TVDB id')).toHaveValue('');
      expect(screen.queryByText('Match explanation')).toBeNull();
      // Results stay until the next search.
      expect(screen.getByText('Some.Series.S01E02.1080p')).toBeInTheDocument();
      expect(api.callsTo('/api/admin/search')).toHaveLength(callsBeforeClear);
    });
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

    // arb-z505 tightened this from /No stored explanation for this release/ to
    // the full sentence. That prefix is shared verbatim with Suppressions'
    // DIFFERENT 404 sentence, so the loose form passed no matter which of the
    // two this surface rendered -- and routing both through QueryState's
    // `renderError` is exactly the change that could have swapped them.
    expect(
      await screen.findByText(
        'No stored explanation for this release. Ad-hoc results are not recorded in the release lookup, so only releases served through a Torznab search have one.',
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole('alert')).toHaveTextContent('Ad-hoc results are not recorded');
    expect(api.callsTo('/api/admin/search/upstream-guid-1/explanation')).toHaveLength(1);
    // Also admin-gated, and also a GET.
    expect(api.adminKeyOn('/api/admin/search/upstream-guid-1/explanation')).toBe('operator-key');
  });
});
