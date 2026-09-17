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
    // The default. The second fixture is Redirect, so the edit form's seeding is
    // exercised for BOTH modes rather than for whichever happens to come first.
    nzbAccessMode: 'Proxy',
    // arb-x7w8.16 — the tuning columns, with values DISTINCT from the second
    // fixture's so an edit form seeded from the wrong row is visible rather than
    // coincidentally right.
    apiPath: '/api',
    priority: 5,
    timeoutSeconds: 12,
    limitsUnit: 'Day',
    runtimeState: 'Healthy',
    disabledUntil: null,
    disabledLevel: 0,
    lastOutcome: 'Success',
    queriesUsed: 4,
    grabsUsed: 1,
    queryLimit: 50,
    grabLimit: 10,
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
    nzbAccessMode: 'Redirect',
    // A null timeout alongside the null limits below, so the "stored null seeds
    // an empty box" path is exercised by the baseline fixture for all three
    // nullable columns and not only by the test that is about them.
    apiPath: '/api/v2',
    priority: 0,
    timeoutSeconds: null,
    limitsUnit: 'Hour',
    runtimeState: 'Healthy',
    disabledUntil: null,
    disabledLevel: 0,
    lastOutcome: null,
    // Null limits, so the unlimited rendering is exercised by the baseline
    // fixture and not only by the test that is about it.
    queriesUsed: 0,
    grabsUsed: 0,
    queryLimit: null,
    grabLimit: null,
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

  /**
   * arb-x7w8.11 — the three non-healthy states render DISTINCTLY, in ONE table.
   *
   * FOUR SOURCES IN ONE RENDER, not four tests with one source each. The defect
   * this is built to catch is a projection that renders every row identically —
   * a single-source test passes against exactly that, because with one row
   * "renders the right state" and "renders one state for everything" are
   * indistinguishable. Each state is asserted within its own row, so a
   * constant-returning implementation fails on the other three.
   */
  it('renders healthy, budgeted, backing off and permanently disabled as four distinct states', async () => {
    const at = (state: string, extra: Record<string, unknown> = {}) => ({
      ...sources[0],
      runtimeState: state,
      ...extra,
    });

    mockApi({
      [SOURCES]: {
        body: [
          { ...at('Healthy'), id: 11, displayName: 'Healthy hydra' },
          { ...at('Budgeted', { queriesUsed: 50, queryLimit: 50 }), id: 12, displayName: 'Budgeted hydra' },
          {
            ...at('BackingOff', { disabledUntil: '2099-01-01T10:05:00Z', disabledLevel: 2 }),
            id: 13,
            displayName: 'Backing hydra',
          },
          {
            ...at('PermanentlyDisabled', { lastOutcome: 'AuthenticationFailure' }),
            id: 14,
            displayName: 'Rejected hydra',
          },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    const healthy = await rowFor('Healthy hydra');
    expect(within(healthy).getByText('Healthy')).toBeInTheDocument();

    const budgeted = await rowFor('Budgeted hydra');
    expect(within(budgeted).getByText('Budgeted')).toBeInTheDocument();
    expect(within(budgeted).getByText('50 of 50')).toBeInTheDocument();

    const backing = await rowFor('Backing hydra');
    expect(within(backing).getByText('Backing off')).toBeInTheDocument();
    expect(within(backing).getByText(/level 2/)).toBeInTheDocument();

    const rejected = await rowFor('Rejected hydra');
    expect(within(rejected).getByText('Permanently disabled')).toBeInTheDocument();
    expect(within(rejected).getByText(/AuthenticationFailure/)).toBeInTheDocument();

    // The four labels are genuinely different strings, which is the property the
    // bead is about. A `within(row)` assertion would still pass if the labels
    // collided, so the distinctness is checked directly rather than implied.
    const labels = ['Healthy', 'Budgeted', 'Backing off', 'Permanently disabled'];
    expect(new Set(labels).size).toBe(labels.length);

    // "Permanently disabled" must not be readable as the configured Enabled flag:
    // every one of these four rows is enabled, so a surface that conflated the
    // two would show a contradiction here.
    expect(within(rejected).getByText('Enabled')).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  /**
   * NULL LIMIT IS UNLIMITED AND IS NOT ZERO, per row.
   *
   * Both cases in one render for the same reason as above: asserting only the
   * null case passes against an implementation that renders "unlimited" for
   * every source, and asserting only the numeric case passes against one that
   * coalesces null to zero and reports "0 of 0" — the exact collapse the
   * server column's doc warns about in both directions.
   */
  it('renders an unconfigured query limit as unlimited and a configured one as a cap', async () => {
    mockApi({
      [SOURCES]: {
        body: [
          { ...sources[0], id: 21, displayName: 'Uncapped hydra', queriesUsed: 3, queryLimit: null },
          { ...sources[0], id: 22, displayName: 'Capped hydra', queriesUsed: 3, queryLimit: 50 },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    const uncapped = await rowFor('Uncapped hydra');
    expect(within(uncapped).getByText('3 used, unlimited')).toBeInTheDocument();
    expect(within(uncapped).queryByText('3 of 0')).not.toBeInTheDocument();

    const capped = await rowFor('Capped hydra');
    expect(within(capped).getByText('3 of 50')).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  /**
   * THE GRABS COLUMN, per row, under the same null-is-unlimited rule.
   *
   * It exists because EITHER allowance being spent renders the one Budgeted
   * badge (SourceRuntimeStateReader.Derive): without grabs on the row that
   * badge is unattributable, since an operator sees Budgeted beside a queries
   * cell that is nowhere near its cap.
   *
   * Both cases in one render, and every number distinct from the queries cell
   * in the same row. Sharing a figure between the two columns is what would let
   * a surface rendering `queriesUsed` under BOTH headings pass: `within(row)`
   * finds the text either way. Here the query and grab cells cannot be
   * confused, so each assertion is evidence about its own column.
   */
  it('renders grabs against a cap and an unconfigured grab limit as unlimited', async () => {
    mockApi({
      [SOURCES]: {
        body: [
          {
            ...sources[0],
            id: 41,
            displayName: 'Capped grabs hydra',
            queriesUsed: 3,
            queryLimit: 50,
            grabsUsed: 7,
            grabLimit: 20,
          },
          {
            ...sources[0],
            id: 42,
            displayName: 'Uncapped grabs hydra',
            queriesUsed: 4,
            queryLimit: 60,
            grabsUsed: 9,
            grabLimit: null,
          },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    const capped = await rowFor('Capped grabs hydra');
    expect(within(capped).getByText('7 of 20')).toBeInTheDocument();
    // The queries cell in the same row still reads its own figure, so the grab
    // cell above is genuinely a second column and not the first one relabelled.
    expect(within(capped).getByText('3 of 50')).toBeInTheDocument();

    const uncapped = await rowFor('Uncapped grabs hydra');
    expect(within(uncapped).getByText('9 used, unlimited')).toBeInTheDocument();
    // Never "9 of 0": the `?? 0` collapse is the defect on this column too.
    expect(within(uncapped).queryByText('9 of 0')).not.toBeInTheDocument();
    expect(within(uncapped).getByText('4 of 60')).toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  /**
   * A `disabledUntil` IN THE PAST is not a backoff.
   *
   * The row keeps the instant after the hold-off elapses, because the level it
   * was reached at is still live information. A surface keyed off the field
   * being non-null rather than off `runtimeState` would show every recovered
   * source as still waiting, indefinitely.
   */
  it('does not render a hold-off countdown for a healthy source whose disabledUntil has passed', async () => {
    mockApi({
      [SOURCES]: {
        body: [
          {
            ...sources[0],
            id: 31,
            displayName: 'Recovered hydra',
            runtimeState: 'Healthy',
            disabledUntil: '2020-01-01T10:00:00Z',
            disabledLevel: 3,
          },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    const recovered = await rowFor('Recovered hydra');
    expect(within(recovered).getByText('Healthy')).toBeInTheDocument();
    expect(within(recovered).queryByText(/level 3/)).not.toBeInTheDocument();
    expect(within(recovered).queryByText(/until /)).not.toBeInTheDocument();
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

  // F-020c: Sources carries the same "Restart required" badge as the
  // Maintenance interval setting, matching its own copy ("take effect on the
  // next restart") a few lines below. The heading stays exactly "Sources" --
  // the badge is a sibling of the <h2>, not nested inside it, so it does not
  // fold into the heading's accessible name.
  it('carries a Restart required badge next to the Sources heading', async () => {
    mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    expect(await screen.findByRole('heading', { name: 'Sources' })).toBeInTheDocument();
    expect(screen.getByText('Restart required')).toBeInTheDocument();
    expect(
      screen.getByText(/These are stored in the database and take effect on the next restart/),
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
      // arb-x7w8.14: always sent, and 'Proxy' unless the operator chose otherwise
      // — the wire half of "Redirect ships OFF by default".
      nzbAccessMode: 'Proxy',
      // arb-x7w8.16: the window is a select that always holds a value, so it is
      // always sent. NOTE WHAT IS ABSENT alongside it — this is an exact
      // `toEqual`, so it also asserts that the five tuning fields the operator
      // did not type send NOTHING rather than a zero: no apiPath, no priority,
      // no timeoutSeconds, and crucially no `queryLimit: 0` or `grabLimit: 0`,
      // which would store a cap of nothing on a source meant to be uncapped.
      limitsUnit: 'Day',
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

  /**
   * arb-x7w8.14 — THE EXPOSURE WARNING, WHICH IS WHY THE SERVER ACCEPTS
   * 'Redirect' AT ALL.
   *
   * `SourceRepository` refused this value outright until this control existed,
   * because the owner's ruling is that the mode ships OFF, per-indexer opt-in,
   * AND that the operator is told what it costs. This test is the UI half of
   * that pairing: if it is deleted, the repository's array entry has lost the
   * thing that justified it.
   *
   * ASSOCIATED WITH THE CONTROL, NOT MERELY PRESENT ON THE PAGE. The assertion
   * reads the select's `aria-describedby` and resolves it to the warning's id,
   * because a warning a screen reader never reaches while the control has focus
   * is decoration. Asserting only `getByText(/visible to Sonarr/)` would pass
   * against a warning rendered in a footer three sections away.
   */
  it('warns that the indexer key is exposed when Redirect is chosen, and ties the warning to the control', async () => {
    const user = userEvent.setup({ delay: null });
    mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    const select = await screen.findByLabelText('New source nzb access mode');

    // Proxy is the initial value, and it carries NO warning -- a permanent
    // warning beside a safe default trains an operator to ignore it.
    expect(select).toHaveValue('Proxy');
    expect(select).not.toHaveAttribute('aria-describedby');
    expect(screen.queryByText(/will be visible to Sonarr/)).not.toBeInTheDocument();

    await user.selectOptions(select, 'Redirect');

    // THE ASSOCIATION: the id the control points at is the element carrying the
    // warning text. Resolved through the DOM rather than asserted as a literal,
    // so a renamed id fails here rather than silently breaking the link.
    const describedBy = select.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();

    const warning = document.getElementById(describedBy!);
    expect(warning).not.toBeNull();
    expect(warning).toHaveTextContent(/API key will be visible to Sonarr, Radarr/);

    // The consequence, the mechanism, and the way back are all stated: an
    // operator is never shown a bare, unexplained value.
    expect(warning).toHaveTextContent(/redirect to the indexer/);
    expect(warning).toHaveTextContent(/Choose Proxy to keep the key on the server/);
  }, TEST_TIMEOUT_MS);

  /**
   * The warning is on the EDIT form too, seeded from the stored mode — which is
   * the path by which an existing source actually reaches Redirect, since a new
   * one is created as Proxy. A test covering only the add form would leave the
   * realistic opt-in route unwarned.
   */
  it('seeds the stored mode on the edit form and warns when it is Redirect', async () => {
    const user = userEvent.setup({ delay: null });
    mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    // 'Spare hydra' is the Redirect fixture: the warning is present on open,
    // without the operator touching anything.
    await user.click(within(await rowFor('Spare hydra')).getByRole('button', { name: 'Edit' }));

    const select = await screen.findByLabelText('Edit source nzb access mode');
    expect(select).toHaveValue('Redirect');

    const describedBy = select.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(document.getElementById(describedBy!)).toHaveTextContent(
      /API key will be visible to Sonarr, Radarr/,
    );

    // And switching back to Proxy withdraws it, so the warning tracks the
    // current choice rather than latching on first sight of Redirect.
    await user.selectOptions(select, 'Proxy');
    expect(select).not.toHaveAttribute('aria-describedby');
    expect(screen.queryByText(/will be visible to Sonarr/)).not.toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  /**
   * The chosen mode actually reaches the wire, on both write paths. Without this
   * the two tests above would pass against a form that displayed the control,
   * warned correctly, and then dropped the value — the setting would read as
   * applied while every download stayed in proxy mode.
   *
   * The edit body is asserted to CARRY the field, unlike `apiKey` which must be
   * absent when untouched: the form always displays a mode, so the save must
   * always mean it. See `UpdateSourceRequest`'s doc.
   */
  it('sends the chosen access mode on create and on edit', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Redirecting hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.selectOptions(screen.getByLabelText('New source nzb access mode'), 'Redirect');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const post = api.calls.find((call) => call.method === 'POST');
    expect(JSON.parse(post!.body!).nzbAccessMode).toBe('Redirect');

    // The exact ordinal spelling the server accepts -- 'redirect' and '1' are
    // both 400s, so this is asserted as the literal rather than case-insensitively.
    expect(JSON.parse(post!.body!).nzbAccessMode).not.toBe('redirect');
  }, TEST_TIMEOUT_MS);

  it('sends the access mode on an edit even when the operator did not change it', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Spare hydra')).getByRole('button', { name: 'Edit' }));
    await screen.findByLabelText('Edit source nzb access mode');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const put = api.calls.find((call) => call.method === 'PUT');
    expect(JSON.parse(put!.body!).nzbAccessMode).toBe('Redirect');
  }, TEST_TIMEOUT_MS);

  /**
   * arb-x7w8.16 — KIND IS A CLOSED PICKER AND THE FREE-TEXT PATH IS GONE.
   *
   * The option list is asserted EXACTLY rather than by presence, because
   * `getByRole('option', { name: /Torznab/ })` passes just as well against a
   * picker that also offers a fourth value the server would reject — and the
   * whole reason this control replaced a text box is that the accepted set is
   * closed. Asserting the set is asserting the thing that changed.
   */
  it('offers exactly the three kinds the server accepts, and no free-text box', async () => {
    mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    const select = await screen.findByLabelText('New source kind');
    // A <select>, not an <input>: a text box with the same label would satisfy
    // every value assertion below while still letting an operator type 'nzbhydra'.
    expect(select.tagName).toBe('SELECT');

    expect(
      within(select)
        .getAllByRole('option')
        .map((option) => (option as HTMLOptionElement).value),
    ).toEqual(['NzbHydra', 'Newznab', 'Torznab']);
  }, TEST_TIMEOUT_MS);

  /**
   * Each kind reaches the wire as the EXACT ordinal spelling.
   *
   * Per kind rather than once, because a picker that emitted its label, its
   * index, or a lower-cased value would pass a single-kind test whose fixture
   * happened to be the default. The negative assertion on the lower-cased form
   * is the one that names the actual failure mode: `SourceRepository` compares
   * ordinally, so 'nzbhydra' is a 400 rather than a tolerated variant.
   */
  it.each(['NzbHydra', 'Newznab', 'Torznab'])(
    'sends %s as the exact string the server matches ordinally',
    async (kind) => {
      const user = userEvent.setup({ delay: null });
      const api = mockApi({ [SOURCES]: { body: [] } });
      renderSurface(<SourcesSection />);

      await user.type(await screen.findByLabelText('New source display name'), `A ${kind}`);
      await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
      await user.selectOptions(screen.getByLabelText('New source kind'), kind);
      await user.click(screen.getByRole('button', { name: 'Add source' }));

      const body = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
      expect(body.kind).toBe(kind);
      expect(body.kind).not.toBe(kind.toLowerCase());
    },
    TEST_TIMEOUT_MS,
  );

  /**
   * A stored kind outside the three is SHOWN, not silently rewritten.
   *
   * Such a row should not exist — every write path validates — but if one does,
   * an operator opening the form must see what is actually stored. A select that
   * re-selected the first option instead would make an unrelated save rewrite a
   * column nobody looked at, which is a data change disguised as a render.
   */
  it('shows a stored kind outside the three as-is rather than rewriting it', async () => {
    const user = userEvent.setup({ delay: null });
    mockApi({
      [SOURCES]: {
        body: [{ ...sources[0], id: 61, displayName: 'Legacy hydra', kind: 'SomethingElse' }],
      },
    });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Legacy hydra')).getByRole('button', { name: 'Edit' }));

    const select = await screen.findByLabelText('Edit source kind');
    expect(select).toHaveValue('SomethingElse');
    // And specifically NOT quietly snapped to the first option.
    expect(select).not.toHaveValue('NzbHydra');
  }, TEST_TIMEOUT_MS);

  /**
   * arb-x7w8.16 — every tuning field reaches the create body, with its own name
   * and as a NUMBER where the contract says a number.
   *
   * The types are asserted as well as the values because `<input type="number">`
   * hands back a string: a builder that forwarded `event.target.value` untouched
   * would produce `priority: "7"`, which `toEqual`'s own loose reading of a
   * body would not catch if only the digits were compared. `typeof` is what
   * makes this test about the coercion.
   */
  it('sends every tuning field on create, with numbers as numbers', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Tuned hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source API path'), '/api/v2');
    await user.type(screen.getByLabelText('New source priority'), '7');
    await user.type(screen.getByLabelText('New source timeout seconds'), '12');
    await user.type(screen.getByLabelText('New source query limit'), '400');
    await user.type(screen.getByLabelText('New source grab limit'), '25');
    await user.selectOptions(screen.getByLabelText('New source limits window'), 'Hour');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
    expect(body.apiPath).toBe('/api/v2');
    expect(body.priority).toBe(7);
    expect(body.timeoutSeconds).toBe(12);
    expect(body.queryLimit).toBe(400);
    expect(body.grabLimit).toBe(25);
    expect(body.limitsUnit).toBe('Hour');

    // Numbers, not the strings the inputs hold.
    expect(typeof body.priority).toBe('number');
    expect(typeof body.timeoutSeconds).toBe('number');
    expect(typeof body.queryLimit).toBe('number');
    expect(typeof body.grabLimit).toBe('number');
  }, TEST_TIMEOUT_MS);

  /**
   * UNLIMITED IS ABSENT, AND IT IS NOT ZERO — on the create path.
   *
   * THE POSITIVE CONTROL IS THE FIRST HALF OF THIS TEST, in the same harness: a
   * typed limit IS sent, which proves the field reaches the body at all. Only
   * then does the absence assertion mean anything — `not.toHaveProperty` passes
   * just as happily against a form that never wired the field up, and this test
   * would otherwise be vacuous in exactly the way CLAUDE.md §4 describes.
   */
  it('sends a typed limit on create but omits an empty one rather than sending zero', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    // CONTROL: a limit that IS typed reaches the body.
    await user.type(await screen.findByLabelText('New source display name'), 'Capped hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source query limit'), '400');
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    const control = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
    expect(control.queryLimit).toBe(400);
    // The grab limit was left empty in the very same submission, so the two
    // arms are compared under identical conditions.
    expect(control).not.toHaveProperty('grabLimit');
    // And absent means absent: never the zero that would store a cap of nothing.
    expect(control.grabLimit).not.toBe(0);
  }, TEST_TIMEOUT_MS);

  /**
   * THE UPDATE'S THREE STATES, ASSERTED AS THREE — untouched, cleared, changed.
   *
   * These are one test rather than three because the distinction between them is
   * the whole contract: asserting only "a cleared limit sends the clear flag"
   * passes against an implementation that sends the flag unconditionally, which
   * would blank a limit on every unrelated edit. Driven from a fixture with a
   * stored value in each of the three nullable columns, so every arm has
   * something real to leave alone, clear, or change.
   */
  it('distinguishes an untouched limit, a cleared one and a changed one on edit', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({
      [SOURCES]: {
        body: [
          {
            ...sources[0],
            id: 71,
            displayName: 'Three state hydra',
            timeoutSeconds: 12,
            queryLimit: 400,
            grabLimit: 25,
          },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    await user.click(
      within(await rowFor('Three state hydra')).getByRole('button', { name: 'Edit' }),
    );

    // The boxes seed from the stored values -- the control for everything below:
    // without this the "untouched" arm could pass against a form that never read
    // them, and the "cleared" arm against one where they arrived empty already.
    expect(await screen.findByLabelText('Edit source timeout seconds')).toHaveValue(12);
    expect(screen.getByLabelText('Edit source query limit')).toHaveValue(400);
    expect(screen.getByLabelText('Edit source grab limit')).toHaveValue(25);

    // CLEARED: the operator empties the query limit.
    await user.clear(screen.getByLabelText('Edit source query limit'));
    // CHANGED: the grab limit gets a new number.
    const grab = screen.getByLabelText('Edit source grab limit');
    await user.clear(grab);
    await user.type(grab, '30');
    // UNTOUCHED: the timeout is not touched at all.
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);

    // CLEARED -- the flag, and emphatically not a zero. `0` here would store a
    // cap of nothing rather than restoring "no cap at all".
    expect(body.clearQueryLimit).toBe(true);
    expect(body).not.toHaveProperty('queryLimit');
    expect(body.queryLimit).not.toBe(0);

    // CHANGED -- the number, and no flag alongside it.
    expect(body.grabLimit).toBe(30);
    expect(typeof body.grabLimit).toBe('number');
    expect(body).not.toHaveProperty('clearGrabLimit');

    // UNTOUCHED -- neither. The stored value is left exactly as it was, which is
    // what stops an unrelated edit from disturbing a limit nobody looked at.
    expect(body.timeoutSeconds).toBe(12);
    expect(body).not.toHaveProperty('clearTimeoutSeconds');
  }, TEST_TIMEOUT_MS);

  /**
   * An ALREADY-NULL limit that stays empty sends NOTHING — not even the flag.
   *
   * The distinction this protects is between "the operator cleared it" and
   * "there was never a value": both leave an empty box, and only the first is a
   * write. The positive control is the third assertion, which shows the same
   * submission DOES carry a clear flag for the column that really was cleared —
   * so the two absences above it are evidence rather than a silent no-op.
   */
  it('sends no clear flag for a limit that was already unlimited', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({
      [SOURCES]: {
        body: [
          {
            ...sources[0],
            id: 72,
            displayName: 'Half capped hydra',
            queryLimit: null,
            grabLimit: 25,
            timeoutSeconds: null,
          },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Half capped hydra')).getByRole('button', { name: 'Edit' }));

    // Stored null seeds an EMPTY box -- never a '0'.
    expect(await screen.findByLabelText('Edit source query limit')).toHaveValue(null);

    // Clear the one that DOES have a stored value, and leave the null one alone.
    await user.clear(screen.getByLabelText('Edit source grab limit'));
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);

    // Already null, still empty: no value and no flag.
    expect(body).not.toHaveProperty('queryLimit');
    expect(body).not.toHaveProperty('clearQueryLimit');
    expect(body).not.toHaveProperty('clearTimeoutSeconds');

    // CONTROL: the column that really was cleared does carry its flag, so the
    // three absences above are a distinction this code draws and not a builder
    // that emits no flags at all.
    expect(body.clearGrabLimit).toBe(true);
  }, TEST_TIMEOUT_MS);

  /**
   * A TYPED `0` IS A CAP OF ZERO AND IS SENT AS `0`.
   *
   * The mirror image of the clear-flag rule, and the reason that rule is written
   * as "empty box" rather than "falsy value": `0` is a real value the server
   * accepts, so the form must not helpfully translate it into "unlimited". Both
   * directions in one test, because each alone passes against an implementation
   * that collapses the two.
   */
  it('sends a typed zero as a cap of zero, not as unlimited', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({
      [SOURCES]: {
        body: [{ ...sources[0], id: 73, displayName: 'Zeroed hydra', queryLimit: 400 }],
      },
    });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Zeroed hydra')).getByRole('button', { name: 'Edit' }));

    const field = await screen.findByLabelText('Edit source query limit');
    await user.clear(field);
    await user.type(field, '0');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);
    expect(body.queryLimit).toBe(0);
    expect(typeof body.queryLimit).toBe('number');
    // A typed zero is NOT a clear: the operator asked for a cap of nothing, and
    // the server decides whether it likes that.
    expect(body).not.toHaveProperty('clearQueryLimit');
  }, TEST_TIMEOUT_MS);

  /**
   * A STORED NULL RENDERS AS UNLIMITED AND A STORED ZERO RENDERS AS ZERO, in one
   * render.
   *
   * Both in the same test because the distinction is what is being asserted:
   * separately, the null case passes against a surface that renders "unlimited"
   * for everything, and the zero case against one that coalesces null to zero.
   * This covers both the table cell and the edit form's box, since the collapse
   * can be introduced independently in either.
   */
  it('renders a stored null as unlimited and a stored zero as zero, in the table and the form', async () => {
    const user = userEvent.setup({ delay: null });
    mockApi({
      [SOURCES]: {
        body: [
          { ...sources[0], id: 81, displayName: 'Null limit hydra', queriesUsed: 2, queryLimit: null },
          { ...sources[0], id: 82, displayName: 'Zero limit hydra', queriesUsed: 2, queryLimit: 0 },
        ],
      },
    });
    renderSurface(<SourcesSection />);

    const nulled = await rowFor('Null limit hydra');
    expect(within(nulled).getByText('2 used, unlimited')).toBeInTheDocument();
    expect(within(nulled).queryByText('2 of 0')).not.toBeInTheDocument();

    // The zero row reads as a real cap of zero, which is a different fact.
    const zeroed = await rowFor('Zero limit hydra');
    expect(within(zeroed).getByText('2 of 0')).toBeInTheDocument();

    // And the same distinction survives into the form: empty box versus '0'.
    await user.click(within(nulled).getByRole('button', { name: 'Edit' }));
    expect(await screen.findByLabelText('Edit source query limit')).toHaveValue(null);

    await user.click(within(await rowFor('Zero limit hydra')).getByRole('button', { name: 'Edit' }));
    await waitFor(() =>
      expect(screen.getByLabelText('Edit source query limit')).toHaveValue(0),
    );
  }, TEST_TIMEOUT_MS);

  /**
   * The timeout box carries the SERVER'S OWN bounds as native attributes, and an
   * out-of-range value is stopped by the browser's constraint validation rather
   * than by any code in this file.
   *
   * `min`/`max` are asserted as the literal 1 and 30 because they mirror
   * `SourceRepository.MinTimeoutSeconds` and `MaxTimeoutSeconds` -- a bound
   * invented here instead would let the form accept a value the server rejects,
   * or refuse one it accepts.
   *
   * THE ABSENCE OF A POST IS ASSERTED AGAINST A POSITIVE CONTROL: the same
   * harness first shows that an IN-RANGE value does submit, so "no POST" is
   * evidence about the out-of-range value and not about a form that never
   * submits at all. Without that half this would pass against a broken button.
   */
  it('mirrors the server bounds on the timeout box and lets the browser stop an out-of-range value', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    const timeout = await screen.findByLabelText('New source timeout seconds');
    expect(timeout).toHaveAttribute('min', '1');
    expect(timeout).toHaveAttribute('max', '30');

    await user.type(screen.getByLabelText('New source display name'), 'Slow hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');

    // CONTROL: an in-range value submits, so the button and the wiring work.
    await user.type(timeout, '30');
    await user.click(screen.getByRole('button', { name: 'Add source' }));
    await waitFor(() => expect(api.calls.some((call) => call.method === 'POST')).toBe(true));
    expect(JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!).timeoutSeconds).toBe(
      30,
    );

    // Now one second past the maximum, in the same form: the native constraint
    // refuses the submit, so no second POST is made.
    const postsBefore = api.calls.filter((call) => call.method === 'POST').length;
    await user.clear(timeout);
    await user.type(timeout, '31');
    await user.click(screen.getByRole('button', { name: 'Add source' }));
    expect(api.calls.filter((call) => call.method === 'POST')).toHaveLength(postsBefore);
  }, TEST_TIMEOUT_MS);

  /**
   * A SERVER 400 ON A TUNING FIELD RENDERS THE SERVER'S OWN MESSAGE.
   *
   * Driven with an in-range timeout so the native attributes cannot be what
   * stops it: the request genuinely reaches the wire and genuinely comes back
   * rejected, which is the only way to prove this form adds no validation layer
   * of its own. A form that pre-empted the server here would show different
   * words -- or none -- and would have put a second copy of the rule in the tree.
   *
   * The POST body assertion is the positive control: it shows the value that was
   * rejected is the value that was sent, so the message below answers this
   * submission rather than merely being present on the page.
   */
  it("renders the server's own rejection of a tuning field", async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: [] } });
    renderSurface(<SourcesSection />);

    await user.type(await screen.findByLabelText('New source display name'), 'Pathy hydra');
    await user.type(screen.getByLabelText('New source base URL'), 'http://192.0.2.30:5076');
    await user.type(screen.getByLabelText('New source timeout seconds'), '25');
    await user.type(screen.getByLabelText('New source API path'), '/api?t=caps');

    api.set(SOURCES, {
      status: 400,
      body: {
        error:
          'Source API path must not contain a query string; search parameters are appended by Arbitarr.',
      },
    });
    await user.click(screen.getByRole('button', { name: 'Add source' }));

    expect(
      await screen.findByText(
        'Source API path must not contain a query string; search parameters are appended by Arbitarr.',
      ),
    ).toBeInTheDocument();

    // CONTROL: the rejected value is the one this form sent.
    const body = JSON.parse(api.calls.find((call) => call.method === 'POST')!.body!);
    expect(body.apiPath).toBe('/api?t=caps');
    expect(body.timeoutSeconds).toBe(25);
  }, TEST_TIMEOUT_MS);

  /**
   * The tuning fields did not disturb the apiKey contract.
   *
   * A regression guard rather than new behaviour: `toUpdateRequest` grew six
   * fields and three flags in arb-x7w8.16, and the one property in that body
   * whose presence blanks a working credential is still absent when untouched.
   * The control is the positive assertion that the new fields ARE present in the
   * same body -- without it this restates an existing test against a builder
   * that might have stopped sending anything at all.
   */
  it('still omits an untouched apiKey from an edit that carries the tuning fields', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Primary hydra')).getByRole('button', { name: 'Edit' }));

    const priority = await screen.findByLabelText('Edit source priority');
    await user.clear(priority);
    await user.type(priority, '9');
    await user.click(screen.getByRole('button', { name: 'Save changes' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);
    // CONTROL: the edited tuning field is in the body, so this really is the
    // arb-x7w8.16 code path and not a build that dropped the fields.
    expect(body.priority).toBe(9);
    expect(body.apiPath).toBe('/api');
    expect(body.limitsUnit).toBe('Day');

    // And the field whose presence would clear the stored key is still absent.
    expect(body).not.toHaveProperty('apiKey');
  }, TEST_TIMEOUT_MS);

  /**
   * Toggling Enabled from the row disturbs no tuning value.
   *
   * That path builds its body inline and shows the operator no form, so absence
   * is correct for every tuning field -- and a clear flag leaking into it would
   * blank a limit on a click that says only "Enable". The positive control is the
   * `enabled` assertion: the body reached the wire and carried the edit that was
   * asked for.
   */
  it('sends no tuning field or clear flag when Enabled is toggled from the row', async () => {
    const user = userEvent.setup({ delay: null });
    const api = mockApi({ [SOURCES]: { body: sources } });
    renderSurface(<SourcesSection />);

    await user.click(within(await rowFor('Spare hydra')).getByRole('button', { name: 'Enable' }));

    const body = JSON.parse(api.calls.find((call) => call.method === 'PUT')!.body!);
    // CONTROL: the toggle really did write.
    expect(body.enabled).toBe(true);

    for (const field of [
      'apiPath',
      'priority',
      'timeoutSeconds',
      'queryLimit',
      'grabLimit',
      'limitsUnit',
      'clearTimeoutSeconds',
      'clearQueryLimit',
      'clearGrabLimit',
    ]) {
      expect(body).not.toHaveProperty(field);
    }
  }, TEST_TIMEOUT_MS);
});
