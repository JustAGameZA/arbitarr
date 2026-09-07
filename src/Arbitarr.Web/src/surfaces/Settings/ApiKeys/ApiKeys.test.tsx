import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ApiKeysSection } from './ApiKeys';
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
 * A list covering all four row shapes the section has to render distinctly:
 * an active read key, an active admin key, a REVOKED key (which must survive as a
 * tombstone rather than vanishing), and the synthetic LEGACY row, whose null id is
 * why it can carry no revoke button.
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
    // The tombstone offers no revoke affordance -- it is already revoked.
    expect(row.queryByRole('button', { name: /revoke/i })).not.toBeInTheDocument();
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
  });
});
