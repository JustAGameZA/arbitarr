import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiKeysSection } from './ApiKeys';
import { apiFetch } from '../../../api/client';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { renderSurface } from '../../../test/renderSurface';

const KEYS = '/api/admin/keys';

interface Reply {
  status?: number;
  body?: unknown;
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

describe('ApiKeys', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: 'test-admin-key', serverKeyUnset: false });
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
    await user.click(screen.getByRole('button', { name: 'Confirm revoke' }));

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
    await user.click(screen.getByRole('button', { name: 'Confirm revoke' }));

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
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

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
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

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
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

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
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // A second attempt, this time accepted. The confirm controls are still up --
    // a refused removal leaves the operator where they were rather than making them
    // find the row again -- so this retries straight from the confirm.
    api.set(`DELETE ${KEYS}/3/tombstone`, { status: 204 });
    api.set(`GET ${KEYS}`, { body: keys.filter((key) => key.id !== 3) });
    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));

    await vi.waitFor(() => expect(screen.queryByText(refusal)).not.toBeInTheDocument());
  });
});
