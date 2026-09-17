import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { ReactElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiKeysSection } from './ApiKeys';
import { apiFetch } from '../../../api/client';
import { AppShell } from '../../../components/shell/AppShell';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { useLiveStatusStore } from '../../../state/liveStatusStore';
import { renderSurface } from '../../../test/renderSurface';

const KEYS = '/api/admin/keys';

interface Reply {
  status?: number;
  body?: unknown;
  /**
   * Never resolves (arb-kytb). Used to hold one call's mutation `isPending`
   * true for the lifetime of a test, so the per-row disabled state can be
   * observed without racing a real settle.
   */
  pending?: boolean;
}

interface Call {
  path: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
}

/**
 * A fetch double keyed by METHOD AND PATH, rather than the shared `mockApi`'s
 * longest-prefix-by-path rule.
 *
 * This section is the one surface where three different methods share a single
 * path: the list GET, the create POST, and the revoke DELETE all live on
 * `/api/admin/keys`. Under prefix-only matching, staging the create response
 * would also answer the list refetch that `invalidateQueries` fires immediately
 * after it — handing an array-rendering table one object and failing on
 * `entries.map`. That is a limitation of the double, not of the component, so it
 * is fixed here rather than by weakening the component or by changing a helper
 * five other suites depend on.
 */
function mockKeysApi(replies: Record<string, Reply>) {
  const table = new Map<string, Reply>(Object.entries(replies));
  const calls: Call[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const url = new URL(input, 'http://localhost');
      const method = init?.method ?? 'GET';
      calls.push({
        path: url.pathname,
        method,
        headers: { ...((init?.headers as Record<string, string>) ?? {}) },
        body: typeof init?.body === 'string' ? init.body : undefined,
      });

      const reply = table.get(`${method} ${url.pathname}`);
      if (reply === undefined) {
        // Unrouted rather than silently empty, so a request nobody staged fails
        // loudly instead of rendering a blank panel.
        return Promise.resolve(
          new Response(JSON.stringify({ error: `No mock for ${method} ${url.pathname}` }), {
            status: 501,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      }

      if (reply.pending === true) {
        return new Promise<Response>(() => {});
      }

      const status = reply.status ?? 200;
      return Promise.resolve(
        new Response(status === 204 || reply.body === undefined ? null : JSON.stringify(reply.body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );

  return {
    calls,
    set: (route: string, reply: Reply) => table.set(route, reply),
    of: (method: string) => calls.filter((call) => call.method === method),
  };
}

/**
 * A list covering every row shape the section has to render distinctly: an active
 * read key, an active admin key, a REVOKED key (which must survive as a tombstone
 * rather than vanishing), and the synthetic LEGACY row, whose null id is why it can
 * carry no revoke button.
 *
 * There are TWO revoked rows on purpose (#98). A per-row assertion about removal
 * needs a second tombstone to be an assertion at all: with only one, "the removed
 * row is gone" is indistinguishable from "every revoked row was swept", which is
 * precisely the bug the per-row check exists to catch.
 */
const keys = [
  {
    id: 1,
    label: 'Sonarr',
    scope: 'ReadOnly',
    createdAt: '2026-01-05T10:00:00Z',
    lastUsedAt: '2026-02-01T08:30:00Z',
    revokedAt: null,
    isLegacy: false,
  },
  {
    id: 2,
    label: 'Maintenance script',
    scope: 'Admin',
    createdAt: '2026-01-06T10:00:00Z',
    lastUsedAt: null,
    revokedAt: null,
    isLegacy: false,
  },
  {
    id: 3,
    label: 'Retired laptop',
    scope: 'ReadOnly',
    createdAt: '2025-11-01T10:00:00Z',
    lastUsedAt: '2025-12-02T09:00:00Z',
    revokedAt: '2026-01-02T12:00:00Z',
    isLegacy: false,
  },
  {
    id: 4,
    label: 'Old script',
    scope: 'ReadOnly',
    createdAt: '2025-10-01T10:00:00Z',
    lastUsedAt: '2025-10-20T09:00:00Z',
    revokedAt: '2025-11-15T12:00:00Z',
    isLegacy: false,
  },
  {
    id: null,
    label: 'Shared admin key (legacy)',
    scope: 'Admin',
    createdAt: null,
    lastUsedAt: null,
    revokedAt: null,
    isLegacy: true,
  },
];

const PLAINTEXT = 'arb_live_REDACTEDPLAINTEXTVALUE_0123456789';

const created = {
  key: {
    id: 9,
    label: 'Radarr',
    scope: 'ReadOnly',
    createdAt: '2026-03-01T10:00:00Z',
    lastUsedAt: null,
    revokedAt: null,
    isLegacy: false,
  },
  plaintextKey: PLAINTEXT,
};

/**
 * Mounts the section against a client the test can then inspect.
 *
 * `renderSurface` builds its QueryClient internally and does not hand it back,
 * which is right for every other suite — none of them need to look inside the
 * cache. This one does: the MutationCache is the thing under test. Rather than
 * widen a helper five other suites depend on, this rebuilds its two providers
 * locally, keeping the same retry-off defaults so behaviour is identical.
 *
 * The client's own defaults deliberately set NO gcTime, mirroring
 * `src/api/queryClient.ts`. If the mutation's `gcTime: 0` were removed, the
 * five-minute default would apply here exactly as it does in the app.
 */
function renderWithClient() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ApiKeysSection />
      </MemoryRouter>
    </QueryClientProvider>,
  );

  return client;
}

/**
 * Serialises everything the MutationCache is holding.
 *
 * `state.data` is where a settled create keeps its whole response — the plaintext
 * with it — so this is the sweep an operator with the devtools open would be doing
 * by hand. Whole mutation objects rather than just `state`, so a copy parked on
 * any other field would be caught too.
 */
function sweepMutationCache(client: QueryClient): string {
  return JSON.stringify(client.getMutationCache().getAll());
}

/**
 * The POSITIVE CONTROL for the two cache tests below, and the reason they bite.
 *
 * This is the SAME create, against the same fetch double, through a mutation that
 * differs from the shipped one in exactly one respect: no `gcTime: 0` and no
 * reset. It reproduces the retention this change exists to remove, and proves the
 * sweep FINDS a plaintext that is really there. Without it, `not.toContain` at the
 * end of each test would pass just as happily against a cache that never held a
 * create at all, or against a sweep pointed at the wrong place — an empty set
 * contains nothing.
 *
 * It is spelled out here rather than planted as a hand-built cache entry because a
 * planted object only proves `JSON.stringify` can see a string. Driving the real
 * mutation proves the retention is a property of react-query's defaults, which is
 * the claim the fix answers.
 */
function LeakyCreateHarness() {
  const mutation = useMutation({
    mutationFn: (request: { label: string; scope: string }) =>
      apiFetch<unknown>('/api/admin/keys', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
  });

  return (
    <button type="button" onClick={() => mutation.mutate({ label: 'Radarr', scope: 'ReadOnly' })}>
      Leaky create
    </button>
  );
}

function renderLeakyHarness() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(
    <QueryClientProvider client={client}>
      <LeakyCreateHarness />
    </QueryClientProvider>,
  );

  return client;
}

/**
 * Patience, not a behaviour change.
 *
 * The vitest config sets `css: true`, so every render here parses the real
 * stylesheet, and the create-path tests each drive a full mutation round trip on
 * top of that. On a loaded machine they sit close enough to the 5s default to turn
 * an ordinary render into a spurious failure. The assertions are unchanged.
 */
const TEST_TIMEOUT_MS = 20_000;

/** The row whose first cell holds `label`. */
function rowFor(label: string): HTMLElement {
  return screen.getByRole('cell', { name: label }).closest('tr') as HTMLElement;
}

/**
 * Mounts the section inside the real `AppShell` (arb-zxwo).
 *
 * `renderSurface` deliberately omits the shell, and the shell is where the one
 * shared `role="status"` region lives — so a copy-announcement assertion made
 * under `renderSurface` would have no region to find and could only be written
 * against a stand-in. Used ONLY by the copy tests below, whose subject is the
 * announcement itself; every other test in this file keeps `renderSurface` and
 * stays independent of the shell's markup, which is why that helper exists.
 */
function renderInShell(element: ReactElement) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <AppShell>{element}</AppShell>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('ApiKeys', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-admin-key', serverKeyUnset: false });
    // The live-status store is a singleton across this file, so a test that left
    // "Copied." announced would decide the next one's "was it empty before?"
    // control by ordering alone (arb-zxwo).
    useLiveStatusStore.setState({ message: '', seq: 0, scopeMessages: {} });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists every key with its scope, created and last-used columns', async () => {
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    expect(await screen.findByRole('cell', { name: 'Sonarr' })).toBeInTheDocument();
    expect(within(rowFor('Sonarr')).getByText('Read only')).toBeInTheDocument();
    expect(within(rowFor('Maintenance script')).getByText('Admin')).toBeInTheDocument();

    // A key never used shows the em-dash rather than a fabricated date.
    expect(within(rowFor('Maintenance script')).getAllByText('—').length).toBeGreaterThan(0);
  });

  it('keeps a revoked key as a tombstone rather than dropping it from the list', async () => {
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Retired laptop' });
    const row = within(rowFor('Retired laptop'));
    expect(row.getByText(/^Revoked /)).toBeInTheDocument();
    // The tombstone offers no revoke affordance -- it is already revoked. Since #98
    // it does offer a REMOVE one, which is a different control with a different
    // name; this assertion still bites because "Remove Retired laptop" does not
    // match /revoke/i.
    expect(row.queryByRole('button', { name: /revoke/i })).not.toBeInTheDocument();
    expect(row.getByRole('button', { name: 'Remove Retired laptop' })).toBeInTheDocument();
  });

  it('renders the legacy key with no revoke button and says why', async () => {
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Shared admin key (legacy)' });
    const row = within(rowFor('Shared admin key (legacy)'));
    expect(row.queryByRole('button', { name: /revoke/i })).not.toBeInTheDocument();
    expect(row.getByText(/server configuration/i)).toBeInTheDocument();
  });

  it('names what would fill the empty state', async () => {
    mockKeysApi({ [`GET ${KEYS}`]: { body: [] } });
    renderSurface(<ApiKeysSection />);

    // Names what would fill it (#52), asserted on the empty-state element itself:
    // the scope help and the label placeholder mention the same products, so a bare
    // text match would pass even with the empty state absent.
    const empty = await screen.findByText(/No API keys yet/i);
    expect(empty).toHaveTextContent(/Sonarr or Radarr instance/i);
    expect(empty).toHaveTextContent(/revocable credential/i);
  });

  /**
   * Every URL the section renders, as text.
   *
   * Collected by reading the <code> elements on screen rather than by
   * re-deriving `origin + path`: a helper that computed the expected strings
   * itself would agree with a broken component for the same reason the
   * component was broken.
   */
  function renderedUrls(): string[] {
    return [...document.querySelectorAll('code')]
      .map((element) => element.textContent ?? '')
      .filter((text) => text.includes('://'));
  }

  it('renders the exact *arr-facing routes, /torznab/api and /newznab/api', async () => {
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });

    // The literal suffixes, not merely "a URL is shown". A wrong path — an
    // /api/ prefix, a version segment, a trailing slash — is the entire 404 this
    // block exists to prevent, and a laxer assertion would pass against it.
    const origin = window.location.origin;
    expect(screen.getByText(`${origin}/torznab/api`)).toBeInTheDocument();
    expect(screen.getByText(`${origin}/newznab/api`)).toBeInTheDocument();

    // Both families are offered, with Torznab named preferred as the README says.
    expect(screen.getByText(/preferred/i)).toBeInTheDocument();

    // The caveat: the browser's origin is not necessarily what the *arr reaches.
    expect(screen.getByText(/externally reachable address may differ/i)).toBeInTheDocument();
  });

  it('never renders the key inside a URL — it is the *arr form’s separate field', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 201, body: created });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    // The planted key really is in play: it reached the reveal panel. Without
    // this, the absence assertion below would pass just as happily on a render
    // that never received a key at all.
    expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();

    const urls = renderedUrls();
    expect(urls.length).toBeGreaterThan(0);

    // POSITIVE CONTROL: prove this search WOULD catch a key baked into a URL, so
    // the assertion that follows detects absence rather than reporting a search
    // that could never have matched.
    const withLeak = [...urls, `${window.location.origin}/torznab/api?apikey=${PLAINTEXT}`];
    expect(withLeak.some((url) => url.includes(PLAINTEXT))).toBe(true);

    expect(urls.some((url) => url.includes(PLAINTEXT))).toBe(false);
  });

  it('copies a URL best-effort, and survives a context with no clipboard API', async () => {
    const user = userEvent.setup();
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });

    const writeText = vi.fn(() => Promise.resolve());
    vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

    await user.click(screen.getByRole('button', { name: 'Copy Torznab URL' }));
    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/torznab/api`);
    expect(await screen.findByText('Copied.')).toBeInTheDocument();

    // No clipboard at all — the jsdom and non-secure-context case the copy
    // closure's optional chaining exists for. The click must not throw, and the
    // URL must stay on screen to be selected by hand.
    vi.stubGlobal('navigator', { ...navigator, clipboard: undefined });
    await user.click(screen.getByRole('button', { name: 'Copy Newznab URL' }));
    expect(screen.getByText(`${window.location.origin}/newznab/api`)).toBeInTheDocument();
  });

  /**
   * arb-zxwo: both copy affordances announce through the shared live region.
   *
   * review-488's finding on the client-URL buttons, and the identical
   * pre-existing gap on the reveal panel: the "Copied." span is visual only, so
   * an operator who cannot see it cannot tell a successful copy from a click
   * that did nothing. On the reveal panel that is the difference between having
   * the only copy of a credential and having lost it.
   *
   * Every assertion is written as "did not say Copied. before, does after",
   * never as a bare `toHaveTextContent`: without the before-check, an
   * end-state assertion would pass against an implementation that announced at
   * the wrong moment or for the wrong reason.
   *
   * The control is "not Copied." rather than "empty", and that was MEASURED,
   * not assumed: the first draft asserted `toBeEmptyDOMElement()` and all three
   * tests failed with the region holding "Loading…". The section mounts its own
   * `QueryState` for the key list, which legitimately announces while that query
   * is in flight, and an announcement is never retracted (`liveStatusStore`'s
   * `seq` doc). So the region is genuinely non-empty before any copy, and the
   * honest control is that it does not yet carry THIS message.
   *
   * For the same reason the VISIBLE span is always queried with the live region
   * excluded: the region legitimately holds "Copied." after a successful copy,
   * so a bare `queryByText('Copied.')` matches two nodes and asserts the
   * opposite of what these tests mean.
   */
  describe('copy success announcement (arb-zxwo)', () => {
    /** The visible "Copied." spans only, never the shared live region. */
    function visibleCopiedFeedback(): HTMLElement[] {
      return screen
        .queryAllByText('Copied.')
        .filter((node) => node.getAttribute('role') !== 'status');
    }

    it('announces a copied client URL through the shared live region', async () => {
      const user = userEvent.setup();
      mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderInShell(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });

      // CONTROL: the region does NOT carry this message before the copy, so the
      // assertion after it detects this click rather than reporting something
      // already there. (It is not empty — the list query announced "Loading…".)
      expect(screen.getByRole('status')).not.toHaveTextContent('Copied.');

      const writeText = vi.fn(() => Promise.resolve());
      vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

      await user.click(screen.getByRole('button', { name: 'Copy Torznab URL' }));

      await waitFor(() => {
        expect(visibleCopiedFeedback()).toHaveLength(1);
      });
      // The visible span and the live region are SEPARATE elements; this asserts
      // the shared region specifically received it, not merely that the word
      // appears somewhere on the page.
      await waitFor(() => {
        expect(screen.getByRole('status')).toHaveTextContent('Copied.');
      });
    });

    it('announces a copied revealed key without putting the key in the announcement', async () => {
      const user = userEvent.setup();
      const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderInShell(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });
      api.set(`POST ${KEYS}`, { status: 201, body: created });

      await user.type(screen.getByLabelText('New key label'), 'Radarr');
      await user.click(screen.getByRole('button', { name: 'Create key' }));

      // The reveal panel is open and really holds the plaintext.
      expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();
      // CONTROL: nothing has announced "Copied." yet.
      expect(screen.getByRole('status')).not.toHaveTextContent('Copied.');

      const writeText = vi.fn(() => Promise.resolve());
      vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

      const reveal = screen.getByRole('alert');
      await user.click(within(reveal).getByRole('button', { name: 'Copy' }));

      expect(writeText).toHaveBeenCalledWith(PLAINTEXT);
      await waitFor(() => {
        expect(screen.getByRole('status')).toHaveTextContent('Copied.');
      });

      // The key was copied to the CLIPBOARD, never into the announcement. The
      // region is rendered into AppShell's markup, which outlives this panel,
      // and the panel is the plaintext's only home.
      //
      // POSITIVE CONTROL for that absence: prove this search WOULD catch the
      // key if the announcement carried it, so the assertion below detects
      // absence rather than reporting a search that could never have matched.
      const announced = screen.getByRole('status').textContent ?? '';
      expect(`${announced} ${PLAINTEXT}`).toContain(PLAINTEXT);
      expect(announced).not.toContain(PLAINTEXT);
    });

    it('announces nothing when there is no clipboard API to copy with', async () => {
      const user = userEvent.setup();
      mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderInShell(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });

      // The no-clipboard branch the copy closures' optional chaining exists for
      // (jsdom, and any non-secure context). Announcing here would tell a
      // screen-reader operator the value was copied when nothing was — strictly
      // worse than the silence this replaces, because it is wrong rather than
      // merely absent.
      vi.stubGlobal('navigator', { ...navigator, clipboard: undefined });

      await user.click(screen.getByRole('button', { name: 'Copy Torznab URL' }));

      expect(visibleCopiedFeedback()).toHaveLength(0);
      expect(screen.getByRole('status')).not.toHaveTextContent('Copied.');

      // CONTROL: the very same region DOES pick up an announcement, so the
      // silence above is the success branch correctly not firing rather than
      // this test watching an element that never receives anything.
      const writeText = vi.fn(() => Promise.resolve());
      vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });
      await user.click(screen.getByRole('button', { name: 'Copy Torznab URL' }));
      await waitFor(() => {
        expect(screen.getByRole('status')).toHaveTextContent('Copied.');
      });
    });

    it(
      'clears the visible Copied. after its timeout, leaving the URL as it was',
      async () => {
        // REAL timers, deliberately. `vi.useFakeTimers()` here deadlocked the
        // whole file -- not this test alone: the run produced no test output at
        // all and had to be killed. userEvent's own timer scheduling and React
        // Query's do not both survive being faked under this setup, and the
        // failure mode is a hang rather than a failed assertion, which is worse
        // than the couple of seconds waiting the real clock costs.
        const user = userEvent.setup();
        mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
        renderInShell(<ApiKeysSection />);

        await screen.findByRole('cell', { name: 'Sonarr' });

        const writeText = vi.fn(() => Promise.resolve());
        vi.stubGlobal('navigator', { ...navigator, clipboard: { writeText } });

        await user.click(screen.getByRole('button', { name: 'Copy Torznab URL' }));
        // CONTROL: it really appeared, so its absence below is it clearing
        // rather than it never having rendered.
        await waitFor(() => {
          expect(visibleCopiedFeedback()).toHaveLength(1);
        });

        // The acknowledgement is transient: it clears itself rather than
        // persisting until unmount, which on a section that stays mounted for a
        // whole session means indefinitely. Waited for rather than asserted
        // immediately, so this pins "it goes away on its own" without the test
        // having to know the exact interval.
        await waitFor(
          () => {
            expect(visibleCopiedFeedback()).toHaveLength(0);
          },
          { timeout: 6000 },
        );

        // The ANNOUNCEMENT is not retracted with it: an announcement records an
        // event that happened, and the live region has no clear by design.
        expect(screen.getByRole('status')).toHaveTextContent('Copied.');

        // The URL it acknowledged is untouched — only the feedback cleared.
        expect(screen.getByText(`${window.location.origin}/torznab/api`)).toBeInTheDocument();
      },
      TEST_TIMEOUT_MS,
    );
  });

  it('explains what each scope reaches, and changes the explanation with the choice', async () => {
    const user = userEvent.setup();
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    expect(await screen.findByText(/public search and download routes/i)).toBeInTheDocument();
    expect(await screen.findByText(/It cannot change rules, settings, sources or keys/i))
      .toBeInTheDocument();

    await user.selectOptions(screen.getByLabelText('New key scope'), 'Admin');
    expect(screen.getByText(/every mutating admin route/i)).toBeInTheDocument();
  });

  it('posts the scope by NAME, never its numeric index', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 201, body: created });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.selectOptions(screen.getByLabelText('New key scope'), 'Admin');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    const post = api.of('POST')[0];
    expect(post).toBeDefined();
    // The server matches the two scope names explicitly and rejects the numeric
    // form on purpose; a client posting an index would be asking for that guard
    // to be relaxed.
    expect(JSON.parse(post!.body!)).toEqual({ label: 'Radarr', scope: 'Admin' });
  });

  it('shows the created plaintext once, says it will never be shown again, and drops it on dismiss', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 201, body: created });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();
    expect(screen.getByText(/only time it will ever be shown/i)).toBeInTheDocument();
    expect(screen.getByText(/no screen, route or support procedure that can show it again/i))
      .toBeInTheDocument();

    // Dismissal requires an explicit acknowledgement: the button is inert until
    // the operator asserts they have the value.
    const dismiss = screen.getByRole('button', { name: 'Dismiss' });
    expect(dismiss).toBeDisabled();

    await user.click(screen.getByLabelText(/I have copied this key somewhere safe/i));
    expect(dismiss).toBeEnabled();
    await user.click(dismiss);

    expect(screen.queryByText(PLAINTEXT)).not.toBeInTheDocument();
  });

  it('never writes the plaintext to localStorage or sessionStorage', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 201, body: created });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    // The plaintext really is in play: it reached the DOM. Without this, the two
    // absence assertions below would pass just as happily on a render that never
    // received a key at all -- an empty set contains nothing.
    expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();

    const dump = () =>
      [localStorage, sessionStorage]
        .flatMap((store) =>
          Object.keys(store).map((name) => `${name}=${store.getItem(name) ?? ''}`),
        )
        .join('\n');

    // POSITIVE CONTROL: prove the search would FIND the plaintext if it were
    // there, so the assertions that follow are detecting absence rather than
    // reporting a search that could never have matched.
    localStorage.setItem('positive-control', PLAINTEXT);
    expect(dump()).toContain(PLAINTEXT);
    localStorage.removeItem('positive-control');

    expect(dump()).not.toContain(PLAINTEXT);
  });

  it('revokes only after confirmation, and sends DELETE for that key', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));

    // Asking is not doing: nothing goes to the server until the confirm.
    expect(api.of('DELETE')).toHaveLength(0);

    api.set(`DELETE ${KEYS}/1`, { status: 204 });
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));

    const del = api.of('DELETE')[0];
    expect(del?.path).toBe(`${KEYS}/1`);
  });

  it("renders the server's last-admin-key refusal verbatim", async () => {
    const user = userEvent.setup();
    // ApiKeyRepository.cs's actual AC5 wording, copied verbatim. Asserted as one
    // literal string, and deliberately a long specific one: a client that
    // paraphrased the refusal, or substituted a guess of its own, cannot satisfy
    // this test by accident. Nothing on the client counts admin keys.
    const refusal =
      "'Maintenance script' is the last API key with admin scope. Revoking it would leave no " +
      'key able to administer this instance. Create a replacement admin-scope key first, ' +
      'then revoke this one.';
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Maintenance script' });
    await user.click(screen.getByRole('button', { name: 'Revoke Maintenance script' }));

    api.set(`DELETE ${KEYS}/2`, { status: 400, body: { error: refusal } });
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Maintenance script' }));

    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // And it renders in the row of the key it refused, not under the table: the
    // message names one key, so a floating copy would read as a statement about
    // the list. The untouched key's row carries no error.
    expect(within(rowFor('Maintenance script')).getByText(refusal)).toBeInTheDocument();
    expect(within(rowFor('Sonarr')).queryByRole('alert')).not.toBeInTheDocument();
  });

  it('does not carry a failed create message into the next attempt', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });

    // First attempt is rejected by the server.
    api.set(`POST ${KEYS}`, {
      status: 400,
      body: { error: "An API key labelled 'Sonarr' already exists." },
    });
    await user.type(screen.getByLabelText('New key label'), 'Sonarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));
    expect(await screen.findByText(/already exists/i)).toBeInTheDocument();

    // Second attempt succeeds. Without create.reset() the mutation keeps its
    // previous error until the new one settles, leaving the stale rejection on
    // screen beneath a value the server has not rejected.
    api.set(`POST ${KEYS}`, { status: 201, body: created });
    await user.clear(screen.getByLabelText('New key label'));
    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();
    expect(screen.queryByText(/already exists/i)).not.toBeInTheDocument();
  });

  it('retains the plaintext in the mutation cache without gcTime and reset', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`POST ${KEYS}`]: { status: 201, body: created } });
    const client = renderLeakyHarness();

    // Nothing yet -- so the assertion below cannot be satisfied by a cache that
    // was already dirty before the create ran.
    expect(sweepMutationCache(client)).not.toContain(PLAINTEXT);

    await user.click(screen.getByRole('button', { name: 'Leaky create' }));
    await vi.waitFor(() => expect(api.of('POST')).toHaveLength(1));

    // THE POSITIVE CONTROL. A create through a mutation with react-query's default
    // gcTime and no reset leaves the whole response -- plaintext included -- sitting
    // in the MutationCache after it settles. This is the retention the shipped
    // mutation removes, and it is what makes the two absence assertions in the next
    // test evidence rather than a search that could never have matched.
    await vi.waitFor(() => expect(sweepMutationCache(client)).toContain(PLAINTEXT));
  }, TEST_TIMEOUT_MS);

  it('leaves no copy of the plaintext in the react-query mutation cache', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    const client = renderWithClient();

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 201, body: created });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    // The reveal still gets the key, exactly once. This is the half that fails if
    // the reset is called synchronously from a hook-level onSettled: react-query
    // awaits those callbacks BEFORE dispatching the success, and reset() removes
    // the observer the dispatch would have notified, so the per-call onSuccess
    // that captures the response never runs at all.
    expect(await screen.findByText(PLAINTEXT)).toBeInTheDocument();
    expect(screen.getAllByText(PLAINTEXT)).toHaveLength(1);

    await user.click(screen.getByLabelText(/I have copied this key somewhere safe/i));
    await user.click(screen.getByRole('button', { name: 'Dismiss' }));
    expect(screen.queryByText(PLAINTEXT)).not.toBeInTheDocument();

    // And the cache kept nothing. Not the reveal being closed -- the value is gone
    // from the last place on the client that could still hold it, which the test
    // above proved would otherwise be holding it.
    await vi.waitFor(() => expect(sweepMutationCache(client)).not.toContain(PLAINTEXT));
  }, TEST_TIMEOUT_MS);

  it('leaves nothing in the cache when the server rejects the create, and still shows its words', async () => {
    const user = userEvent.setup();
    // The repository's real duplicate-label refusal, verbatim. A client that
    // paraphrased it, or substituted a guess, cannot satisfy this by accident.
    const refusal = "An API key labelled 'Radarr' already exists.";
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    const client = renderWithClient();

    await screen.findByRole('cell', { name: 'Sonarr' });
    api.set(`POST ${KEYS}`, { status: 400, body: { error: refusal } });

    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    // The rejection still reaches the operator. This is the other half the
    // same-tick reset destroys: the per-call onError never runs, so the refusal is
    // swallowed and the form sits there looking as though nothing happened.
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // A rejected create never held a plaintext, so the interesting assertion is
    // that the mutation itself is not left parked in the cache carrying the label
    // and scope that were attempted.
    await vi.waitFor(() => expect(sweepMutationCache(client)).not.toContain('Radarr'));
    expect(sweepMutationCache(client)).not.toContain(PLAINTEXT);
  }, TEST_TIMEOUT_MS);

  it('offers the remove action on revoked rows only', async () => {
    // #98 AC3's visibility rule, asserted across every row shape at once rather
    // than only on the row that has the button. The live rows are the ones that
    // matter: an affordance there would be a one-click path to destroying a working
    // credential, which is exactly what the two-step exists to prevent.
    mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });

    // Both revoked rows carry it.
    expect(
      within(rowFor('Retired laptop')).getByRole('button', { name: 'Remove Retired laptop' }),
    ).toBeInTheDocument();
    expect(
      within(rowFor('Old script')).getByRole('button', { name: 'Remove Old script' }),
    ).toBeInTheDocument();

    // Neither live row does, nor the legacy row, whose null id could address nothing.
    expect(within(rowFor('Sonarr')).queryByRole('button', { name: /remove/i })).not.toBeInTheDocument();
    expect(
      within(rowFor('Maintenance script')).queryByRole('button', { name: /remove/i }),
    ).not.toBeInTheDocument();
    expect(
      within(rowFor('Shared admin key (legacy)')).queryByRole('button', { name: /remove/i }),
    ).not.toBeInTheDocument();
  });

  it('removes only after confirmation, and sends DELETE to that key\'s tombstone', async () => {
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Retired laptop' });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));

    // Asking is not doing. This one is worth asserting harder than the revoke's
    // equivalent: a removal cannot be undone or inspected afterwards.
    expect(api.of('DELETE')).toHaveLength(0);

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 204 });
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

    const del = api.of('DELETE')[0];
    // The tombstone sub-path, not the bare id: that route revokes, and hitting it
    // here would be a no-op on an already-revoked key rather than a removal.
    expect(del?.path).toBe(`${KEYS}/3/tombstone`);
    expect(api.of('DELETE')).toHaveLength(1);
  });

  it('drops only the removed row, leaving the live and the other revoked rows', async () => {
    // #98 AC4 on the client. The refetched list is what the table renders from, so
    // this asserts the section reflects the server's answer per row rather than
    // inventing a disappearance -- and the SECOND tombstone is the assertion that
    // bites: a client that hid every revoked row on success would pass without it.
    const user = userEvent.setup();
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Retired laptop' });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 204 });
    api.set(`GET ${KEYS}`, { body: keys.filter((key) => key.id !== 3) });

    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

    await vi.waitFor(() =>
      expect(screen.queryByRole('cell', { name: 'Retired laptop' })).not.toBeInTheDocument(),
    );

    expect(screen.getByRole('cell', { name: 'Sonarr' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: 'Maintenance script' })).toBeInTheDocument();
    // The other tombstone is untouched, still revoked, still removable.
    expect(screen.getByRole('cell', { name: 'Old script' })).toBeInTheDocument();
    expect(within(rowFor('Old script')).getByText(/^Revoked /)).toBeInTheDocument();
    expect(
      within(rowFor('Old script')).getByRole('button', { name: 'Remove Old script' }),
    ).toBeInTheDocument();
  });

  it("renders the server's refusal for a still-live key verbatim, in that key's row", async () => {
    const user = userEvent.setup();
    // ApiKeyRepository.RemoveRevokedAsync's actual wording, copied verbatim and
    // asserted as one long literal: a client that paraphrased it, or substituted a
    // guess of its own about why removal was refused, cannot satisfy this by
    // accident. Nothing on the client decides which keys are removable.
    const refusal =
      "'Retired laptop' is still live and cannot be removed from the list. Revoke it first, " +
      'then remove it \u2014 removing a working credential in one step would take a caller ' +
      'offline with no confirmation that it had stopped being used.';
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Retired laptop' });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 400, body: { error: refusal } });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

    // Reaching the operator at all is the first half: the mutation is reset the
    // moment it settles, so a render that read `remove.error` would show nothing.
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // And it renders in the row of the key it refused, not under the table -- the
    // message names one key, so a floating copy would read as a statement about the
    // list. The other tombstone carries no error.
    expect(within(rowFor('Retired laptop')).getByText(refusal)).toBeInTheDocument();
    expect(within(rowFor('Old script')).queryByRole('alert')).not.toBeInTheDocument();
    expect(within(rowFor('Sonarr')).queryByRole('alert')).not.toBeInTheDocument();

    // The row is still there: a refused removal removed nothing.
    expect(screen.getByRole('cell', { name: 'Retired laptop' })).toBeInTheDocument();
  });

  /**
   * arb-kytb: a revoke or remove in flight disables only its own row.
   *
   * The section used to pass `revoke.isPending || remove.isPending` to EVERY
   * row, so one key's in-flight call disabled the confirm button on every
   * other row too. Each test below holds one request open (never resolving
   * it) and asserts POSITIVELY that the in-flight row's own confirm button
   * IS disabled first — proving the disabled state really fires for that
   * call — before asserting the other rows are NOT, so the second assertion
   * is evidence of per-row scoping rather than a check that could never have
   * failed.
   */
  describe('per-row pending (arb-kytb)', () => {
    it('disables only the confirming row while its revoke is in flight', async () => {
      const user = userEvent.setup();
      const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderSurface(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });
      await screen.findByRole('cell', { name: 'Maintenance script' });

      // Hold Sonarr's revoke open — never resolved in this test.
      api.set(`DELETE ${KEYS}/1`, { pending: true });

      await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
      await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));

      // POSITIVE CONTROL: the in-flight row's own confirm button IS disabled,
      // so the negative assertions below detect real per-row scoping rather
      // than a `pending` that never fires at all.
      await waitFor(() => {
        expect(
          within(rowFor('Sonarr')).getByRole('button', { name: 'Confirm revoke Sonarr' }),
        ).toBeDisabled();
      });

      // A different row's revoke-confirm is untouched by Sonarr's in-flight call.
      await user.click(screen.getByRole('button', { name: 'Revoke Maintenance script' }));
      expect(
        within(rowFor('Maintenance script')).getByRole('button', {
          name: 'Confirm revoke Maintenance script',
        }),
      ).toBeEnabled();

      // And a revoked row's remove-confirm is untouched too.
      await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
      expect(
        within(rowFor('Retired laptop')).getByRole('button', {
          name: 'Confirm remove Retired laptop',
        }),
      ).toBeEnabled();
    });

    it('disables only the confirming row while its remove is in flight', async () => {
      const user = userEvent.setup();
      const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderSurface(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Retired laptop' });
      await screen.findByRole('cell', { name: 'Old script' });

      // Hold Retired laptop's tombstone removal open — never resolved.
      api.set(`DELETE ${KEYS}/3/tombstone`, { pending: true });

      await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
      await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

      // POSITIVE CONTROL: the in-flight row's own confirm button IS disabled.
      await waitFor(() => {
        expect(
          within(rowFor('Retired laptop')).getByRole('button', {
            name: 'Confirm remove Retired laptop',
          }),
        ).toBeDisabled();
      });

      // A different tombstone's remove-confirm is untouched.
      await user.click(screen.getByRole('button', { name: 'Remove Old script' }));
      expect(
        within(rowFor('Old script')).getByRole('button', { name: 'Confirm remove Old script' }),
      ).toBeEnabled();

      // A live row's revoke-confirm is untouched too.
      await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
      expect(
        within(rowFor('Sonarr')).getByRole('button', { name: 'Confirm revoke Sonarr' }),
      ).toBeEnabled();
    });
  });

  it('keeps two concurrent refusals on their own rows, and clears only the row that retries (arb-39g3)', async () => {
    // arb-39g3: revokeFailedId/revoke.error and removeFailedId/removeFailure
    // used to be single shared scalars, so a second refusal in flight
    // overwrote the first row's text -- row A's message vanished or appeared
    // under row B. This drives two DIFFERENT rows' refusals unresolved AT THE
    // SAME TIME (one revoke, one remove, since that is what the three-row
    // brief calls for and it exercises both Maps at once), with a third row
    // untouched throughout.
    const user = userEvent.setup();
    const refusalA =
      "'Sonarr' cannot be revoked right now: a positive control refusal for row A.";
    const refusalB =
      "'Retired laptop' cannot be removed right now: a positive control refusal for row B.";
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    await screen.findByRole('cell', { name: 'Retired laptop' });
    await screen.findByRole('cell', { name: 'Old script' });

    // Refuse A ("Sonarr", revoke) and B ("Retired laptop", remove), both left
    // unresolved.
    api.set(`DELETE ${KEYS}/1`, { status: 400, body: { error: refusalA } });
    await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));
    await waitFor(() => {
      expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();
    });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 400, body: { error: refusalB } });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

    // POSITIVE CONTROLS first: each row's own message really is there, and A's
    // survived B's refusal landing afterward.
    await waitFor(() => {
      expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
    });
    expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();

    // THEN neither message leaks into the other rows or above the table.
    expect(within(rowFor('Retired laptop')).queryByText(refusalA)).not.toBeInTheDocument();
    expect(within(rowFor('Old script')).queryByText(refusalA)).not.toBeInTheDocument();
    expect(within(rowFor('Sonarr')).queryByText(refusalB)).not.toBeInTheDocument();
    expect(within(rowFor('Old script')).queryByText(refusalB)).not.toBeInTheDocument();
    expect(screen.getAllByText(refusalA)).toHaveLength(1);
    expect(screen.getAllByText(refusalB)).toHaveLength(1);

    // Retry A successfully: its own refusal clears, B's stays exactly as it was.
    api.set(`DELETE ${KEYS}/1`, { status: 204 });
    await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));

    await waitFor(() => {
      expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    });
    // B's refusal is untouched by A's retry -- another row's success must not
    // clear a refusal it did not own.
    expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
  });

  it('prunes a refusal once its row leaves the list, but keeps it for a row that stays (arb-gn4z)', async () => {
    // Two rows are refused (A, a revoke; B, a remove). A then leaves the list
    // on a refetch a THIRD row's action triggers, and later returns under the
    // SAME id via a later fetch. The stale refusal must not resurface. B never
    // leaves, so its refusal must survive every refetch untouched.
    const user = userEvent.setup();
    const refusalA = "'Sonarr' cannot be revoked right now: a positive control refusal for row A.";
    const refusalB =
      "'Retired laptop' cannot be removed right now: a positive control refusal for row B.";
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    await screen.findByRole('cell', { name: 'Retired laptop' });
    await screen.findByRole('cell', { name: 'Old script' });

    // Refuse A ("Sonarr", revoke) and B ("Retired laptop", remove), both left
    // unresolved.
    api.set(`DELETE ${KEYS}/1`, { status: 400, body: { error: refusalA } });
    await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));
    await waitFor(() => {
      expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();
    });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 400, body: { error: refusalB } });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));
    await waitFor(() => {
      expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
    });

    // POSITIVE CONTROL: A's refusal really is there before its row vanishes.
    expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();

    // Row 4 ("Old script")'s own remove succeeds, and its refetch's response
    // no longer includes row 1 ("Sonarr") at all -- the row left the list
    // through an action that has nothing to do with A's own refusal or retry.
    api.set(`DELETE ${KEYS}/4/tombstone`, { status: 204 });
    api.set(`GET ${KEYS}`, { body: keys.filter((entry) => entry.id !== 1 && entry.id !== 4) });
    await user.click(screen.getByRole('button', { name: 'Remove Old script' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Old script' }));

    await waitFor(() => {
      expect(screen.queryByRole('cell', { name: 'Old script' })).not.toBeInTheDocument();
    });
    // A's row is also gone, and so is its refusal text, pruned rather than
    // merely unrendered because nothing keys back to a row anymore.
    expect(screen.queryByRole('cell', { name: 'Sonarr' })).not.toBeInTheDocument();
    expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    // B never left: its refusal survives this refetch untouched.
    expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();

    // Row 1 ("Sonarr") returns under the SAME id via a later fetch -- a key
    // recreated by another admin session, say. A fresh create is used to
    // cause the invalidation, a mutation whose own success is unrelated to
    // either A or B, so B's still-unresolved refusal is untouched by anything
    // this step does directly.
    api.set(`GET ${KEYS}`, { body: [keys[0], keys[2]] });
    api.set(`POST ${KEYS}`, { status: 201, body: created });
    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await waitFor(() => {
      expect(screen.getByRole('cell', { name: 'Sonarr' })).toBeInTheDocument();
    });
    // The old refusal for id 1 must NOT resurface just because the id is back.
    expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    // B's own, still-unresolved refusal is unaffected by A's return.
    expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
  });

  it('keeps every refusal through a refetch that reorders or edits rows without changing membership (arb-gn4z)', async () => {
    // Same shape as Rules' equivalent test: the prune effects (revokeFailures
    // and removeFailures both) key off row ids present vs. absent, so a
    // refetch that changes order or an unrelated field, but not which ids are
    // present, must not disturb either Map. Also the no-render-loop case: the
    // functional setState in each prune effect must return the SAME Map when
    // membership is unchanged.
    const user = userEvent.setup();
    const refusalA = "'Sonarr' cannot be revoked right now: a positive control refusal for row A.";
    const refusalB =
      "'Retired laptop' cannot be removed right now: a positive control refusal for row B.";
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Sonarr' });
    await screen.findByRole('cell', { name: 'Retired laptop' });

    api.set(`DELETE ${KEYS}/1`, { status: 400, body: { error: refusalA } });
    await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
    await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));
    await waitFor(() => {
      expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();
    });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 400, body: { error: refusalB } });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));
    await waitFor(() => {
      expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
    });

    // POSITIVE CONTROL: both refusals are really there before the no-op refetch.
    expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();
    expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();

    // Trigger a refetch via a successful, unrelated create -- every id (1, 2,
    // 3, 4, null) is still present, reordered, and one field (lastUsedAt) on
    // a row that was never refused changed.
    api.set(`GET ${KEYS}`, {
      body: [keys[2], keys[0], { ...keys[1], lastUsedAt: '2026-03-01T00:00:00Z' }, keys[3], keys[4]],
    });
    api.set(`POST ${KEYS}`, { status: 201, body: created });
    await user.type(screen.getByLabelText('New key label'), 'Radarr');
    await user.click(screen.getByRole('button', { name: 'Create key' }));

    await waitFor(() => {
      expect(api.calls.some((call) => call.method === 'POST')).toBe(true);
    });
    // The create's own success reveal panel takes over the view, but the
    // underlying list refetch (and this effect) already ran off its result --
    // both refusals below are read from the same DOM the reveal panel sits
    // alongside, so this is exercising the refetched data, not a stale render.
    await waitFor(() => {
      expect(within(rowFor('Sonarr')).getByText(refusalA)).toBeInTheDocument();
    });
    expect(within(rowFor('Retired laptop')).getByText(refusalB)).toBeInTheDocument();
  });

  it('does not carry a failed remove message into the next attempt', async () => {
    // The capture-then-reset shape has to clear as well as capture. Without the
    // reset of the previous refusal, the operator reads a rejection the server has
    // not issued for the row now under the cursor.
    const user = userEvent.setup();
    const refusal = "'Retired laptop' is still live and cannot be removed from the list.";
    const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
    renderSurface(<ApiKeysSection />);

    await screen.findByRole('cell', { name: 'Retired laptop' });

    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 400, body: { error: refusal } });
    await user.click(screen.getByRole('button', { name: 'Remove Retired laptop' }));
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // A second attempt, this time accepted. The confirm controls are still up --
    // a refused removal leaves the operator where they were rather than making them
    // find the row again -- so this retries straight from the confirm.
    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 204 });
    api.set(`GET ${KEYS}`, { body: keys.filter((key) => key.id !== 3) });
    await user.click(screen.getByRole('button', { name: 'Confirm remove Retired laptop' }));

    await vi.waitFor(() => expect(screen.queryByText(refusal)).not.toBeInTheDocument());
  });

  describe('two mutates in flight at once (arb-kytb follow-up)', () => {
    it('opens and confirms a second row while an earlier revoke is still in flight', async () => {
      // The invariant the per-row `pending` prop rests on: `confirmingId` is
      // section-wide, so only one row's Confirm is ever RENDERED at a time.
      // But `onAskConfirm`/`setConfirmingId` has no guard against moving it to
      // a different row while an earlier revoke or remove is still
      // outstanding — observed directly here, rather than assumed: after
      // Sonarr's revoke is held open, Maintenance script's own confirm is
      // reachable and clickable, proving a second mutate CAN start.
      const user = userEvent.setup();
      const api = mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderSurface(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });
      await screen.findByRole('cell', { name: 'Maintenance script' });

      // Sonarr's revoke never resolves within this test.
      api.set(`DELETE ${KEYS}/1`, { pending: true });

      await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
      await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));

      // POSITIVE CONTROL: Sonarr's own confirm really is disabled by its
      // in-flight call.
      await waitFor(() => {
        expect(
          within(rowFor('Sonarr')).getByRole('button', { name: 'Confirm revoke Sonarr' }),
        ).toBeDisabled();
      });

      // Nothing stops moving confirmingId to Maintenance script while
      // Sonarr's revoke is still outstanding, and its Confirm is enabled --
      // this second mutate is reachable.
      await user.click(screen.getByRole('button', { name: 'Revoke Maintenance script' }));
      expect(
        within(rowFor('Maintenance script')).getByRole('button', {
          name: 'Confirm revoke Maintenance script',
        }),
      ).toBeEnabled();

      api.set(`DELETE ${KEYS}/2`, { pending: true });
      await user.click(
        screen.getByRole('button', { name: 'Confirm revoke Maintenance script' }),
      );

      // Both revokes are now in flight at once.
      await waitFor(() => {
        expect(
          within(rowFor('Maintenance script')).getByRole('button', {
            name: 'Confirm revoke Maintenance script',
          }),
        ).toBeDisabled();
      });
    });

    it('reads pending PER ROW with two revokes in flight at once, and clears independently on settle', async () => {
      // pendingRevokeIds/pendingRemoveIds are Sets rather than
      // `revoke.variables === entry.id`, because `variables` can only ever
      // name the LATEST call. This proves the Set tracks BOTH concurrent
      // calls, and that settling one leaves the other's row alone.
      const user = userEvent.setup();
      mockKeysApi({ [`GET ${KEYS}`]: { body: keys } });
      renderSurface(<ApiKeysSection />);

      await screen.findByRole('cell', { name: 'Sonarr' });
      await screen.findByRole('cell', { name: 'Maintenance script' });

      // POSITIVE CONTROL: before either revoke starts, neither row is pending.
      expect(screen.getByRole('button', { name: 'Revoke Sonarr' })).not.toBeDisabled();

      // Sonarr's revoke is held open indefinitely. Maintenance script's gets
      // its own controllable promise so it can be settled independently.
      let resolveMaintenance: (() => void) | undefined;
      vi.mocked(fetch).mockImplementation((input: RequestInfo | URL, init?: RequestInit) => {
        const url = new URL(input instanceof Request ? input.url : input, 'http://localhost');
        if (init?.method === 'DELETE' && url.pathname === `${KEYS}/1`) {
          return new Promise(() => {});
        }
        if (init?.method === 'DELETE' && url.pathname === `${KEYS}/2`) {
          return new Promise((resolve) => {
            resolveMaintenance = () => resolve(new Response(null, { status: 204 }));
          });
        }
        return Promise.resolve(
          new Response(JSON.stringify(keys), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      });

      await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
      await user.click(screen.getByRole('button', { name: 'Confirm revoke Sonarr' }));

      await waitFor(() => {
        expect(
          within(rowFor('Sonarr')).getByRole('button', { name: 'Confirm revoke Sonarr' }),
        ).toBeDisabled();
      });

      await user.click(screen.getByRole('button', { name: 'Revoke Maintenance script' }));
      await user.click(
        screen.getByRole('button', { name: 'Confirm revoke Maintenance script' }),
      );

      // PER ROW, with both in flight at once: Maintenance script reads
      // pending, and an untouched row does not.
      await waitFor(() => {
        expect(
          within(rowFor('Maintenance script')).getByRole('button', {
            name: 'Confirm revoke Maintenance script',
          }),
        ).toBeDisabled();
      });
      expect(screen.getByRole('button', { name: 'Remove Retired laptop' })).not.toBeDisabled();

      // Read Sonarr's own state back: still disabled, because its revoke is
      // still in flight -- proving the Set kept it even while
      // Maintenance script's call was added and is running concurrently. A
      // single `revoke.variables` check could not show this: by now
      // `variables` names Maintenance script's call, not Sonarr's.
      await user.click(screen.getByRole('button', { name: 'Revoke Sonarr' }));
      expect(
        within(rowFor('Sonarr')).getByRole('button', { name: 'Confirm revoke Sonarr' }),
      ).toBeDisabled();

      // Settle Maintenance script only. Its confirming state clears on
      // success, while Sonarr's promise never resolved.
      resolveMaintenance?.();
      await waitFor(() => {
        expect(
          within(rowFor('Maintenance script')).getByRole('button', {
            name: 'Revoke Maintenance script',
          }),
        ).toBeInTheDocument();
      });
    });
  });
});
