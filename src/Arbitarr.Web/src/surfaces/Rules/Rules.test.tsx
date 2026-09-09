import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import RulesPage from './Rules';
import { ADMIN_KEY_HEADER } from '../../api/client';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { SERVER_KEY_UNSET_MESSAGE } from '../QueryState';
import { mockApi, type CapturedCall, type MockApi } from '../../test/mockApi';
import { renderSurface } from '../../test/renderSurface';

const rules = [
  { id: 1, name: 'block-cam', isAllow: false, pattern: 'CAM|TS', precedence: 10, enabled: true },
  { id: 2, name: 'allow-1080p', isAllow: true, pattern: '1080p', precedence: 20, enabled: true },
];

/**
 * Waits for the request a click was supposed to send, and returns it.
 *
 * arb-kmp: `user.click` resolves when React has flushed the click, NOT when the
 * mutation it starts has reached fetch -- react-query dispatches that a tick or
 * more later. Reading `api.calls` synchronously straight after the click
 * therefore races the request: it passed on an unloaded machine because the
 * gap is normally sub-millisecond, and failed under full-suite load, which is
 * exactly the intermittency this file was reported for.
 *
 * `waitFor` polls the recorded calls instead, so the assertion waits for the
 * state it is about rather than for a duration. It is NOT a raised timeout: a
 * request that never goes out still fails, just with "expected a POST" rather
 * than a null-dereference on the line below.
 */
function findCall(api: MockApi, method: string): Promise<CapturedCall> {
  return waitFor(() => {
    const call = api.calls.find((c) => c.method === method);
    expect(call, `expected a ${method} to have been sent`).toBeDefined();
    return call!;
  });
}

describe('Rules', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('renders its title and the rule list', async () => {
    mockApi({ '/api/admin/rules': { body: rules } });
    renderSurface(<RulesPage />);

    expect(screen.getByRole('heading', { level: 1, name: 'Rules' })).toBeInTheDocument();
    expect(await screen.findByText('block-cam')).toBeInTheDocument();
    expect(screen.getByText('CAM|TS')).toBeInTheDocument();
    expect(screen.getByText('allow-1080p')).toBeInTheDocument();
  });

  it('tells the operator what a rule does when none are defined yet', async () => {
    mockApi({ '/api/admin/rules': { body: [] } });
    renderSurface(<RulesPage />);

    expect(
      await screen.findByText('No rules defined. Add one below to allow or deny releases matching a pattern.'),
    ).toBeInTheDocument();
  });

  it('creates, edits and deletes a rule', async () => {
    const user = userEvent.setup();
    const api = mockApi({ '/api/admin/rules': { body: rules } });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // Create.
    const addPanel = screen.getByRole('heading', { name: 'Add rule' }).closest('section')!;
    await user.type(within(addPanel).getByLabelText('Name'), 'block-dv');
    await user.type(within(addPanel).getByLabelText('Pattern'), 'DV');
    await user.click(within(addPanel).getByRole('button', { name: 'Add rule' }));

    const posted = await findCall(api, 'POST');
    expect(posted.path).toBe('/api/admin/rules');
    expect(JSON.parse(posted.body!)).toMatchObject({ name: 'block-dv', pattern: 'DV' });

    // Edit -- the PUT the legacy admin-rules.js never wired at all, which meant
    // changing a pattern required deleting the rule and re-adding it.
    await user.click(screen.getAllByRole('button', { name: 'Edit' })[0]);
    const editPanel = screen.getByRole('heading', { name: 'Edit rule' }).closest('section')!;
    const patternField = within(editPanel).getByLabelText('Pattern');
    await user.clear(patternField);
    await user.type(patternField, 'CAM');
    await user.click(within(editPanel).getByRole('button', { name: 'Save changes' }));

    const put = await findCall(api, 'PUT');
    expect(put.path).toBe('/api/admin/rules/1');
    expect(JSON.parse(put.body!)).toMatchObject({ name: 'block-cam', pattern: 'CAM' });

    // Delete.
    await user.click(screen.getAllByRole('button', { name: 'Delete' })[0]);
    const del = await findCall(api, 'DELETE');
    expect(del.path).toBe('/api/admin/rules/1');
  });

  it('renders the server rejection verbatim and clamps nothing', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      '/api/admin/rules': { body: rules },
      // A precedence the server refuses. The client must not pre-empt this
      // check, adjust the value, or paraphrase the reason.
      '/api/admin/rules/1': { status: 400, body: { error: 'Precedence must be between 1 and 1000.' } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    await user.click(screen.getAllByRole('button', { name: 'Edit' })[0]);
    const editPanel = screen.getByRole('heading', { name: 'Edit rule' }).closest('section')!;
    const precedence = within(editPanel).getByLabelText('Precedence');
    await user.clear(precedence);
    await user.type(precedence, '99999');
    await user.click(within(editPanel).getByRole('button', { name: 'Save changes' }));

    expect(await screen.findByText('Precedence must be between 1 and 1000.')).toBeInTheDocument();
    // The request went out unaltered: the server is the only authority on the
    // bound, so 99999 must reach it rather than being silently reduced to 1000.
    expect(JSON.parse((await findCall(api, 'PUT')).body!).precedence).toBe(99999);
    // And the editor keeps what was typed, so it can be corrected.
    expect(precedence).toHaveValue(99999);
  });

  it('attaches the admin key to its reads and its writes', async () => {
    const user = userEvent.setup();
    useAdminKeyStore.getState().setKey('operator-key');
    const api = mockApi({ '/api/admin/rules': { body: rules } });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    await user.click(screen.getAllByRole('button', { name: 'Delete' })[0]);

    // Waits for the DELETE itself, not for 'block-cam'. The old wait here was
    // `findByText('block-cam')`, which was already on screen from the initial
    // load and so resolved on its first poll without the delete having been
    // sent -- it read as a wait but settled nothing, leaving the assertion
    // below racing the request (arb-kmp).
    const del = await findCall(api, 'DELETE');
    const get = await findCall(api, 'GET');
    expect(get.headers[ADMIN_KEY_HEADER]).toBe('operator-key');
    expect(del.headers[ADMIN_KEY_HEADER]).toBe('operator-key');
  });

  it('keeps the affordance and the stored key on a 503 fresh install', async () => {
    useAdminKeyStore.getState().setKey('operator-key');
    mockApi({ '/api/admin/rules': { status: 503, body: { error: 'admin key not configured' } } });
    renderSurface(<RulesPage />);

    expect(await screen.findByText(SERVER_KEY_UNSET_MESSAGE)).toBeInTheDocument();
    // AC6-503: the add form is still there and no key prompt appears -- the
    // operator's key is not what is wrong.
    expect(screen.getByRole('button', { name: 'Add rule' })).toBeInTheDocument();
    expect(useAdminKeyStore.getState().key).toBe('operator-key');
    expect(useAdminKeyStore.getState().serverKeyUnset).toBe(true);
    expect(screen.queryByLabelText(/admin api key/i)).toBeNull();
  });
});
