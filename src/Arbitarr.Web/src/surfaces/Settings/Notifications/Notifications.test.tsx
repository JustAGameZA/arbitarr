import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { NotificationsSection } from './Notifications';
import { ADMIN_KEY_HEADER } from '../../../api/client';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../../QueryState';
import { mockApi } from '../../../test/mockApi';
import { renderSurface } from '../../../test/renderSurface';

const ROUTE = '/api/admin/notifications';

/**
 * The value the leak assertions hunt for, shaped the way a real provider shapes
 * one — token in the PATH, which is how Discord, Telegram, Gotify and Notifiarr
 * all do it, and why for a webhook the URL IS the credential.
 *
 * Distinctive on purpose: a substring search for it across the whole document
 * or the whole of storage cannot collide with anything the page legitimately
 * renders, so a hit is unambiguously the secret escaping. `example.com` and the
 * `placeholder-` prefix keep a fake endpoint out of committed content.
 */
const SECRET_URL = 'https://example.com/hooks/placeholder-super-secret-webhook-token';

/** The token half, searched separately so a leak of only the path still fails. */
const SECRET_TOKEN = 'placeholder-super-secret-webhook-token';

const unconfigured = {
  enabled: false,
  hasWebhookUrl: false,
  consecutiveFailureThreshold: 3,
  suppressionRateThreshold: 0.5,
  suppressionRateWindow: '01:00:00',
  enabledTriggers: ['SourceFailing', 'SourceRecovered'],
  lastDeliveryOutcome: null,
  lastDeliveryAt: null,
};

const configured = {
  ...unconfigured,
  enabled: true,
  hasWebhookUrl: true,
  lastDeliveryOutcome: 'Delivered',
  lastDeliveryAt: '2026-09-07T10:15:00+00:00',
};

/** The body of the last PUT the page sent, parsed. */
function lastPutBody(api: ReturnType<typeof mockApi>): Record<string, unknown> {
  const puts = api.calls.filter((call) => call.method === 'PUT');
  expect(puts.length).toBeGreaterThan(0);
  return JSON.parse(puts[puts.length - 1].body!) as Record<string, unknown>;
}

describe('Notifications section', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('reports an unconfigured target and names what would fill the empty state', async () => {
    mockApi({ [ROUTE]: { body: unconfigured } });
    renderSurface(<NotificationsSection />);

    expect(await screen.findByText('Not configured')).toBeInTheDocument();
    expect(screen.getByText(/No target is stored/)).toBeInTheDocument();

    // #52: the empty state says what fills it rather than merely being blank.
    expect(
      screen.getByText(/No notification has been attempted yet.*fills this in/s),
    ).toBeInTheDocument();

    // Nothing to clear when nothing is stored.
    expect(screen.queryByRole('button', { name: 'Clear webhook' })).toBeNull();
  });

  it('reports a configured target as a bool without ever showing a value', async () => {
    mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    expect(await screen.findByText('Configured')).toBeInTheDocument();

    // The field is for REPLACING, not for showing: the server serves no value,
    // so it starts empty and says so in its own label.
    const field = screen.getByLabelText('Replace the webhook URL');
    expect(field).toHaveValue('');
    expect(field).toHaveAttribute('type', 'password');
  });

  it('omits webhookUrl from a save the operator did not type a URL into', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    const threshold = await screen.findByLabelText(
      'Consecutive failures before a source is reported',
    );
    await user.clear(threshold);
    await user.type(threshold, '7');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    const body = lastPutBody(api);

    // OMITTED, not sent as "". Omission is the server's "leave the stored URL
    // alone"; the client never held the value and so cannot reapply one, and an
    // empty string would depend on the server normalizing it back.
    expect('webhookUrl' in body).toBe(false);

    // NON-VACUOUS: the edit really was sent, so the absence above is a property
    // of a real request rather than of a request that never went out.
    expect(body.consecutiveFailureThreshold).toBe(7);
  });

  it('includes webhookUrl exactly when the operator typed one', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    const field = await screen.findByLabelText('Replace the webhook URL');
    await user.type(field, SECRET_URL);
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(lastPutBody(api).webhookUrl).toBe(SECRET_URL);
  });

  it('clears the field on submit so a failed save leaves no URL in the DOM', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    const field = await screen.findByLabelText('Replace the webhook URL');
    await user.type(field, SECRET_URL);

    // POSITIVE CONTROL: while it is typed the value IS findable by the very
    // searches used below. Without this half, "the URL is not in the DOM" would
    // pass just as happily against a page that never received it — which is the
    // vacuous shape CLAUDE.md §4 names.
    expect(field).toHaveValue(SECRET_URL);
    expect(document.body.innerHTML).toContain(SECRET_TOKEN);

    // Now make the save FAIL. A rejected save is the state in which a retained
    // secret would sit on screen longest, so it is the one worth pinning.
    api.set(ROUTE, { status: 400, body: { error: 'Consecutive failure threshold must be at least 2.' } });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Consecutive failure threshold must be at least 2.')).toBeInTheDocument();

    // The server's rejection is shown, and the secret is gone from both the
    // field and the whole rendered document.
    expect(screen.getByLabelText('Replace the webhook URL')).toHaveValue('');
    expect(document.body.innerHTML).not.toContain(SECRET_URL);
    expect(document.body.innerHTML).not.toContain(SECRET_TOKEN);
  });

  it('never writes the URL to localStorage or sessionStorage', async () => {
    const user = userEvent.setup();
    mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    const field = await screen.findByLabelText('Replace the webhook URL');
    await user.type(field, SECRET_URL);
    await user.click(screen.getByRole('button', { name: 'Save' }));

    const dump = () =>
      [localStorage, sessionStorage]
        .flatMap((store) =>
          Object.keys(store).map((key) => `${key}=${store.getItem(key) ?? ''}`),
        )
        .join('\n');

    // POSITIVE CONTROL: plant the secret where the sweep looks and prove the
    // sweep would find it. An "absence" assertion over storage passes trivially
    // when storage is empty, which it is here — so without this the next two
    // assertions would prove nothing at all.
    localStorage.setItem('leak-probe', SECRET_URL);
    expect(dump()).toContain(SECRET_URL);
    localStorage.removeItem('leak-probe');

    // The real assertion, now that it is known to be capable of failing.
    expect(dump()).not.toContain(SECRET_URL);
    expect(dump()).not.toContain(SECRET_TOKEN);
  });

  /**
   * Renders the section against a client the test can inspect.
   *
   * `renderSurface` builds its own QueryClient and does not hand it back, and it
   * is shared by every surface test, so it is not widened just for this file.
   * The options mirror it exactly (retries off both sides) so this is the same
   * environment, only observable.
   */
  function renderWithClient() {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <NotificationsSection />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    return client;
  }

  /**
   * THE LAST PLACE THE CLIENT CAN HOLD THE SECRET.
   *
   * Clearing the input and dropping it from React state is not enough on its
   * own: react-query keeps every mutation's `variables` on the MutationCache
   * after it settles, for `gcTime` (5 minutes by default, and
   * `api/queryClient.ts` sets none for mutations). The webhook URL is a
   * mutation variable, so without an explicit eviction it stays readable from
   * the devtools or the console long after the field looks empty — and longest
   * of all on the FAILED path, where nothing prompts a remount.
   *
   * Asserted over the serialised cache rather than a single field, so a future
   * react-query that stores the variables somewhere else on the entry still
   * fails this.
   */
  it.each([
    ['a successful save', 200, { ...configured, hasWebhookUrl: true }],
    ['a failed save', 400, { error: 'Consecutive failure threshold must be at least 2.' }],
  ])('leaves no webhook URL in the mutation cache after %s', async (_label, status, body) => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    const client = renderWithClient();

    const field = await screen.findByLabelText('Replace the webhook URL');
    await user.type(field, SECRET_URL);

    const dumpMutationCache = () => JSON.stringify(client.getMutationCache().getAll());

    // POSITIVE CONTROL: run a REAL mutation carrying the URL as its variables —
    // exactly the shape production produces — and prove this sweep finds it
    // while it is retained. Without this half, "the cache does not contain the
    // URL" would pass just as happily against a cache that never held anything,
    // the vacuous shape CLAUDE.md §4 names. Built with default (unset) gcTime
    // rather than the section's own options, so it demonstrates what react-query
    // retains BY DEFAULT — which is precisely the leak being closed.
    const planted = client.getMutationCache().build(client, {
      mutationFn: async (variables: unknown) => variables,
    });
    await planted.execute({ webhookUrl: SECRET_URL });
    expect(dumpMutationCache()).toContain(SECRET_URL);
    client.getMutationCache().remove(planted);
    expect(dumpMutationCache()).not.toContain(SECRET_URL);

    api.set(ROUTE, { status, body });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // NON-VACUOUS: the URL really was sent, so there was something for the
    // cache to retain.
    await waitFor(() => expect(lastPutBody(api).webhookUrl).toBe(SECRET_URL));

    // The real assertion, now known to be capable of failing.
    await waitFor(() => {
      expect(dumpMutationCache()).not.toContain(SECRET_URL);
      expect(dumpMutationCache()).not.toContain(SECRET_TOKEN);
    });

    // And still not in the DOM, on either path.
    expect(document.body.innerHTML).not.toContain(SECRET_URL);
    expect(document.body.innerHTML).not.toContain(SECRET_TOKEN);
  });

  it('clears the webhook only after an explicit confirmation', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('button', { name: 'Clear webhook' }));

    // Nothing has been sent yet: the consequence is stated and has to be agreed.
    expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(0);
    expect(screen.getByText(/Clear the stored webhook\?/)).toBeInTheDocument();

    // Declining sends nothing either.
    await user.click(screen.getByRole('button', { name: 'Keep it' }));
    expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Clear webhook' }));
    await user.click(screen.getByRole('button', { name: 'Clear it' }));

    const deletes = api.calls.filter((call) => call.method === 'DELETE');
    expect(deletes).toHaveLength(1);

    // The dedicated route, not a PUT of an empty string: clearing a secret the
    // operator cannot see must be something they asked for by name.
    expect(deletes[0].path).toBe('/api/admin/notifications/webhook');
  });

  it.each([
    ['Delivered', true, /accepted the test notification/i],
    ['Unreachable', false, /no response from that address/i],
    ['TlsFailure', false, /TLS handshake failed/i],
    ['Rejected', false, /refused the notification/i],
    ['NotConfigured', false, /nothing to send a test to/i],
  ])('renders %s as its own distinct outcome', async (outcome, success, wording) => {
    const user = userEvent.setup();
    const messages: Record<string, string> = {
      Delivered: 'The webhook accepted the test notification.',
      Unreachable:
        'Could not reach the webhook: no response from that address before the timeout. Check the host and port in the URL, and that the service is running.',
      TlsFailure:
        'Reached the webhook but the TLS handshake failed. Check the certificate (expired, self-signed, or issued for a different hostname) or use http if the service is not serving TLS on that port.',
      Rejected:
        'The webhook is reachable but refused the notification. The token in the URL may have been revoked, or the endpoint may not accept this payload.',
      NotConfigured:
        'No webhook URL is configured, so there was nothing to send a test to. Set one and try again.',
    };

    mockApi({
      [ROUTE]: { body: configured },
      [`${ROUTE}/test`]: { body: { success, outcome, message: messages[outcome] } },
    });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('button', { name: 'Send a test notification' }));

    // Each outcome says a different thing, because each is a different fix —
    // never one red "failed".
    expect(await screen.findByText(wording)).toBeInTheDocument();

    // And on failure the operator is told the KIND without the target ever
    // being named. The server cannot send one; this asserts nothing here
    // reintroduces it.
    expect(document.body.innerHTML).not.toContain(SECRET_TOKEN);
  });

  it('does not fabricate a success when the server reports a failed delivery', async () => {
    const user = userEvent.setup();
    mockApi({
      [ROUTE]: { body: configured },
      [`${ROUTE}/test`]: {
        // A 200 carrying success:false — the request completed, the delivery did
        // not. Reading the HTTP status as the answer is exactly the mistake the
        // closed outcome enum exists to prevent.
        body: {
          success: false,
          outcome: 'Unreachable',
          message: 'Could not reach the webhook: no response from that address before the timeout.',
        },
      },
    });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('button', { name: 'Send a test notification' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/Could not reach the webhook/);
  });

  it('shows a transport failure as an error rather than as a delivery outcome', async () => {
    const user = userEvent.setup();
    mockApi({
      [ROUTE]: { body: configured },
      [`${ROUTE}/test`]: { status: 500, body: { error: 'The notifier is unavailable.' } },
    });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('button', { name: 'Send a test notification' }));

    // The request itself failed; that is not a delivery outcome and must not be
    // dressed as one.
    expect(await screen.findByText('The notifier is unavailable.')).toBeInTheDocument();
    expect(screen.queryByText(/accepted the test notification/i)).toBeNull();
  });

  it('offers every shipped trigger, including the ones currently switched off', async () => {
    mockApi({ [ROUTE]: { body: unconfigured } });
    renderSurface(<NotificationsSection />);

    // The server lists only the ENABLED triggers. Deriving the checkbox list
    // from that response would make a disabled trigger vanish with no way back.
    expect(await screen.findByRole('checkbox', { name: /Source started failing/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Source recovered/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Suppression rate high/ })).not.toBeChecked();
    expect(
      screen.getByRole('checkbox', { name: /Suppression rate back to normal/ }),
    ).not.toBeChecked();
  });

  it('sends the trigger set the operator selected', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: unconfigured } });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('checkbox', { name: /Suppression rate high/ }));
    await user.click(screen.getByRole('checkbox', { name: /Source recovered/ }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(lastPutBody(api).enabledTriggers).toEqual(['SourceFailing', 'SuppressionRateHigh']);
  });

  it('sends threshold values as typed, leaving the bounds to the server', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [ROUTE]: { body: configured } });
    renderSurface(<NotificationsSection />);

    const threshold = await screen.findByLabelText(
      'Consecutive failures before a source is reported',
    );
    await user.clear(threshold);
    await user.type(threshold, '1');

    const rejection = 'Consecutive failure threshold must be at least 2; got 1.';
    api.set(ROUTE, { status: 400, body: { error: rejection } });
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Out of bounds and sent anyway: there is no client-side guard to block a
    // value the server would have accepted, or to invent a rejection it never
    // issued.
    expect(lastPutBody(api).consecutiveFailureThreshold).toBe(1);

    // The server's exact words, not a paraphrase.
    expect(await screen.findByText(rejection)).toBeInTheDocument();
  });

  it('renders the last delivery outcome and when it happened', async () => {
    mockApi({ [ROUTE]: { body: { ...configured, lastDeliveryOutcome: 'TlsFailure' } } });
    renderSurface(<NotificationsSection />);

    expect(await screen.findByText('TLS handshake failed')).toBeInTheDocument();
  });

  it('attaches the admin key to every call it makes', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({
      [ROUTE]: { body: configured },
      [`${ROUTE}/test`]: { body: { success: true, outcome: 'Delivered', message: 'ok' } },
    });
    renderSurface(<NotificationsSection />);

    await user.click(await screen.findByRole('button', { name: 'Send a test notification' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // Gated by PATH PREFIX, so the GET carries it exactly as the writes do.
    for (const call of api.callsTo('/api/admin/notifications')) {
      expect(call.headers[ADMIN_KEY_HEADER]).toBe('operator-key');
    }
    expect(api.calls.some((call) => call.method === 'GET')).toBe(true);
    expect(api.calls.some((call) => call.method === 'PUT')).toBe(true);
    expect(api.calls.some((call) => call.method === 'POST')).toBe(true);

    // Never on the URL — the key belongs in the header, out of access logs.
    for (const call of api.calls) {
      expect(call.url.search).toBe('');
    }
  });

  it('keeps the stored key and explains the gap on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({ [ROUTE]: { status: 503, body: { error: 'admin key not configured' } } });
    renderSurface(<NotificationsSection />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
  });

  it('renders inside the Settings surface above the catalog groups', async () => {
    const { default: SettingsPage } = await import('../Settings');
    mockApi({
      '/api/admin/settings': {
        body: [
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
        ],
      },
      [ROUTE]: { body: configured },
    });
    renderSurface(<SettingsPage />);

    // Awaited, not merely found: the heading renders while the query is still
    // pending, so asserting on the panel's contents synchronously would only
    // ever see "Loading…".
    const indicator = await screen.findByText('Configured');
    const heading = screen.getByRole('heading', { name: 'Notifications' });
    const panel = heading.closest('section');

    expect(panel).not.toBeNull();
    expect(within(panel!).getByText('Configured')).toBe(indicator);

    // ABOVE the catalog groups: the section the operator configures notifications
    // in precedes the tunable-values panels rather than being buried under them.
    const catalogHeading = await screen.findByRole('heading', { name: 'Caching' });
    expect(panel!.compareDocumentPosition(catalogHeading.closest('section')!)).toBe(
      Node.DOCUMENT_POSITION_FOLLOWING,
    );
  });
});
