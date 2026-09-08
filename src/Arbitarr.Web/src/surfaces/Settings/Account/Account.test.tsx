import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider, useMutation } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AccountSection } from './Account';
import { apiFetch } from '../../../api/client';

const PASSWORD_ROUTE = '/api/auth/password';

// Throwaway values, distinctive enough that a sweep of a serialised cache either
// finds them or genuinely does not hold them. This repository is public; no real
// credential appears in committed content.
const CURRENT_PASSWORD = 'example-current-passphrase';
const NEW_PASSWORD = 'example-rotated-passphrase';

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

function mockAccountApi(reply: Reply) {
  const calls: Call[] = [];
  let current = reply;

  vi.stubGlobal(
    'fetch',
    vi.fn((input: string, init?: RequestInit) => {
      const url = new URL(input, 'http://localhost');
      calls.push({
        path: url.pathname,
        method: init?.method ?? 'GET',
        headers: { ...((init?.headers as Record<string, string>) ?? {}) },
        body: typeof init?.body === 'string' ? init.body : undefined,
      });

      const status = current.status ?? 204;
      return Promise.resolve(
        new Response(status === 204 || current.body === undefined ? null : JSON.stringify(current.body), {
          status,
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );

  return {
    calls,
    set: (next: Reply) => {
      current = next;
    },
  };
}

/**
 * Mounts the section against a client the test can then inspect.
 *
 * The client's own defaults deliberately set NO gcTime, mirroring
 * `src/api/queryClient.ts`. If the mutation's `gcTime: 0` were removed, the
 * five-minute default would apply here exactly as it does in the app -- which is
 * what the control below demonstrates.
 */
function renderWithClient() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(
    <QueryClientProvider client={client}>
      <AccountSection />
    </QueryClientProvider>,
  );

  return client;
}

/**
 * Serialises everything the MutationCache is holding.
 *
 * `state.variables` is where a settled change keeps BOTH passwords, so this is
 * the sweep an operator with the devtools open would be doing by hand. Whole
 * mutation objects rather than just `state`, so a copy parked on any other field
 * would be caught too.
 */
function sweepMutationCache(client: QueryClient): string {
  return JSON.stringify(client.getMutationCache().getAll());
}

/**
 * POSITIVE CONTROL 1, and the reason the cache assertions below bite.
 *
 * The SAME change, against the same fetch double, through a mutation that differs
 * from the shipped one in exactly one respect: no `gcTime: 0` and no reset. It
 * reproduces the retention this change exists to remove, and proves the sweep
 * FINDS passwords that are really there. Without it, `not.toContain` would pass
 * just as happily against a cache that never held a change at all, or against a
 * sweep pointed at the wrong place -- an empty set contains nothing.
 *
 * Driven through a real mutation rather than a hand-planted cache entry, because
 * a planted object only proves `JSON.stringify` can see a string; this proves the
 * retention is a property of react-query's defaults, which is the claim the fix
 * answers.
 */
function LeakyChangeHarness() {
  const mutation = useMutation({
    mutationFn: (request: { currentPassword: string; newPassword: string }) =>
      apiFetch<void>(PASSWORD_ROUTE, { method: 'POST', body: JSON.stringify(request) }),
  });

  return (
    <button
      type="button"
      onClick={() => mutation.mutate({ currentPassword: CURRENT_PASSWORD, newPassword: NEW_PASSWORD })}
    >
      Leaky change
    </button>
  );
}

/**
 * POSITIVE CONTROL 2: the hook-level `onSettled` placement the shipped code
 * warns against, reproduced so the warning is evidence rather than folklore.
 *
 * react-query awaits hook-level callbacks BEFORE dispatching the settle action,
 * and that dispatch is what runs the per-call ones. Resetting from up there
 * removes the observer they would arrive on, so the per-call `onError` never
 * runs and the server's rejection is never rendered. This harness asserts exactly
 * that swallowing, which is why `dropChangeFromCache` lives in the component.
 */
function SwallowedRejectionHarness({ onFailure }: { onFailure: (message: string) => void }) {
  const mutation = useMutation({
    gcTime: 0,
    mutationFn: (request: { currentPassword: string; newPassword: string }) =>
      apiFetch<void>(PASSWORD_ROUTE, { method: 'POST', body: JSON.stringify(request) }),
    // THE PLACEMENT UNDER TEST. Awaited before the settle dispatch, so the reset
    // below removes the observer the per-call onError would have arrived on.
    onSettled: () => {
      mutation.reset();
    },
  });

  return (
    <button
      type="button"
      onClick={() =>
        mutation.mutate(
          { currentPassword: CURRENT_PASSWORD, newPassword: NEW_PASSWORD },
          { onError: (error) => onFailure(String(error)) },
        )
      }
    >
      Swallowing change
    </button>
  );
}

function renderHarness(node: React.ReactNode) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });

  render(<QueryClientProvider client={client}>{node}</QueryClientProvider>);

  return client;
}

/**
 * Patience, not a behaviour change. The vitest config sets `css: true`, so every
 * render parses the real stylesheet, and each change test drives a full mutation
 * round trip on top of that. The assertions are unchanged.
 */
const TEST_TIMEOUT_MS = 20_000;

/** Serialises both browser stores, for the "never persisted" assertions. */
function sweepBrowserStorage(): string {
  return JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } });
}

async function fillAndSubmit(
  user: ReturnType<typeof userEvent.setup>,
  { current, next, confirm }: { current: string; next: string; confirm?: string },
) {
  await user.type(screen.getByLabelText('Current password'), current);
  await user.type(screen.getByLabelText('New password'), next);
  await user.type(screen.getByLabelText('Confirm new password'), confirm ?? next);
  await user.click(screen.getByRole('button', { name: 'Change password' }));
}

describe('Account', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('posts the change and reports Saved.', async () => {
    const user = userEvent.setup();
    const api = mockAccountApi({ status: 204 });
    renderWithClient();

    await fillAndSubmit(user, { current: CURRENT_PASSWORD, next: NEW_PASSWORD });

    expect(await screen.findByText('Saved.')).toBeInTheDocument();

    const posted = api.calls.filter((call) => call.method === 'POST');
    expect(posted).toHaveLength(1);
    expect(posted[0].path).toBe(PASSWORD_ROUTE);

    // The confirmation field is compared client-side and NEVER sent.
    const body = JSON.parse(posted[0].body ?? '{}');
    expect(body).toEqual({ currentPassword: CURRENT_PASSWORD, newPassword: NEW_PASSWORD });
    expect(posted[0].body).not.toContain('confirm');

    // And no admin key rides along: this route is under /api/auth/, so
    // `needsAdminKey` is false and a machine credential is never offered.
    expect(Object.keys(posted[0].headers)).not.toContain('X-Admin-Api-Key');
  }, TEST_TIMEOUT_MS);

  it("renders the server's exact rejection", async () => {
    const user = userEvent.setup();
    // The server's real generic refusal, verbatim. A client that paraphrased it
    // cannot satisfy this by accident.
    const refusal = 'The current password is incorrect.';
    mockAccountApi({ status: 401, body: { detail: refusal, title: 'Password change failed' } });
    renderWithClient();

    await fillAndSubmit(user, { current: 'example-wrong-passphrase', next: NEW_PASSWORD });

    expect(await screen.findByText(refusal)).toBeInTheDocument();
    expect(screen.queryByText('Saved.')).not.toBeInTheDocument();
  }, TEST_TIMEOUT_MS);

  it('refuses a mismatched confirmation without calling the server', async () => {
    const user = userEvent.setup();
    const api = mockAccountApi({ status: 204 });
    renderWithClient();

    await fillAndSubmit(user, {
      current: CURRENT_PASSWORD,
      next: NEW_PASSWORD,
      confirm: 'example-mistyped-passphrase',
    });

    expect(await screen.findByText(/do not match/i)).toBeInTheDocument();
    expect(api.calls.filter((call) => call.method === 'POST')).toHaveLength(0);
  }, TEST_TIMEOUT_MS);

  it('retains both passwords in the mutation cache without gcTime and reset', async () => {
    const user = userEvent.setup();
    mockAccountApi({ status: 204 });
    const client = renderHarness(<LeakyChangeHarness />);

    // Nothing yet -- so the assertion below cannot be satisfied by a cache that
    // was already dirty before the change ran.
    expect(sweepMutationCache(client)).not.toContain(CURRENT_PASSWORD);
    expect(sweepMutationCache(client)).not.toContain(NEW_PASSWORD);

    await user.click(screen.getByRole('button', { name: 'Leaky change' }));

    // THE POSITIVE CONTROL. A change through a mutation with react-query's default
    // gcTime and no reset leaves `variables` -- BOTH passwords -- sitting in the
    // MutationCache after it settles. This is the retention the shipped mutation
    // removes, and it is what makes the absence assertions below evidence rather
    // than a search that could never have matched.
    await vi.waitFor(() => expect(sweepMutationCache(client)).toContain(CURRENT_PASSWORD));
    expect(sweepMutationCache(client)).toContain(NEW_PASSWORD);
  }, TEST_TIMEOUT_MS);

  it('swallows the rejection when the reset is moved to a hook-level onSettled', async () => {
    const user = userEvent.setup();
    mockAccountApi({ status: 401, body: { detail: 'The current password is incorrect.' } });

    const delivered: string[] = [];
    const client = renderHarness(
      <SwallowedRejectionHarness onFailure={(message) => delivered.push(message)} />,
    );

    await user.click(screen.getByRole('button', { name: 'Swallowing change' }));

    // The request really was made and really did fail, so the silence below is
    // the callback never running -- not a mutation that never started.
    await vi.waitFor(() => expect(sweepMutationCache(client)).toBe('[]'));

    // POSITIVE CONTROL 2: with the reset up at the hook level, the per-call
    // onError never runs at all -- so a component built that way would render no
    // message for a rejection the server really issued. That is the failure the
    // comment on `dropChangeFromCache` warns about, and the reason the shipped
    // reset lives in the per-call callbacks instead. The shipped component's own
    // rejection test above is the other half of the pair.
    expect(delivered).toHaveLength(0);
  }, TEST_TIMEOUT_MS);

  it('leaves neither password in the mutation cache after a successful change', async () => {
    const user = userEvent.setup();
    mockAccountApi({ status: 204 });
    const client = renderWithClient();

    await fillAndSubmit(user, { current: CURRENT_PASSWORD, next: NEW_PASSWORD });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();

    // BOTH fields, because #93's bug was a test that searched for the FIRST value
    // while the leak carried the LAST.
    await vi.waitFor(() => expect(sweepMutationCache(client)).not.toContain(CURRENT_PASSWORD));
    expect(sweepMutationCache(client)).not.toContain(NEW_PASSWORD);

    // And neither reached browser storage, matching the existing session posture.
    expect(sweepBrowserStorage()).not.toContain(CURRENT_PASSWORD);
    expect(sweepBrowserStorage()).not.toContain(NEW_PASSWORD);
  }, TEST_TIMEOUT_MS);

  it('leaves neither password in the mutation cache after a rejected change', async () => {
    const user = userEvent.setup();
    const refusal = 'The current password is incorrect.';
    mockAccountApi({ status: 401, body: { detail: refusal } });
    const client = renderWithClient();

    await fillAndSubmit(user, { current: CURRENT_PASSWORD, next: NEW_PASSWORD });

    // The rejection still reaches the operator -- the half a same-tick hook-level
    // reset destroys, as the control above demonstrates.
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // Both fields, on the failure path too.
    await vi.waitFor(() => expect(sweepMutationCache(client)).not.toContain(CURRENT_PASSWORD));
    expect(sweepMutationCache(client)).not.toContain(NEW_PASSWORD);

    expect(sweepBrowserStorage()).not.toContain(CURRENT_PASSWORD);
    expect(sweepBrowserStorage()).not.toContain(NEW_PASSWORD);
  }, TEST_TIMEOUT_MS);

  it('clears the fields on both paths so neither password stays in the DOM', async () => {
    const user = userEvent.setup();
    const api = mockAccountApi({ status: 401, body: { detail: 'The current password is incorrect.' } });
    renderWithClient();

    await fillAndSubmit(user, { current: CURRENT_PASSWORD, next: NEW_PASSWORD });
    expect(await screen.findByText(/current password is incorrect/i)).toBeInTheDocument();

    // The failure path clears too -- otherwise the typed values sit in the DOM
    // for as long as the tab is open, which is the copy that outlives the request.
    expect(screen.getByLabelText('Current password')).toHaveValue('');
    expect(screen.getByLabelText('New password')).toHaveValue('');
    expect(screen.getByLabelText('Confirm new password')).toHaveValue('');

    api.set({ status: 204 });
    await fillAndSubmit(user, { current: CURRENT_PASSWORD, next: NEW_PASSWORD });
    expect(await screen.findByText('Saved.')).toBeInTheDocument();

    expect(screen.getByLabelText('Current password')).toHaveValue('');
    expect(screen.getByLabelText('New password')).toHaveValue('');
    expect(screen.getByLabelText('Confirm new password')).toHaveValue('');
  }, TEST_TIMEOUT_MS);
});
