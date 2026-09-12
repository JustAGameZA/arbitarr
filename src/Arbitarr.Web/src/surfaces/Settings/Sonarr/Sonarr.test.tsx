import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { SonarrSection } from './Sonarr';
import { apiFetch } from '../../../api/client';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { mockApi } from '../../../test/mockApi';
import { renderSurface } from '../../../test/renderSurface';

const ROUTE = '/api/admin/arr/sonarr';
const TEST_ROUTE = '/api/admin/arr/sonarr/test';

/** RFC 5737 TEST-NET-1: non-routable, and no real address enters committed content. */
const CONFIGURED = { baseUrl: 'http://192.0.2.10:8989', hasApiKey: true };
const UNCONFIGURED = { baseUrl: null, hasApiKey: false };

/**
 * Distinctive enough that a substring search over a serialized request body
 * cannot match it by accident, which is what makes the "it is not in here"
 * assertions below meaningful.
 */
const TYPED_KEY = 'placeholder-sonarr-key-9d4c';

function probe(outcome: string, message: string) {
  return { success: outcome === 'Ok', outcome, message };
}

/**
 * Mounts the section against a client the test can then inspect.
 *
 * `renderSurface` builds its own client and does not hand it back, so the cache
 * assertions below mount their own — with the SAME defaults, and deliberately no
 * `gcTime` of its own, mirroring `src/api/queryClient.ts`. If the mutation's
 * `gcTime: 0` were removed, react-query's five-minute default would apply here
 * exactly as it does in the app, which is what the control below demonstrates.
 */
function renderWithClient() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(
    <QueryClientProvider client={client}>
      <SonarrSection />
    </QueryClientProvider>,
  );

  return client;
}

/**
 * Serialises everything the MutationCache is holding.
 *
 * `state.variables` is where a settled save keeps the typed key, so this is the
 * sweep an operator with the devtools open would be doing by hand. Whole mutation
 * objects rather than just `state`, so a copy parked on any other field would be
 * caught too.
 */
function sweepMutationCache(client: QueryClient): string {
  return JSON.stringify(client.getMutationCache().getAll());
}

/**
 * THE POSITIVE CONTROL for the cache assertions, and the reason they bite.
 *
 * The same PUT, against the same fetch double, through a mutation differing from
 * the shipped one in exactly one respect: no `gcTime: 0` and no reset. It
 * reproduces the retention `gcTime: 0` exists to remove, and proves the sweep
 * FINDS a key that is really there. Without it, `not.toContain` would pass just as
 * happily against a cache that never held a save at all — an empty set contains
 * nothing.
 *
 * Driven through a real mutation rather than a hand-planted cache entry, because
 * a planted object only proves `JSON.stringify` can see a string; this proves the
 * retention is a property of react-query's defaults, which is the claim the
 * shipped `gcTime: 0` answers.
 */
function LeakySaveHarness() {
  const mutation = useMutation({
    mutationFn: (request: { baseUrl: string; apiKey: string }) =>
      apiFetch<unknown>(ROUTE, { method: 'PUT', body: JSON.stringify(request) }),
  });

  return (
    <button
      type="button"
      onClick={() => mutation.mutate({ baseUrl: CONFIGURED.baseUrl, apiKey: TYPED_KEY })}
    >
      Leaky save
    </button>
  );
}

/** The body of the last PUT the page sent, parsed. */
function lastPutBody(api: ReturnType<typeof mockApi>): Record<string, unknown> {
  const puts = api.calls.filter((call) => call.method === 'PUT');
  expect(puts.length).toBeGreaterThan(0);
  return JSON.parse(puts[puts.length - 1]!.body!) as Record<string, unknown>;
}

describe('Sonarr section', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * The address is SHOWN, not hidden behind a "replace it" field — it is not a
   * credential, and the server rejects one carrying userinfo so that stays true.
   * The KEY field, directly below, is the opposite on both counts, and the next
   * test asserts that difference.
   */
  it('shows the stored base URL in an editable field', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    const field = await screen.findByLabelText('Base URL');
    expect(field).toHaveValue('http://192.0.2.10:8989');
    // Not a password field: masking a non-secret only stops the operator reading it.
    expect(field).toHaveAttribute('type', 'url');
  });

  /**
   * THE WRITE-ONLY IDIOM, ASSERTED ON THE FIELD ITSELF. The key field starts
   * empty even when a key is stored, because the client never had the value —
   * there is nothing to seed it from. It is masked, and its label says what
   * typing DOES rather than what is stored.
   */
  it('starts the key field empty and masked even when a key is stored', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    const field = await screen.findByLabelText('Replace API key');
    expect(field).toHaveValue('');
    expect(field).toHaveAttribute('type', 'password');
  });

  it('labels the key field as setting rather than replacing when none is stored', async () => {
    mockApi({ [ROUTE]: { body: UNCONFIGURED } });
    renderSurface(<SonarrSection />);

    expect(await screen.findByLabelText('Set API key')).toBeInTheDocument();
    expect(screen.queryByLabelText('Replace API key')).not.toBeInTheDocument();
  });

  it('reports whether a key is stored without ever showing one', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    expect(await screen.findByText('Key configured')).toBeInTheDocument();
  });

  it('says so when nothing is configured yet', async () => {
    mockApi({ [ROUTE]: { body: UNCONFIGURED } });
    renderSurface(<SonarrSection />);

    expect(await screen.findByText('No key')).toBeInTheDocument();
    expect(await screen.findByLabelText('Base URL')).toHaveValue('');
  });

  /**
   * THE CONTRACT THAT PROTECTS A WORKING CREDENTIAL. Leaving the key field blank
   * must send NO `apiKey` property at all — the server reads its absence as
   * "leave the stored key alone". Sending an empty string would be a submitted
   * blank and would be rejected, and sending the field's value unconditionally is
   * the bug this test exists to catch.
   */
  it('omits the key entirely when the field is left blank', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    const field = await screen.findByLabelText('Base URL');
    await userEvent.clear(field);
    await userEvent.type(field, 'http://192.0.2.20:8989');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(lastPutBody(api)).toEqual({ baseUrl: 'http://192.0.2.20:8989' }));
    // Stated as its own assertion, not merely implied by toEqual: this is the
    // property that keeps an address edit from blanking a working key.
    expect(lastPutBody(api)).not.toHaveProperty('apiKey');
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
  });

  it('sends the key when the operator typed one', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    const field = await screen.findByLabelText('Replace API key');
    await userEvent.type(field, TYPED_KEY);
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(lastPutBody(api)).toEqual({
        baseUrl: 'http://192.0.2.10:8989',
        apiKey: TYPED_KEY,
      }),
    );
  });

  /**
   * The typed key is dropped from the field once delivered, so it is not left
   * sitting in the DOM for the rest of the session.
   *
   * POSITIVE CONTROL: the field is first shown to HOLD the key, proving this
   * query reads the value that is actually there. Without that, "the field is
   * empty" would pass against a field that never received the text at all.
   */
  it('clears the typed key from the field after a successful save', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    const field = await screen.findByLabelText('Replace API key');
    await userEvent.type(field, TYPED_KEY);

    // POSITIVE CONTROL: the value really is in the field before the save.
    expect(field).toHaveValue(TYPED_KEY);

    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(lastPutBody(api)).toHaveProperty('apiKey', TYPED_KEY));

    // ...therefore this emptiness is a real clear.
    await waitFor(() => expect(screen.getByLabelText('Replace API key')).toHaveValue(''));
  });

  /**
   * The key must never come back from the server, so it must never appear in a
   * RESPONSE the page renders. This asserts the page does not put it on screen
   * even if a future server change started returning it.
   *
   * POSITIVE CONTROL: the base URL from the same response IS rendered, proving
   * this render actually shows values from the payload.
   */
  it('never renders a key that a response carried', async () => {
    mockApi({
      // A deliberately leaky payload: the real server cannot produce this, and
      // that is the point — the page must not surface it if one ever did.
      [ROUTE]: { body: { ...CONFIGURED, apiKey: TYPED_KEY } },
    });
    renderSurface(<SonarrSection />);

    // POSITIVE CONTROL: a value from this payload IS on screen.
    expect(await screen.findByLabelText('Base URL')).toHaveValue('http://192.0.2.10:8989');

    // ...therefore this absence is real.
    expect(screen.queryByDisplayValue(TYPED_KEY)).not.toBeInTheDocument();
    expect(document.body.textContent).not.toContain(TYPED_KEY);
  });

  /**
   * The operator reads the SERVER's exact words. There is no client-side
   * validation in front of the write, so a rejection cannot be one this page
   * invented — it either came back from the server or it is not shown.
   *
   * The route is swapped only AFTER the form has rendered, because `mockApi`
   * matches on path alone and not on method: replacing it up front would fail the
   * initial GET and leave no form to submit.
   */
  it('surfaces the server rejection verbatim rather than inventing one', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    // Rendered from a successful GET first...
    const save = await screen.findByRole('button', { name: 'Save' });

    // ...then the write is made to fail.
    api.set(ROUTE, {
      status: 400,
      body: { error: 'The Sonarr base URL must not contain credentials.' },
    });
    await userEvent.click(save);

    expect(await screen.findByText(/must not contain credentials/i)).toBeInTheDocument();
  });

  /**
   * NO CLEAR AFFORDANCE, ASSERTED (ADR 0010; bead arb-c26). A key-only clear does
   * not exist on the server — a secret is cleared only by deleting the thing that
   * owns it — so offering one here would promise an operation that cannot happen.
   * Pinned rather than merely omitted, so re-adding a clear button is a test
   * failure and a deliberate decision rather than an unnoticed convenience.
   *
   * POSITIVE CONTROL: the form is first shown to HAVE rendered its buttons, so the
   * absence below is about this button specifically rather than about a page that
   * rendered nothing at all.
   */
  it('offers no key-clearing affordance, because the server has none', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    // POSITIVE CONTROL: the form's own buttons ARE present.
    expect(await screen.findByRole('button', { name: 'Save' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Test connection' })).toBeInTheDocument();

    // ...therefore these absences are real.
    expect(screen.queryByRole('button', { name: 'Clear key' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /clear/i })).not.toBeInTheDocument();
  });

  /**
   * The page never issues a DELETE at all: the whole-instance unconfigure exists
   * on the server but is not surfaced yet (it wants the two-step confirmation the
   * Notifications section uses). Asserted so an accidental wiring shows up here.
   */
  it('issues no DELETE anywhere on this surface', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    await screen.findByRole('button', { name: 'Save' });
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(api.calls.filter((c) => c.method === 'PUT').length).toBe(1));

    expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(0);
  });

  /**
   * FIVE DISTINCT OUTCOMES, NOT ONE RED "FAILED". Each has a different fix, so a
   * single verdict would make the button decorative. `AuthenticationFailed` is
   * the member the AI section deliberately does NOT have — Sonarr carries a key
   * and a wrong key is the likeliest misconfiguration this button catches.
   */
  it.each([
    ['Ok', 'Connected'],
    ['Unreachable', 'Unreachable'],
    ['TlsFailure', 'TLS failure'],
    ['AuthenticationFailed', 'Key rejected'],
    ['UnexpectedResponse', 'Not Sonarr'],
  ])('renders a distinct badge for the %s outcome', async (outcome, label) => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe(outcome, `Server wording for ${outcome}.`) },
    });
    renderSurface(<SonarrSection />);

    await userEvent.click(await screen.findByRole('button', { name: 'Test connection' }));

    expect(await screen.findByText(label)).toBeInTheDocument();
    // The server's own wording is rendered as-is beside our short label.
    expect(await screen.findByText(`Server wording for ${outcome}.`)).toBeInTheDocument();
  });

  /**
   * A probe that fails to complete is NOT a probe outcome and must not be dressed
   * as one: a rejected admin key or a dead server says nothing about Sonarr.
   */
  it('shows a transport failure without an outcome badge', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { status: 503, body: { error: 'Service unavailable' } },
    });
    renderSurface(<SonarrSection />);

    await userEvent.click(await screen.findByRole('button', { name: 'Test connection' }));

    await screen.findByRole('alert');
    for (const label of ['Connected', 'Unreachable', 'TLS failure', 'Key rejected', 'Not Sonarr']) {
      expect(screen.queryByText(label)).not.toBeInTheDocument();
    }
  });

  it('sends no body on the probe, because the stored values are what is under test', async () => {
    const api = mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe('Ok', 'Connected successfully and the API key was accepted.') },
    });
    renderSurface(<SonarrSection />);

    await userEvent.click(await screen.findByRole('button', { name: 'Test connection' }));

    await waitFor(() => expect(api.callsTo(TEST_ROUTE).length).toBeGreaterThan(0));
    const probeCall = api.callsTo(TEST_ROUTE)[0]!;
    expect(probeCall.method).toBe('POST');
    expect(probeCall.body).toBeUndefined();
  });

  /**
   * The gate is by path prefix, never by verb, so the READ carries the admin key
   * exactly as the writes do.
   */
  it('attaches the admin key to the read as well as the writes', async () => {
    useAdminKeyStore.setState({ key: 'admin-key-under-test', serverKeyUnset: false });
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<SonarrSection />);

    await screen.findByLabelText('Base URL');
    expect(api.adminKeyOn(ROUTE)).toBe('admin-key-under-test');
  });

  /**
   * POSITIVE CONTROL for the test that follows: a mutation carrying this key with
   * react-query's DEFAULT gcTime and no reset leaves `variables` — the plaintext
   * key — sitting in the MutationCache after it settles, for five minutes.
   *
   * That retention is exactly what `gcTime: 0` on the shipped mutation removes,
   * and demonstrating it here is what makes the absence assertion below evidence
   * rather than a search that could never have matched.
   */
  it('retains the typed key in the mutation cache without gcTime and reset', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={client}>
        <LeakySaveHarness />
      </QueryClientProvider>,
    );

    // Nothing yet — so the retention below cannot be satisfied by a cache that
    // was already dirty before the save ran.
    expect(sweepMutationCache(client)).not.toContain(TYPED_KEY);

    await userEvent.click(screen.getByRole('button', { name: 'Leaky save' }));

    await waitFor(() => expect(sweepMutationCache(client)).toContain(TYPED_KEY));
  });

  /**
   * ...THEREFORE THIS ABSENCE IS REAL. The shipped mutation sets `gcTime: 0` and
   * resets on settle, so the same save through the real form leaves no copy of the
   * key behind — component state alone would not be enough, because the cache is a
   * second copy. Removing either half of that pairing fails here.
   */
  it('leaves no copy of the typed key in the mutation cache after a save', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    const client = renderWithClient();

    const field = await screen.findByLabelText('Replace API key');
    await userEvent.type(field, TYPED_KEY);
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    // The save really was made and really did carry the key, so the emptiness
    // below is an eviction rather than a mutation that never ran.
    await waitFor(() => expect(lastPutBody(api)).toHaveProperty('apiKey', TYPED_KEY));

    await waitFor(() => expect(sweepMutationCache(client)).not.toContain(TYPED_KEY));
  });
});
