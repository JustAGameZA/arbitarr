import { render, renderHook, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SourcesSection } from './Sources';
import { ADMIN_KEY_HEADER } from '../../../api/client';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { mockApi } from '../../../test/mockApi';
import { renderSurface } from '../../../test/renderSurface';

const SOURCES = '/api/admin/sources';

// RFC 5737 TEST-NET-1 throughout: no real address is committed.
const sources = [
  {
    id: 1,
    kind: 'NzbHydra',
    displayName: 'Primary hydra',
    baseUrl: 'http://192.0.2.10:5076',
    enabled: true,
    hasApiKey: true,
    createdAt: '2026-09-01T10:00:00Z',
    updatedAt: '2026-09-02T11:00:00Z',
  },
  {
    id: 2,
    kind: 'NzbHydra',
    displayName: 'Spare hydra',
    baseUrl: 'http://192.0.2.20:5076',
    enabled: false,
    hasApiKey: false,
    createdAt: '2026-09-03T10:00:00Z',
    updatedAt: '2026-09-04T11:00:00Z',
  },
];

/**
 * The row for one source, so assertions cannot accidentally match the other.
 *
 * Async because the list arrives from a query: a synchronous `getByRole` here
 * runs before the fetch resolves and reports "no rows" rather than waiting for
 * them.
 */
const rowFor = (name: string) => screen.findByRole('row', { name: new RegExp(name) });

/**
 * A longer per-test budget than vitest's 5s default.
 *
 * Not masking a hang: this file drives the largest form on the surface, and the
 * config sets `css: true` (load-bearing for the chrome gate), so every render
 * here parses the real stylesheet. The passing tests already sit at 3-4s on a
 * slow machine, which leaves the 5s default with no headroom and turns an
 * ordinary render into a spurious failure. The assertions are unchanged; only
 * the patience is.
 */
const TEST_TIMEOUT_MS = 20_000;

describe('Sources section', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists every source with its kind, address, enabled state and key indicator', async () => {
    mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    expect(await screen.findByRole('heading', { name: 'Sources' })).toBeInTheDocument();

    const primary = await rowFor('Primary hydra');
    expect(within(primary).getByText('NzbHydra')).toBeInTheDocument();
    expect(within(primary).getByText('http://192.0.2.10:5076')).toBeInTheDocument();
    expect(within(primary).getByText('Enabled')).toBeInTheDocument();
    expect(within(primary).getByText('Configured')).toBeInTheDocument();

    // The disabled, keyless one reads differently on BOTH flags -- asserted per
    // row rather than "somewhere on the page", because a projection that wrote
    // one value to every row would satisfy the looser form.
    const spare = await rowFor('Spare hydra');
    expect(within(spare).getByText('Disabled')).toBeInTheDocument();
    expect(within(spare).getByText('Not configured')).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  it('names what would fill the list when there are no sources', async () => {
    mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    expect(
      await screen.findByText(
        /No sources configured — add an NZBHydra2 base URL and API key below/,
      ),
    ).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  it('sends the typed key on create, and omits the field entirely when none was typed', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Added hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source API key'), 'placeholder-new-key');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const post = api.calls.find((call) => call.method === 'POST');
    expect(JSON.parse(post!.body!)).toEqual({
      kind: 'NzbHydra',
      displayName: 'Added hydra',
      baseUrl: 'http://192.0.2.30:5076',
      enabled: true,
      apiKey: 'placeholder-new-key',
    });
  }, TEST_TIMEOUT_MS);

  it('omits apiKey from a create body when the operator typed no key', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Keyless hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
    expect(body).not.toHaveProperty('apiKey');
  }, TEST_TIMEOUT_MS);

  /**
   * THE EDIT CONTRACT, which has three different null policies in one signature.
   *
   * `apiKey` omitted means "leave the stored key alone", so an unrelated edit
   * must not carry the field at all -- sending an empty string would blank a
   * working credential. But `kind`, `displayName` and `baseUrl` omitted become
   * `string.Empty` server-side and are then REJECTED, so those three must be
   * present on every edit whether or not they changed. Both halves are asserted
   * here because the `apiKey` rule actively invites the wrong generalisation to
   * the other three.
   */
  it('sends kind, displayName and baseUrl on an edit but omits an untouched apiKey', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));

    // Change only the address. The key field is left untouched.
    const url = await screen.findByLabelText('Edit source base URL');
    await user.clear(url);
    await user.type(url, 'http://192.0.2.99:5076');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const put = api.calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe(`${SOURCES}/1`);

    const body = JSON.parse(put!.body!);
    // The three whose omission is a validation failure are all present and
    // carry their unchanged values, not just the edited one.
    expect(body.kind).toBe('NzbHydra');
    expect(body.displayName).toBe('Primary hydra');
    expect(body.baseUrl).toBe('http://192.0.2.99:5076');
    expect(body.enabled).toBe(true);

    // And the one whose omission means "leave it alone" is absent -- not
    // present-and-empty, which would clear the stored key.
    expect(body).not.toHaveProperty('apiKey');
  }, TEST_TIMEOUT_MS);

  it('sends apiKey on an edit when the operator typed a replacement', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));
    await user.type(
      await screen.findByLabelText('Edit source API key'),
      'placeholder-rotated-key',
    );
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);
    expect(body.apiKey).toBe('placeholder-rotated-key');
  }, TEST_TIMEOUT_MS);

  it('frames the edit key field as replacing the stored key, and shows no value for it', async () => {
    const user = userEvent.setup({ delay: null });
    mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));

    expect(
      await screen.findByText(/An API key is stored for this source. It cannot be shown./),
    ).toBeInTheDocument();

    // No stored value is populated: SourceResponse has no field that could
    // carry one, so a masked or placeholder value here would be invented.
    const field = await screen.findByLabelText('Edit source API key');
    expect(field).toHaveValue('');
    expect(field).toHaveAttribute('type', 'password');
  }, TEST_TIMEOUT_MS);

  it('toggles enabled from the row with the full body and no apiKey', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Spare hydra')).getByRole('button', { name: 'Enable' }));

    const put = api.calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe(`${SOURCES}/2`);
    const body = JSON.parse(put!.body!);
    expect(body.enabled).toBe(true);
    expect(body.displayName).toBe('Spare hydra');
    expect(body.baseUrl).toBe('http://192.0.2.20:5076');
    expect(body).not.toHaveProperty('apiKey');
  }, TEST_TIMEOUT_MS);

  it('requires a confirmation before removing a source', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Remove' }));

    // Nothing has been sent yet: the first click only asks.
    expect(api.calls.some((call) => call.method === 'DELETE')).toBe(false);
    expect(
      screen.getByText(/Removing this source also deletes its stored API key/),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));
    expect(api.calls.find((call) => call.method === 'DELETE')?.path).toBe(`${SOURCES}/1`);
  }, TEST_TIMEOUT_MS);

  it('abandons the removal when the operator keeps the source', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Remove' }));
    await user.click(screen.getByRole('button', { name: 'Keep' }));

    expect(api.calls.some((call) => call.method === 'DELETE')).toBe(false);
  }, TEST_TIMEOUT_MS);

  /**
   * §3.3/AC4: the five outcomes render five DISTINCT messages.
   *
   * Driven as a table and then asserted for mutual distinctness at the end,
   * because the failure this guards against is not "one outcome renders
   * nothing" -- it is "all five collapse to the same red 'failed'", which every
   * per-outcome assertion in isolation would happily pass.
   */
  /**
   * `message` is the EXACT literal `DescribeOutcome` returns for that outcome
   * (AdminSourceEndpoints.cs:252-263), copied rather than paraphrased. A
   * shortened stand-in still passes every assertion here while proving nothing
   * about what the operator actually reads, and it hides the case these tests
   * exist to catch: server wording that drifts, or is truncated on the way to
   * the screen. Update these only by copying from the C# again.
   */
  const outcomes = [
    {
      outcome: 'Ok',
      success: true,
      label: 'Connected',
      message: 'Connected successfully and the API key was accepted.',
    },
    {
      outcome: 'Unreachable',
      success: false,
      label: 'Unreachable',
      message:
        'Could not reach the source: no response from that address before the timeout. Check the base URL, the port, and that the service is running.',
    },
    {
      outcome: 'TlsFailure',
      success: false,
      label: 'TLS failure',
      message:
        'Reached the source but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if the service is not serving TLS on that port.',
    },
    {
      outcome: 'AuthenticationFailed',
      success: false,
      label: 'API key rejected',
      message:
        'The source is reachable but rejected the API key. Check the key and that it has permission on this source.',
    },
    {
      outcome: 'UnexpectedResponse',
      success: false,
      label: 'Unexpected response',
      message:
        'The source answered but not with the API expected. The base URL is probably pointing at a different service, a login page, or a reverse proxy rather than the source itself.',
    },
  ];

  it.each(outcomes)(
    'reports the $outcome probe outcome as "$label"',
    async ({ outcome, success, label, message }) => {
      const user = userEvent.setup({ delay: null });
      mockApi({
        [SOURCES]: { body: sources },
        [`${SOURCES}/1/test`]: { body: { success, outcome, message } },
      });
      renderSurface(<SourcesSection />);

      await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Test' }));

      const result = await screen.findByRole('status');
      expect(within(result).getByText(label)).toBeInTheDocument();
      // The server's own wording is shown too: it says what to check next,
      // which is the whole reason the outcomes are distinguished at all.
      expect(within(result).getByText(message)).toBeInTheDocument();
    },
    TEST_TIMEOUT_MS,
  );

  it('gives the five outcomes five different labels', () => {
    const labels = new Set(outcomes.map((entry) => entry.label));
    expect(labels.size).toBe(5);
  }, TEST_TIMEOUT_MS);

  /**
   * AC2, swept across EVERY surface the key could reach, in one pass.
   *
   * The surfaces are DOM, localStorage, sessionStorage, request URLs, and the
   * React Query MutationCache. They are enumerated together deliberately: the
   * earlier version of this file checked storage and URLs only, and the
   * mutation cache — which holds each settled mutation's `variables`, i.e. the
   * request body with the plaintext key, for a default 5 minutes — was a real
   * leak that neither of those sweeps could see. Checking "some" surfaces is
   * how that was missed; the list lives in one place now so adding a surface
   * means adding it here rather than writing a fourth isolated test.
   *
   * EVERY SURFACE CARRIES A PLANTED POSITIVE CONTROL, and that is the whole
   * point of the shape. An absence assertion passes just as happily when the
   * secret was never in play — an empty set contains nothing — so each sweep
   * first proves it WOULD see a key that was really there, and only then
   * asserts the real one is absent. Without the controls this test would keep
   * passing if the field stopped being filled in, if the dump read the wrong
   * storage, or if getMutationCache() started returning an empty array.
   *
   * The mutation-cache control goes one step further and REPRODUCES the leak
   * with a real hook left on react-query's default gcTime, rather than planting
   * a literal in the cache. A planted literal proves only that the dump can
   * find a string; reproducing the retention proves the library still behaves
   * the way `gcTime: 0` in queries.ts exists to counter, so this test fails
   * loudly if that assumption ever stops holding.
   */
  it('leaks the typed API key to no reachable surface', async () => {
    const KEY = 'placeholder-secret-under-test';

    // A client of our own, because renderSurface makes its own internally and
    // does not hand it back — and the mutation cache is only reachable through
    // the client that owns it.
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });

    const mutationDump = () =>
      JSON.stringify(
        client
          .getMutationCache()
          .getAll()
          .map((m) => [m.state.variables, m.state.data]),
      );
    const storageDump = () =>
      JSON.stringify([
        Object.entries({ ...localStorage }),
        Object.entries({ ...sessionStorage }),
      ]);
    const domDump = () => document.body.innerHTML;
    // A predicate rather than a dump, because a request URL is per-call: this
    // is the same check the final assertion runs, so the control below proves
    // exactly the thing that is later asserted.
    const urlsCarry = (calls: { url: URL }[]) =>
      calls.some((call) => call.url.toString().includes(KEY));

    // --- positive controls: each sweep demonstrably detects a planted key ----
    localStorage.setItem('planted', KEY);
    expect(storageDump()).toContain(KEY);
    localStorage.removeItem('planted');

    sessionStorage.setItem('planted', KEY);
    expect(storageDump()).toContain(KEY);
    sessionStorage.removeItem('planted');
    expect(storageDump()).not.toContain(KEY);

    const planted = document.createElement('div');
    planted.textContent = KEY;
    document.body.appendChild(planted);
    expect(domDump()).toContain(KEY);
    planted.remove();
    expect(domDump()).not.toContain(KEY);

    // The URL sweep needs its control too, and it is the easiest one to leave
    // out because `api.calls` starts empty — `every()` over an empty array is
    // vacuously true, so the real assertion below would pass before a single
    // request had been made. Proving the predicate against a URL that really
    // does carry the key is what makes it bite.
    expect(urlsCarry([{ url: new URL(`http://localhost/x?k=${KEY}`) }])).toBe(true);
    expect(urlsCarry([{ url: new URL('http://localhost/x') }])).toBe(false);

    // The mutation-cache control REPRODUCES THE REAL RETENTION rather than
    // planting a literal. A hand-built cache entry would prove only that
    // JSON.stringify can find a string someone put there; it would not prove
    // that react-query retains a settled mutation's `variables` at all, so it
    // would still pass if that behaviour changed and the sweep became
    // pointless. This drives an actual mutation through an actual hook with NO
    // gcTime override — i.e. the library's 5-minute default, exactly what
    // queries.ts opts out of — and shows the key sitting in the cache after the
    // mutation has settled. That is the leak; the `gcTime: 0` in queries.ts is
    // what closes it.
    const retained = renderHook(() => useMutation({ mutationFn: async (v: unknown) => v }), {
      wrapper: ({ children }) => (
        <QueryClientProvider client={client}>{children}</QueryClientProvider>
      ),
    });
    retained.result.current.mutate({ apiKey: KEY });
    await waitFor(() => expect(retained.result.current.isSuccess).toBe(true));
    expect(mutationDump()).toContain(KEY);
    retained.unmount();
    client.getMutationCache().clear();
    expect(mutationDump()).not.toContain(KEY);

    // --- the real assertions ------------------------------------------------
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <SourcesSection />
        </MemoryRouter>
      </QueryClientProvider>,
    );

    // A successful create...
    await user.type(await screen.findByLabelText('New source display name'), 'Added hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Add source' }));
    await waitFor(() => expect(api.calls.some((c) => c.method === 'POST')).toBe(true));

    // ...and a successful edit that replaces the stored key.
    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));
    await user.type(await screen.findByLabelText('Edit source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    await waitFor(() => expect(api.calls.some((c) => c.method === 'PUT')).toBe(true));

    // The key DID reach the wire. Without this the sweeps below would be
    // vacuous for a second reason: a key never sent was never at risk.
    expect(api.calls.some((call) => call.body?.includes(KEY) === true)).toBe(true);

    // A FAILING edit too: a rejected write cached the key exactly as a
    // successful one did, so the failure path must be swept, not assumed.
    api.set(SOURCES, { status: 400, body: { error: 'Base URL must be an absolute http or https URL.' } });
    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));
    await user.type(await screen.findByLabelText('Edit source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Save changes' }));
    expect(
      await screen.findByText('Base URL must be an absolute http or https URL.'),
    ).toBeInTheDocument();

    await waitFor(() => expect(mutationDump()).not.toContain(KEY));
    expect(storageDump()).not.toContain(KEY);
    expect(domDump()).not.toContain(KEY);
    expect(urlsCarry(api.calls)).toBe(false);
  }, TEST_TIMEOUT_MS);

  it('attaches the admin key to its read and its writes', async () => {
    const user = userEvent.setup({ delay: null });
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Test' }));

    // Gated by PATH PREFIX, never by verb: the GET carries the key exactly as
    // the POST does.
    expect(api.calls.find((call) => call.method === 'GET')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
    expect(api.calls.find((call) => call.method === 'POST')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
  }, TEST_TIMEOUT_MS);

  it("shows the server's own rejection when a write fails", async () => {
    const user = userEvent.setup({ delay: null });
    // The list loads, then the POST to the same path is switched to a 400 --
    // the mock matches by longest prefix, so one entry serves both verbs and
    // `set` swaps the reply between them.
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Bad hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'not-a-url');

    api.set(SOURCES, {
      status: 400,
      body: { error: 'Base URL must be an absolute http or https URL.' },
    });
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    // The server's exact words, not a message invented here -- the same posture
    // the Rules and Settings surfaces take.
    expect(
      await screen.findByText('Base URL must be an absolute http or https URL.'),
    ).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);
});
