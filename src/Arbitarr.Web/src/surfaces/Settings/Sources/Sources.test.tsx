import { screen, within } from '@testing-library/react';
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
  });

  it('names what would fill the list when there are no sources', async () => {
    mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    expect(
      await screen.findByText(
        /No sources configured — add an NZBHydra2 base URL and API key below/,
      ),
    ).toBeInTheDocument();
  });

  it('sends the typed key on create, and omits the field entirely when none was typed', async () => {
    const user = userEvent.setup();
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
  });

  it('omits apiKey from a create body when the operator typed no key', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Keyless hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
    expect(body).not.toHaveProperty('apiKey');
  });

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
    const user = userEvent.setup();
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
  });

  it('sends apiKey on an edit when the operator typed a replacement', async () => {
    const user = userEvent.setup();
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
  });

  it('frames the edit key field as replacing the stored key, and shows no value for it', async () => {
    const user = userEvent.setup();
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
  });

  it('toggles enabled from the row with the full body and no apiKey', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(await within(await rowFor('Spare hydra')).findByRole('button', { name: 'Enable' }));

    const put = api.calls.find((call) => call.method === 'PUT');
    expect(put?.path).toBe(`${SOURCES}/2`);
    const body = JSON.parse(put!.body!);
    expect(body.enabled).toBe(true);
    expect(body.displayName).toBe('Spare hydra');
    expect(body.baseUrl).toBe('http://192.0.2.20:5076');
    expect(body).not.toHaveProperty('apiKey');
  });

  it('requires a confirmation before removing a source', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(await within(await rowFor('Primary hydra')).findByRole('button', { name: 'Remove' }));

    // Nothing has been sent yet: the first click only asks.
    expect(api.calls.some((call) => call.method === 'DELETE')).toBe(false);
    expect(
      screen.getByText(/Removing this source also deletes its stored API key/),
    ).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Confirm remove' }));
    expect(api.calls.find((call) => call.method === 'DELETE')?.path).toBe(`${SOURCES}/1`);
  });

  it('abandons the removal when the operator keeps the source', async () => {
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(await within(await rowFor('Primary hydra')).findByRole('button', { name: 'Remove' }));
    await user.click(screen.getByRole('button', { name: 'Keep' }));

    expect(api.calls.some((call) => call.method === 'DELETE')).toBe(false);
  });

  /**
   * §3.3/AC4: the five outcomes render five DISTINCT messages.
   *
   * Driven as a table and then asserted for mutual distinctness at the end,
   * because the failure this guards against is not "one outcome renders
   * nothing" -- it is "all five collapse to the same red 'failed'", which every
   * per-outcome assertion in isolation would happily pass.
   */
  const outcomes = [
    { outcome: 'Ok', success: true, label: 'Connected', message: 'Connected successfully.' },
    {
      outcome: 'Unreachable',
      success: false,
      label: 'Unreachable',
      message: 'Could not reach the source before the timeout.',
    },
    {
      outcome: 'TlsFailure',
      success: false,
      label: 'TLS failure',
      message: 'Reached the source but the TLS handshake failed.',
    },
    {
      outcome: 'AuthenticationFailed',
      success: false,
      label: 'API key rejected',
      message: 'The source is reachable but rejected the API key.',
    },
    {
      outcome: 'UnexpectedResponse',
      success: false,
      label: 'Unexpected response',
      message: 'The source answered but not with the API expected.',
    },
  ];

  it.each(outcomes)(
    'reports the $outcome probe outcome as "$label"',
    async ({ outcome, success, label, message }) => {
      const user = userEvent.setup();
      mockApi({
        [SOURCES]: { body: sources },
        [`${SOURCES}/1/test`]: { body: { success, outcome, message } },
      });
      renderSurface(<SourcesSection />);

      await user.click(await within(await rowFor('Primary hydra')).findByRole('button', { name: 'Test' }));

      const result = await screen.findByRole('status');
      expect(within(result).getByText(label)).toBeInTheDocument();
      // The server's own wording is shown too: it says what to check next,
      // which is the whole reason the outcomes are distinguished at all.
      expect(within(result).getByText(message)).toBeInTheDocument();
    },
  );

  it('gives the five outcomes five different labels', () => {
    const labels = new Set(outcomes.map((entry) => entry.label));
    expect(labels.size).toBe(5);
  });

  /**
   * AC2: the key value never reaches browser storage.
   *
   * POSITIVE CONTROL FIRST. `Assert.DoesNotContain`-shaped assertions pass just
   * as happily when the secret was never in play -- an empty set contains
   * nothing -- so this test first proves the search it is about to run WOULD
   * find the key if the key were there. Only then is the real check meaningful.
   * Without the control, this test would keep passing if the field stopped
   * being filled in at all, or if `storageDump` read the wrong storage.
   */
  it('never writes the typed API key to localStorage or sessionStorage', async () => {
    const KEY = 'placeholder-secret-under-test';

    const storageDump = () =>
      JSON.stringify([
        Object.entries({ ...localStorage }),
        Object.entries({ ...sessionStorage }),
      ]);

    // --- positive control: the search demonstrably detects a planted key -----
    localStorage.setItem('planted', KEY);
    expect(storageDump()).toContain(KEY);
    localStorage.removeItem('planted');
    sessionStorage.setItem('planted', KEY);
    expect(storageDump()).toContain(KEY);
    sessionStorage.removeItem('planted');
    expect(storageDump()).not.toContain(KEY);

    // --- the real assertion -------------------------------------------------
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    // Type it into both forms, and submit both, so neither path is exempt.
    await user.type(await screen.findByLabelText('New source display name'), 'Added hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));
    await user.type(await screen.findByLabelText('Edit source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    // The key DID reach the wire -- otherwise the storage check below would be
    // vacuous for a second reason: a key that was never sent was never at risk.
    expect(api.calls.some((call) => call.body?.includes(KEY) === true)).toBe(true);

    expect(storageDump()).not.toContain(KEY);
  });

  it('never puts the key in a URL, only in a request body', async () => {
    const KEY = 'placeholder-url-check-key';
    const user = userEvent.setup();
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Added hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source API key'), KEY);
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    // Positive control: it reached the body, so "absent from the URL" is a real
    // finding rather than a statement about a key that was never sent.
    expect(api.calls.some((call) => call.body?.includes(KEY) === true)).toBe(true);
    expect(api.calls.every((call) => !call.url.toString().includes(KEY))).toBe(true);
  });

  it('attaches the admin key to its read and its writes', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(await within(await rowFor('Primary hydra')).findByRole('button', { name: 'Test' }));

    // Gated by PATH PREFIX, never by verb: the GET carries the key exactly as
    // the POST does.
    expect(api.calls.find((call) => call.method === 'GET')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
    expect(api.calls.find((call) => call.method === 'POST')?.headers[ADMIN_KEY_HEADER]).toBe(
      'operator-key',
    );
  });

  it("shows the server's own rejection when a write fails", async () => {
    const user = userEvent.setup();
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
  });
});
