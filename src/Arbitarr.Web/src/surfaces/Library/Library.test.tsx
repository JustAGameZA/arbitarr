import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import LibraryPage from './Library';
import { LIBRARY_POLL_INTERVAL_MS, buildLibraryQuery, buildQueueQuery } from './queries';
import { mockApi, type MockRoutes } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';
import { useAdminKeyStore } from '../../state/adminKeyStore';

const SONARR_QUEUE = '/api/admin/arr/sonarr/queue';
const RADARR_QUEUE = '/api/admin/arr/radarr/queue';
const SONARR_SERIES = '/api/admin/arr/sonarr/series';
const RADARR_MOVIES = '/api/admin/arr/radarr/movies';

/**
 * A section envelope. The transport is ALWAYS 200 and the verdict is `status`, so every helper here
 * builds a success-shaped response — a failing section is a successful request carrying a failure,
 * which is exactly the distinction these tests exist to pin.
 */
function envelope<T>(
  status: string,
  message: string,
  records: T[] = [],
  extra: { page?: number; pageSize?: number; totalRecords?: number } = {},
) {
  return {
    body: {
      status,
      message,
      page: extra.page ?? 1,
      pageSize: extra.pageSize ?? 25,
      totalRecords: extra.totalRecords ?? records.length,
      records,
    },
  };
}

const queueRow = {
  id: 701,
  title: 'Example.Show.S01E01.1080p.WEB-DL',
  status: 'downloading',
  trackedDownloadStatus: 'ok',
  trackedDownloadState: 'downloading',
  size: 1024 ** 3,
  sizeLeft: 1024 ** 2 * 512,
  timeLeft: '00:12:34',
  estimatedCompletionTime: '2026-09-17T12:00:00+00:00',
  protocol: 'usenet',
  downloadClient: 'SABnzbd',
  indexer: 'Example Indexer',
  statusMessages: [],
  errorMessage: null,
};

const seriesRow = {
  id: 11,
  title: 'Example Show',
  year: 2019,
  tvdbId: 424242,
  status: 'continuing',
  monitored: true,
  seasonCount: 3,
  episodeFileCount: 12,
  episodeCount: 13,
  sizeOnDisk: 1024 ** 3 * 40,
  network: 'Example Network',
};

const movieRow = {
  id: 21,
  title: 'Example Movie',
  year: 2021,
  tmdbId: 515151,
  monitored: true,
  hasFile: true,
  sizeOnDisk: 1024 ** 3 * 8,
  status: 'released',
};

/** Every section Ok, each with one row. The baseline the failure cases depart from. */
const allOk: MockRoutes = {
  [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', [queueRow]),
  [RADARR_QUEUE]: envelope('Ok', 'Read the Radarr queue successfully.', [queueRow]),
  [SONARR_SERIES]: envelope('Ok', 'Read the Sonarr series successfully.', [seriesRow]),
  [RADARR_MOVIES]: envelope('Ok', 'Read the Radarr movie library successfully.', [movieRow]),
};

/** Selects a tab by its visible label and returns the panel. */
async function openTab(user: ReturnType<typeof userEvent.setup>, label: string) {
  await user.click(screen.getByRole('tab', { name: label }));
  return screen.getByRole('tabpanel');
}

beforeEach(() => {
  useAdminKeyStore.setState({ key: 'test-key', serverKeyUnset: false });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Library shell', () => {
  it('renders exactly one h1 and a tab per section', async () => {
    mockApi(allOk);
    renderSurface(<LibraryPage />);

    // AC2b: exactly one <h1> per view, and the tabs add none.
    const headings = screen.getAllByRole('heading', { level: 1 });
    expect(headings).toHaveLength(1);
    expect(headings[0]).toHaveTextContent('Library');

    expect(screen.getAllByRole('tab').map((tab) => tab.textContent)).toEqual([
      'Sonarr queue',
      'Sonarr series',
      'Radarr queue',
      'Radarr movies',
    ]);

    // Non-vacuity for the unmount rule below: the first tab really did load.
    expect(await screen.findByRole('table')).toBeInTheDocument();
  });

  it('reads only the active section, so an inactive tab never touches the operator arr', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    // POSITIVE CONTROL: the active section really is being read, so "the others are not" is
    // evidence about the unmounting and not about a mock nobody called.
    expect(api.callsTo(SONARR_QUEUE).length).toBeGreaterThan(0);
    expect(api.callsTo(RADARR_QUEUE)).toHaveLength(0);
    expect(api.callsTo(SONARR_SERIES)).toHaveLength(0);

    await openTab(user, 'Radarr movies');
    await screen.findByText('Example Movie');

    expect(api.callsTo(RADARR_MOVIES).length).toBeGreaterThan(0);
    // Still untouched: switching tabs unmounts rather than reveals.
    expect(api.callsTo(RADARR_QUEUE)).toHaveLength(0);
  });

  it('attaches the admin key by path prefix on every section', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    expect(api.adminKeyOn(SONARR_QUEUE)).toBe('test-key');

    await openTab(user, 'Sonarr series');
    await screen.findByText('Example Show');
    expect(api.adminKeyOn(SONARR_SERIES)).toBe('test-key');

    // The arr's OWN key never reaches the browser: nothing in any request URL carries one, and
    // the response has no member for it. Asserted on the URL because that is where a well-meaning
    // "just pass it through" change would put it.
    for (const call of api.calls) {
      expect(call.url.search).not.toContain('apikey');
    }
  });
});

describe('Library section verdicts', () => {
  /**
   * The five statuses the server declares, plus what each must produce.
   *
   * Ok and NotConfigured are the only two the client recognises; the rest share one treatment. They
   * are listed individually rather than looped over a "failure" bucket so that a regression that
   * special-cased one of them fails here by name.
   */
  const failures = [
    ['Unreachable', 'Could not reach Sonarr: no response from that address before the timeout.'],
    ['AuthenticationFailed', 'Sonarr is reachable but rejected the API key.'],
    ['UnexpectedResponse', 'Something answered but it was not Sonarr.'],
  ] as const;

  it.each(failures)('renders the server message for %s in the error treatment', async (
    status,
    message,
  ) => {
    mockApi({ ...allOk, [SONARR_QUEUE]: envelope(status, message) });
    renderSurface(<LibraryPage />);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(message);
  });

  it('shows the loading state before the first response resolves', async () => {
    mockApi(allOk);
    renderSurface(<LibraryPage />);

    // Synchronously after mount the query is pending, which is the only moment this is visible.
    expect(screen.getByText('Loading…')).toBeInTheDocument();

    // Non-vacuity: it really did resolve afterwards, so the assertion above caught a transient
    // state rather than a permanently stuck one.
    expect(await screen.findByRole('table')).toBeInTheDocument();
  });

  it('renders an unrecognised status verbatim rather than inventing wording for it', async () => {
    // A sixth member the server grows later. The client has never heard of it, and the correct
    // answer is the server's own sentence -- NOT a default branch reading "something went wrong",
    // which would replace a specific message with a vaguer one exactly when the client knows least.
    const marker = 'MARKER-3f9a11 the Sonarr instance is in maintenance mode';
    mockApi({ ...allOk, [SONARR_QUEUE]: envelope('MaintenanceMode', marker) });
    renderSurface(<LibraryPage />);

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(marker);
  });

  it('renders the server message verbatim for a status it does recognise', async () => {
    // The mirror of the case above, with a PLANTED marker inside a known status: this is what
    // proves the text comes from the response rather than from a client-side table that happens
    // to agree with the server's real wording.
    const marker = 'MARKER-7c21be could not reach the instance';
    mockApi({ ...allOk, [SONARR_QUEUE]: envelope('Unreachable', marker) });
    renderSurface(<LibraryPage />);

    expect(await screen.findByRole('alert')).toHaveTextContent(marker);
  });

  it('offers a link to Arbitarr settings when a section is not configured', async () => {
    const message = 'Sonarr is not configured. Add its base URL and API key in the Sonarr section.';
    mockApi({ ...allOk, [SONARR_QUEUE]: envelope('NotConfigured', message) });
    renderSurface(<LibraryPage />);

    expect(await screen.findByText(message)).toBeInTheDocument();
    // Arbitarr's OWN settings anchor, never the operator's arr: that address is sensitive and this
    // surface is never told it.
    expect(screen.getByRole('link', { name: 'Open Sonarr settings' })).toHaveAttribute(
      'href',
      '/settings#sonarr',
    );
  });

  it('points a Radarr section at the Radarr anchor', async () => {
    const message = 'Radarr is not configured.';
    mockApi({ ...allOk, [RADARR_MOVIES]: envelope('NotConfigured', message) });
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    await openTab(user, 'Radarr movies');

    expect(await screen.findByText(message)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Open Radarr settings' })).toHaveAttribute(
      'href',
      '/settings#radarr',
    );
  });

  it('renders an empty Ok section as what would fill it, never as a failure', async () => {
    mockApi({ ...allOk, [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', []) });
    renderSurface(<LibraryPage />);

    expect(await screen.findByText(/Nothing is downloading/)).toBeInTheDocument();
    // An empty queue is a healthy queue: no alert, and no table of zero rows.
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('keeps one failing section from blanking another', async () => {
    // FIRST show both CAN render in this setup, so the failure below is evidence about isolation
    // rather than about a tab that never worked.
    const bothOk = mockApi(allOk);
    const user = userEvent.setup();
    const first = renderSurface(<LibraryPage />);
    expect(await screen.findByText('Example.Show.S01E01.1080p.WEB-DL')).toBeInTheDocument();
    await openTab(user, 'Radarr movies');
    expect(await screen.findByText('Example Movie')).toBeInTheDocument();
    expect(bothOk.callsTo(RADARR_MOVIES).length).toBeGreaterThan(0);
    first.unmount();

    // Now fail ONLY Sonarr's queue. Radarr's movies must be untouched.
    mockApi({
      ...allOk,
      [SONARR_QUEUE]: envelope('Unreachable', 'Could not reach Sonarr.'),
    });
    const second = userEvent.setup();
    renderSurface(<LibraryPage />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not reach Sonarr.');

    await openTab(second, 'Radarr movies');
    expect(await screen.findByText('Example Movie')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });
});

describe('Library tables', () => {
  it('renders queue rows with sizes formatted and the upstream time left verbatim', async () => {
    mockApi(allOk);
    renderSurface(<LibraryPage />);

    const table = await screen.findByRole('table');
    expect(within(table).getByText('Example.Show.S01E01.1080p.WEB-DL')).toBeInTheDocument();
    expect(within(table).getByText('1.0 GiB')).toBeInTheDocument();
    expect(within(table).getByText('512.0 MiB')).toBeInTheDocument();
    // Upstream's own opaque string, not re-derived into a second duration convention.
    expect(within(table).getByText('00:12:34')).toBeInTheDocument();
  });

  it('renders an unrecognised tracked status as text, without inventing a severity for it', async () => {
    // POSITIVE CONTROL FIRST: a status the mapping DOES know carries a severity class. Without
    // this, "the unknown one has no severity class" would pass just as well if the component had
    // stopped emitting severity classes entirely, or if the class names resolved to empty strings
    // under the test's CSS handling -- an absence assertion that nothing could ever fail.
    mockApi({
      ...allOk,
      [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', [
        { ...queueRow, trackedDownloadStatus: 'warning' },
      ]),
    });
    const { unmount } = renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const known = screen.getByText('warning');
    expect(known.className.split(/\s+/).length).toBeGreaterThan(1);
    const severityClasses = known.className
      .split(/\s+/)
      .filter((name) => name !== '')
      .slice(1);
    expect(severityClasses.length).toBeGreaterThan(0);
    unmount();

    // The real case: upstream grows a status nobody here has seen. It renders its own text, so the
    // operator still learns what their *arr said, and it takes the PLAIN badge -- colouring it
    // would be a severity claim the data does not support.
    mockApi({
      ...allOk,
      [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', [
        { ...queueRow, trackedDownloadStatus: 'quarantined' },
      ]),
    });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const unknown = await screen.findByText('quarantined');
    for (const severity of severityClasses) {
      expect(unknown.className.split(/\s+/)).not.toContain(severity);
    }
  });

  it('renders two queue rows that share a title, because the id is what keys them', async () => {
    // Two files of the same release is the ordinary case this guards, not a contrived one. A key
    // synthesised from the title would collide here, and React would drop or merge a row -- which
    // is the whole reason the projection carries upstream's id at all.
    mockApi({
      ...allOk,
      [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', [
        { ...queueRow, id: 701, title: 'Example.Show.S01E01.1080p.WEB-DL' },
        { ...queueRow, id: 702, title: 'Example.Show.S01E01.1080p.WEB-DL' },
      ]),
    });
    renderSurface(<LibraryPage />);
    const table = await screen.findByRole('table');

    // BOTH, asserted by count rather than by "the title is present": one surviving row renders the
    // same text as two and would satisfy a findByText just as happily.
    expect(screen.getAllByText('Example.Show.S01E01.1080p.WEB-DL')).toHaveLength(2);
    expect(within(table).getAllByRole('row')).toHaveLength(3);
  });

  it('renders an absent size as the em-dash rather than a plausible zero', async () => {
    mockApi({
      ...allOk,
      [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', [
        { ...queueRow, size: null, sizeLeft: null, timeLeft: null },
      ]),
    });
    renderSurface(<LibraryPage />);

    const table = await screen.findByRole('table');
    const cells = within(table).getAllByRole('cell');
    const dashes = cells.filter((cell) => cell.textContent === '—');
    // Three: size, remaining, time left. A size that is unknown is a different fact from one that
    // is zero bytes, which is what format.ts's shared convention exists to keep distinct.
    expect(dashes).toHaveLength(3);
    // The exact character, matching format.ts's pinned U+2014.
    expect(dashes[0].textContent?.codePointAt(0)).toBe(0x2014);
  });

  it('renders series and movie rows in their own tabs', async () => {
    mockApi(allOk);
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    await openTab(user, 'Sonarr series');
    const series = await screen.findByRole('table');
    expect(within(series).getByText('Example Show')).toBeInTheDocument();
    expect(within(series).getByText('12 of 13')).toBeInTheDocument();
    expect(within(series).getByText('40.0 GiB')).toBeInTheDocument();

    await openTab(user, 'Radarr movies');
    const movies = await screen.findByRole('table');
    expect(within(movies).getByText('Example Movie')).toBeInTheDocument();
    expect(within(movies).getByText('8.0 GiB')).toBeInTheDocument();
  });

  it('renders no filesystem path, because the projection carries none', async () => {
    // DETECTABILITY CONTROL: a path-shaped value really is planted on the wire, in a member the
    // client does not declare. The assertion below is therefore about the RENDER dropping it, not
    // about a fixture that never carried one.
    const planted = '/mnt/planted-path/tv/Example Show/Season 01';
    const body = {
      status: 'Ok',
      message: 'Read the Sonarr queue successfully.',
      page: 1,
      pageSize: 25,
      totalRecords: 1,
      records: [{ ...queueRow, outputPath: planted }],
    };
    expect(JSON.stringify(body)).toContain(planted);

    mockApi({ ...allOk, [SONARR_QUEUE]: { body } });
    renderSurface(<LibraryPage />);

    await screen.findByRole('table');
    expect(document.body.textContent).not.toContain(planted);
  });
});

describe('Library paging', () => {
  /** 25 rows with a total of 60, so the pager renders and claims three pages. */
  const page1 = Array.from({ length: 25 }, (_, index) => ({
    ...queueRow,
    id: 900 + index,
    title: `Example.Show.S01E${String(index).padStart(2, '0')}.1080p.WEB-DL`,
  }));

  const pagedRoutes: MockRoutes = {
    ...allOk,
    [SONARR_QUEUE]: envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
      page: 1,
      pageSize: 25,
      totalRecords: 60,
    }),
  };

  it('hides the pager when everything fits on one page', async () => {
    mockApi(allOk);
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    expect(screen.queryByRole('button', { name: 'Next' })).not.toBeInTheDocument();
  });

  it('derives the page count from the envelope rather than from the request', async () => {
    mockApi(pagedRoutes);
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    // 60 rows at 25 per page is 3 pages -- a ceiling, not a truncation.
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
  });

  it('asks the server for the next page and reports the page the server served', async () => {
    const api = mockApi(pagedRoutes);
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    api.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 2,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));

    expect(await screen.findByText('Page 2 of 3')).toBeInTheDocument();
    expect(api.callsTo(SONARR_QUEUE).at(-1)?.url.searchParams.get('page')).toBe('2');
    expect(screen.getByRole('button', { name: 'Previous' })).toBeEnabled();
  });

  /**
   * Holds the next reply open so a click can land mid-flight.
   *
   * `mockApi` answers synchronously, which is the right default everywhere else and useless here:
   * the desync this guards is only reachable while a response is OUTSTANDING. So the stubbed fetch
   * is wrapped rather than replaced -- the routing, the recorded calls and the admin key all stay
   * `mockApi`'s -- and only the settling of the promise is taken over.
   */
  function deferNext() {
    const real = globalThis.fetch as unknown as (
      input: string,
      init?: RequestInit,
    ) => Promise<Response>;
    let release!: () => void;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    let armed = true;

    vi.stubGlobal('fetch', (input: string, init?: RequestInit) => {
      const pending = real(input, init);
      if (!armed) {
        return pending;
      }
      armed = false;
      return gate.then(() => pending);
    });

    return release;
  }

  it('does not swallow a second Next pressed while the first is still in flight', async () => {
    const api = mockApi(pagedRoutes);
    const user = userEvent.setup();
    const control = renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    // POSITIVE CONTROL: with nothing deferred, two consecutive clicks reach page 3. If the harness
    // could not drive two clicks through at all, the assertion below would pass for that reason
    // instead of for the one it names.
    api.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 2,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('Page 2 of 3');
    api.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 3,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('Page 3 of 3');
    expect(api.callsTo(SONARR_QUEUE).at(-1)?.url.searchParams.get('page')).toBe('3');
    control.unmount();

    // The real case. Back to page 1, then hold the page-2 response open and click Next twice.
    // Under the old pager both clicks computed their target from the SERVED page, which
    // keepPreviousData still reported as 1, so the second click recomputed 2 and was swallowed.
    const fresh = mockApi(pagedRoutes);
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const release = deferNext();
    fresh.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 2,
        pageSize: 25,
        totalRecords: 60,
      }),
    );

    const next = screen.getByRole('button', { name: 'Next' });
    await user.click(next);

    // While the request is outstanding the control is frozen, so the second press cannot be
    // aimed at a stale page in the first place. That is the fix stated as a user-visible fact:
    // the operator is never offered a button whose target disagrees with the number beside it.
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();

    release();
    expect(await screen.findByText('Page 2 of 3')).toBeInTheDocument();

    // And once it settles, Next advances to 3 rather than re-requesting 2 -- the click is routed
    // from the requested page, so no press is spent re-asking for the page already on screen.
    fresh.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 3,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(await screen.findByText('Page 3 of 3')).toBeInTheDocument();
    expect(fresh.callsTo(SONARR_QUEUE).at(-1)?.url.searchParams.get('page')).toBe('3');
  });

  it('resets to page one when the tab changes', async () => {
    const api = mockApi({
      ...pagedRoutes,
      [RADARR_MOVIES]: envelope('Ok', 'Read the Radarr movie library successfully.', [movieRow]),
    });
    const user = userEvent.setup();
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    api.set(
      SONARR_QUEUE,
      envelope('Ok', 'Read the Sonarr queue successfully.', page1, {
        page: 2,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    // Positive control: page 2 really was reached, so the reset below is a change and not a
    // position that was never left.
    expect(await screen.findByText('Page 2 of 3')).toBeInTheDocument();

    await openTab(user, 'Radarr movies');
    await screen.findByText('Example Movie');

    // Page 3 of Sonarr's queue is not a meaningful position in Radarr's movies.
    expect(api.callsTo(RADARR_MOVIES).at(-1)?.url.searchParams.has('page')).toBe(false);
  });
});

describe('Library filter debounce', () => {
  beforeEach(() => {
    // Fake timers let a test fast-forward past the ~250ms window instead of racing a real one;
    // `shouldAdvanceTime` lets Testing Library's own polling keep making progress against it.
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('costs one request for three quick keystrokes', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup({ delay: null });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');
    await openTab(user, 'Sonarr series');
    await screen.findByText('Example Show');

    const before = api.callsTo(SONARR_SERIES).length;
    await user.type(screen.getByLabelText('Search'), 'exa');
    // No new request yet -- the debounce window has not elapsed. Advancing the clock only after
    // the whole word lands proves the requests in between never fired, not merely that the LAST
    // one carried the right value.
    expect(api.callsTo(SONARR_SERIES)).toHaveLength(before);

    await vi.advanceTimersByTimeAsync(250);

    const after = api.callsTo(SONARR_SERIES);
    expect(after).toHaveLength(before + 1);
    expect(after.at(-1)?.url.searchParams.get('q')).toBe('exa');
  });

  it('omits the filter parameter when the box is cleared', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup({ delay: null });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');
    await openTab(user, 'Sonarr series');
    await screen.findByText('Example Show');

    const input = screen.getByLabelText('Search');
    await user.type(input, 'exa');
    await vi.advanceTimersByTimeAsync(250);
    // Positive control: the parameter is on the wire while filtered, so its absence after clearing
    // is evidence the filter changed rather than evidence it was never sent.
    expect(api.callsTo(SONARR_SERIES).at(-1)?.url.searchParams.get('q')).toBe('exa');

    await user.clear(input);
    await vi.advanceTimersByTimeAsync(250);

    // Omitted, not `q=` -- an empty string would be a filter the server treats as absent only by
    // accident.
    expect(api.callsTo(SONARR_SERIES).at(-1)?.url.searchParams.has('q')).toBe(false);
  });

  it('does not let the mount run of the debounce reset a page the operator chose', async () => {
    // arb-6l13, carried across from LogsTab. The debounce effect also runs on MOUNT, and a timer
    // that reset the page unconditionally when it fired ~250ms later would throw an operator who
    // clicked Next inside that window back to page 1. Nothing is typed here, so a correct debounce
    // has nothing to commit and must leave both the page and the request alone.
    const paged = envelope('Ok', 'Read the Sonarr series successfully.', [seriesRow], {
      page: 1,
      pageSize: 25,
      totalRecords: 60,
    });
    const api = mockApi({ ...allOk, [SONARR_SERIES]: paged });
    const user = userEvent.setup({ delay: null, advanceTimers: vi.advanceTimersByTime });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');
    await openTab(user, 'Sonarr series');
    await screen.findByText('Page 1 of 3');

    api.set(
      SONARR_SERIES,
      envelope('Ok', 'Read the Sonarr series successfully.', [seriesRow], {
        page: 2,
        pageSize: 25,
        totalRecords: 60,
      }),
    );
    await user.click(screen.getByRole('button', { name: 'Next' }));
    await screen.findByText('Page 2 of 3');

    // Drive the clock PAST the debounce deadline, so the mount timer has certainly fired.
    await vi.advanceTimersByTimeAsync(500);

    expect(screen.getByText('Page 2 of 3')).toBeInTheDocument();
    expect(api.callsTo(SONARR_SERIES).at(-1)?.url.searchParams.get('page')).toBe('2');
  });
});

describe('Library polling', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    // Leave the document visible for the next test: `hidden` is stubbed per-test below and a
    // leaked `true` would silence every later poll assertion into a false pass.
    vi.spyOn(document, 'hidden', 'get').mockRestore?.();
  });

  it('re-reads the active section once per interval while the document is visible', async () => {
    const api = mockApi(allOk);
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const before = api.callsTo(SONARR_QUEUE).length;
    // Non-vacuity: the first read really happened, so a later count is a poll and not the mount.
    expect(before).toBeGreaterThan(0);

    await vi.advanceTimersByTimeAsync(LIBRARY_POLL_INTERVAL_MS);

    expect(api.callsTo(SONARR_QUEUE)).toHaveLength(before + 1);
  });

  it('issues no request while the document is hidden, and resumes when it becomes visible', async () => {
    const api = mockApi(allOk);
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const before = api.callsTo(SONARR_QUEUE).length;
    expect(before).toBeGreaterThan(0);

    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    document.dispatchEvent(new Event('visibilitychange'));

    await vi.advanceTimersByTimeAsync(LIBRARY_POLL_INTERVAL_MS * 2);

    // NOT "fewer requests" -- none at all. A hidden tab must cost the operator's arr nothing.
    expect(api.callsTo(SONARR_QUEUE)).toHaveLength(before);

    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    document.dispatchEvent(new Event('visibilitychange'));

    await vi.advanceTimersByTimeAsync(LIBRARY_POLL_INTERVAL_MS);

    // The count proves the interval RE-ARMED rather than merely that the resume fired once.
    expect(api.callsTo(SONARR_QUEUE).length).toBeGreaterThan(before);
  });

  it('re-reads immediately when Refresh is pressed', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup({ delay: null, advanceTimers: vi.advanceTimersByTime });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    const before = api.callsTo(SONARR_QUEUE).length;
    expect(before).toBeGreaterThan(0);

    await user.click(screen.getByRole('button', { name: 'Refresh' }));

    // Exactly one more, without the clock having advanced an interval: the point of the control is
    // that an operator need not wait out a five-minute cadence.
    expect(api.callsTo(SONARR_QUEUE)).toHaveLength(before + 1);
  });

  it('serves a manual Refresh while hidden without re-arming the automatic cadence', async () => {
    const api = mockApi(allOk);
    const user = userEvent.setup({ delay: null, advanceTimers: vi.advanceTimersByTime });
    renderSurface(<LibraryPage />);
    await screen.findByRole('table');

    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true);
    document.dispatchEvent(new Event('visibilitychange'));

    const before = api.callsTo(SONARR_QUEUE).length;
    expect(before).toBeGreaterThan(0);

    // The two facts are different and the gate only claims one of them. A Refresh is IMPERATIVE --
    // an operator asking for data now -- so it fires while hidden and that is correct. What the
    // visibility gate suppresses is the unattended timer, which nobody asked for.
    await user.click(screen.getByRole('button', { name: 'Refresh' }));
    expect(api.callsTo(SONARR_QUEUE)).toHaveLength(before + 1);

    const afterRefresh = api.callsTo(SONARR_QUEUE).length;
    await vi.advanceTimersByTimeAsync(LIBRARY_POLL_INTERVAL_MS * 2);

    // Two full intervals later the count is UNCHANGED from just after the refresh. Serving the
    // manual read must not restart the cadence behind it: a refresh pressed before backgrounding
    // the browser would otherwise leave a hidden tab polling someone else's server indefinitely.
    expect(api.callsTo(SONARR_QUEUE)).toHaveLength(afterRefresh);
  });
});

describe('Library query builders', () => {
  // Their own tests, exactly as buildLogsQuery has: the filter-to-URL mapping is the part that
  // breaks silently. A dropped `page` still renders plausible rows -- page 1's -- under a pager
  // claiming to be on page 3, which no render-level assertion catches.
  it('omits page 1 and always names the page size', () => {
    expect(buildQueueQuery('sonarr', 1)).toBe('/api/admin/arr/sonarr/queue?pageSize=25');
    expect(buildQueueQuery('radarr', 3)).toBe('/api/admin/arr/radarr/queue?page=3&pageSize=25');
  });

  it('routes each kind at its own collection and omits a blank filter', () => {
    expect(buildLibraryQuery('sonarr', '', 1)).toBe('/api/admin/arr/sonarr/series?pageSize=25');
    expect(buildLibraryQuery('radarr', '   ', 1)).toBe('/api/admin/arr/radarr/movies?pageSize=25');
    expect(buildLibraryQuery('sonarr', ' example ', 2)).toBe(
      '/api/admin/arr/sonarr/series?q=example&page=2&pageSize=25',
    );
  });
});
