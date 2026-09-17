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
 * Returns the FIRST recorded call of that method, not the most recent one. Where
 * a surface sends the same method more than once -- the post-delete refetch below
 * follows the initial load's GET -- this resolves to the earlier call, which was
 * already satisfied and so is not a wait for the later one. Assert on a method
 * the click is the only source of, or read api.calls directly.
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

    // Delete: asking is not doing, so the click that names the row must be
    // followed by a Confirm before anything reaches the server.
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));
    const del = await findCall(api, 'DELETE');
    expect(del.path).toBe('/api/admin/rules/1');
  });

  // arb-d66: the field's width used to come from a `.testTitleField` literal;
  // it now relies on the shared `.input` convention instead. This exercises
  // the behaviour that literal was never actually needed for -- the field
  // renders, is labelled, and accepts input that reaches the request.
  it('renders the "Test a rule" field and sends the typed title', async () => {
    const user = userEvent.setup();
    const api = mockApi({
      '/api/admin/rules': { body: rules },
      '/api/admin/rules/test': { body: { verdict: 'Denied by block-cam' } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    const testPanel = screen.getByRole('heading', { name: 'Test a rule' }).closest('section')!;
    const titleField = within(testPanel).getByLabelText('Release title');
    await user.type(titleField, 'Some.Movie.2024.CAM.x264');
    await user.click(within(testPanel).getByRole('button', { name: 'Test' }));

    const posted = await findCall(api, 'POST');
    expect(posted.path).toBe('/api/admin/rules/test');
    expect(JSON.parse(posted.body!)).toMatchObject({ title: 'Some.Movie.2024.CAM.x264' });
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

    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));

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

  it('does not delete on one click, names the rule, and lets Cancel restore the row', async () => {
    const user = userEvent.setup();
    const api = mockApi({ '/api/admin/rules': { body: rules } });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));

    // The prompt names the rule.
    expect(screen.getByText('Delete “block-cam”?')).toBeInTheDocument();
    // Asking is not doing: a single click must not have reached the server.
    expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(0);

    await user.click(screen.getByRole('button', { name: 'Cancel delete block-cam' }));

    // Cancel restores the row: the confirm is gone, the plain Delete is back,
    // and still nothing was sent.
    expect(screen.queryByText('Delete “block-cam”?')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Delete block-cam' })).toBeInTheDocument();
    expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(0);

    // Positive control: the same sequence with Confirm instead DOES reach the
    // server, proving the assertions above are not vacuous.
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));
    const del = await findCall(api, 'DELETE');
    expect(del.path).toBe('/api/admin/rules/1');
  });

  it('disables only the row whose delete is in flight, leaving the others operable', async () => {
    const user = userEvent.setup();
    const threeRules = [
      ...rules,
      { id: 3, name: 'deny-x265', isAllow: false, pattern: 'x265', precedence: 30, enabled: true },
    ];
    mockApi({ '/api/admin/rules': { body: threeRules } });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // Never resolves within the test, so the row stays "in flight" the whole
    // time the assertions below run.
    vi.mocked(fetch).mockImplementationOnce(() => new Promise(() => {}));

    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));

    // Positive control first: the in-flight row's own Confirm IS disabled.
    expect(screen.getByRole('button', { name: 'Confirm delete block-cam' })).toBeDisabled();

    // THEN the other two rows' Delete buttons are not — each asserted per row,
    // not as "some row is enabled", which would pass even if every row were
    // disabled together.
    expect(screen.getByRole('button', { name: 'Delete allow-1080p' })).not.toBeDisabled();
    expect(screen.getByRole('button', { name: 'Delete deny-x265' })).not.toBeDisabled();
  });

  it('tracks two concurrent deletes independently, each clearing on its own settle', async () => {
    // The comment above `disabled={pendingDeleteIds.has(rule.id)}` states the
    // real invariant: `confirmingId` being section-wide means only one row's
    // Confirm is ever RENDERED at once, but nothing stops the operator moving
    // it to a different row while an earlier delete is still in flight, so two
    // deletes CAN run at the same time. `remove.variables` could only ever
    // name the latest one; `pendingDeleteIds` is a Set so it has to be proven
    // per row, with two calls open at once, not just for a single in-flight id.
    const user = userEvent.setup();
    const threeRules = [
      ...rules,
      { id: 3, name: 'deny-x265', isAllow: false, pattern: 'x265', precedence: 30, enabled: true },
    ];
    mockApi({ '/api/admin/rules': { body: threeRules } });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // POSITIVE CONTROL: before either delete starts, no row reads pending --
    // so the assertions below detect the Set gaining/losing entries rather
    // than a `disabled` that is unconditionally true.
    expect(screen.getByRole('button', { name: 'Delete block-cam' })).not.toBeDisabled();

    // Row A's ("block-cam") delete is held open indefinitely; row B's
    // ("allow-1080p") gets its own controllable promise so it can be settled
    // independently, after A is proven to still be pending.
    let resolveB: (() => void) | undefined;
    vi.mocked(fetch)
      .mockImplementationOnce(() => new Promise(() => {}))
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveB = () =>
              resolve(new Response(null, { status: 204 }));
          }),
      );

    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Confirm delete block-cam' })).toBeDisabled();
    });

    // `confirmingId` moves to row B once its Delete is clicked, which is the
    // section-wide half of the invariant: row A's Confirm/Cancel UI is no
    // longer RENDERED. That does not mean row A's delete stopped, which is
    // exactly the gap `pendingDeleteIds` closes -- proven below once row A's
    // row is back on screen.
    await user.click(screen.getByRole('button', { name: 'Delete allow-1080p' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete allow-1080p' }));

    // PER-ROW, with both in flight at once: row B reads pending, and untouched
    // row C ("deny-x265") does not -- its Delete button stays fully operable
    // rather than a section-wide flag catching it too.
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Confirm delete allow-1080p' })).toBeDisabled();
    });
    expect(screen.getByRole('button', { name: 'Delete deny-x265' })).not.toBeDisabled();

    // Cancel back to row A to read its own state: STILL disabled, because its
    // delete is still in flight -- `pendingDeleteIds` kept it, even though
    // `confirmingId` moved away and back. A single `remove.variables` check
    // could never show this: by now `variables` names row B's call, not row A's.
    await user.click(screen.getByRole('button', { name: 'Cancel delete allow-1080p' }));
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    expect(screen.getByRole('button', { name: 'Confirm delete block-cam' })).toBeDisabled();

    // Settle B only, with its delete succeeding. Row B leaves the confirming
    // state on success -- proof its own pending entry cleared -- while row A's
    // delete is still outstanding: its promise never resolved.
    resolveB?.();
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Delete allow-1080p' })).toBeInTheDocument();
    });
  });

  it('does not carry a rejected save on one rule into another rule\'s editor', async () => {
    // Finding 2: update.error used to survive startEditing/Cancel, so a
    // rejected save on rule 1 followed by Cancel and Edit on rule 2 rendered
    // rule 1's refusal inside rule 2's form. Positive control first: the
    // refusal IS shown in rule 1's own editor, proving the search below would
    // catch it if it leaked; then it must be absent once rule 2 is opened.
    const user = userEvent.setup();
    const refusal = 'Precedence must be between 1 and 1000.';
    const api = mockApi({
      '/api/admin/rules': { body: rules },
      '/api/admin/rules/1': { status: 400, body: { error: refusal } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // Reject a save on rule 1 ("block-cam").
    await user.click(screen.getAllByRole('button', { name: 'Edit' })[0]);
    let editPanel = screen.getByRole('heading', { name: 'Edit rule' }).closest('section')!;
    const precedence = within(editPanel).getByLabelText('Precedence');
    await user.clear(precedence);
    await user.type(precedence, '99999');
    await user.click(within(editPanel).getByRole('button', { name: 'Save changes' }));

    // POSITIVE CONTROL: the refusal really is shown, in rule 1's own editor.
    expect(await screen.findByText(refusal)).toBeInTheDocument();

    // Cancel out, then open rule 2 ("allow-1080p").
    await user.click(within(editPanel).getByRole('button', { name: 'Cancel' }));
    await user.click(screen.getAllByRole('button', { name: 'Edit' })[1]);
    editPanel = screen.getByRole('heading', { name: 'Edit rule' }).closest('section')!;

    // Rule 1's refusal must not follow the operator into rule 2's form.
    expect(within(editPanel).queryByText(refusal)).not.toBeInTheDocument();
    expect(api.calls.filter((call) => call.method === 'PUT')).toHaveLength(1);
  });

  it('renders a refused delete in its own row, and nowhere else', async () => {
    const user = userEvent.setup();
    const threeRules = [
      ...rules,
      { id: 3, name: 'deny-x265', isAllow: false, pattern: 'x265', precedence: 30, enabled: true },
    ];
    const refusal = 'This rule is referenced by an active dry run and cannot be deleted.';
    mockApi({
      '/api/admin/rules': { body: threeRules },
      '/api/admin/rules/1': { status: 400, body: { error: refusal } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));

    // Positive control first: the message DOES land, in the refused row.
    const rowMessage = await screen.findByText(refusal);
    expect(rowMessage).toBeInTheDocument();
    expect(rowMessage).toHaveAttribute('role', 'alert');
    const row = rowMessage.closest('tr')!;
    expect(within(row).getByText('block-cam')).toBeInTheDocument();

    // THEN confirm it is absent from the sibling rows and from above the
    // table: exactly one role="alert" exists, and it is the row's own.
    expect(screen.getAllByText(refusal)).toHaveLength(1);
    const otherRow = screen.getByText('allow-1080p').closest('tr')!;
    expect(within(otherRow).queryByText(refusal)).not.toBeInTheDocument();
  });

  it('keeps two concurrent refusals on their own rows, and clears only the row that retries (arb-39g3)', async () => {
    // arb-39g3: deleteFailedId/deleteFailure used to be a single shared
    // scalar, so a second refusal in flight overwrote the first row's text --
    // row A's message vanished or appeared under row B. This drives two
    // refusals unresolved AT THE SAME TIME, then a third row untouched
    // throughout, so the fix has to be proven per row rather than "some row
    // shows a message".
    const user = userEvent.setup();
    const threeRules = [
      ...rules,
      { id: 3, name: 'deny-x265', isAllow: false, pattern: 'x265', precedence: 30, enabled: true },
    ];
    const refusalA = 'Rule A refusal: referenced by an active dry run.';
    const refusalB = 'Rule B refusal: precedence collides with another enabled rule.';
    const api = mockApi({
      '/api/admin/rules': { body: threeRules },
      '/api/admin/rules/1': { status: 400, body: { error: refusalA } },
      '/api/admin/rules/2': { status: 400, body: { error: refusalB } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // Refuse A ("block-cam") and B ("allow-1080p"), both left unresolved.
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));
    await findCall(api, 'DELETE');

    await user.click(screen.getByRole('button', { name: 'Delete allow-1080p' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete allow-1080p' }));
    await waitFor(() => {
      expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(2);
    });

    // POSITIVE CONTROLS first: each row's own message really is there.
    const rowA = screen.getByText('block-cam').closest('tr')!;
    const rowB = screen.getByText('allow-1080p').closest('tr')!;
    const rowC = screen.getByText('deny-x265').closest('tr')!;
    await waitFor(() => {
      expect(within(rowA).getByText(refusalA)).toBeInTheDocument();
    });
    await waitFor(() => {
      expect(within(rowB).getByText(refusalB)).toBeInTheDocument();
    });

    // THEN neither message leaks into the other rows or above the table.
    expect(within(rowB).queryByText(refusalA)).not.toBeInTheDocument();
    expect(within(rowC).queryByText(refusalA)).not.toBeInTheDocument();
    expect(within(rowA).queryByText(refusalB)).not.toBeInTheDocument();
    expect(within(rowC).queryByText(refusalB)).not.toBeInTheDocument();
    expect(screen.getAllByText(refusalA)).toHaveLength(1);
    expect(screen.getAllByText(refusalB)).toHaveLength(1);

    // Retry A successfully: its own refusal clears, B's stays exactly as it was.
    api.set('/api/admin/rules/1', { status: 204 });
    api.set('/api/admin/rules', { body: threeRules.filter((rule) => rule.id !== 1) });
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));

    await waitFor(() => {
      expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    });
    // B's refusal is untouched by A's retry -- another row's success must not
    // clear a refusal it did not own.
    expect(screen.getByText(refusalB)).toBeInTheDocument();
  });

  it('prunes a refusal once its row leaves the list, but keeps it for a row that stays (arb-gn4z)', async () => {
    // Two rows are refused (A and B). A then leaves the list on a refetch a
    // THIRD row's action triggers -- not A's own retry, which is already
    // covered above -- and later returns under the SAME id via a later fetch.
    // The stale refusal must not resurface. B never leaves, so its refusal
    // must survive every refetch untouched.
    const user = userEvent.setup();
    const threeRules = [
      ...rules,
      { id: 3, name: 'deny-x265', isAllow: false, pattern: 'x265', precedence: 30, enabled: true },
    ];
    const refusalA = 'Rule A refusal: referenced by an active dry run.';
    const refusalB = 'Rule B refusal: precedence collides with another enabled rule.';
    const api = mockApi({
      '/api/admin/rules': { body: threeRules },
      '/api/admin/rules/1': { status: 400, body: { error: refusalA } },
      '/api/admin/rules/2': { status: 400, body: { error: refusalB } },
    });
    renderSurface(<RulesPage />);
    await screen.findByText('block-cam');

    // Refuse A and B, both left unresolved.
    await user.click(screen.getByRole('button', { name: 'Delete block-cam' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete block-cam' }));
    await findCall(api, 'DELETE');

    await user.click(screen.getByRole('button', { name: 'Delete allow-1080p' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete allow-1080p' }));
    await waitFor(() => {
      expect(api.calls.filter((call) => call.method === 'DELETE')).toHaveLength(2);
    });

    // POSITIVE CONTROL: A's refusal really is shown before the row vanishes.
    await waitFor(() => {
      expect(screen.getByText(refusalA)).toBeInTheDocument();
    });
    expect(screen.getByText(refusalB)).toBeInTheDocument();

    // Row C's own delete succeeds and its refetch's response no longer
    // includes row A at all -- the row left the list through an action that
    // has nothing to do with A's own refusal or retry.
    api.set('/api/admin/rules/3', { status: 204 });
    api.set('/api/admin/rules', { body: threeRules.filter((rule) => rule.id !== 1 && rule.id !== 3) });
    await user.click(screen.getByRole('button', { name: 'Delete deny-x265' }));
    await user.click(screen.getByRole('button', { name: 'Confirm delete deny-x265' }));

    await waitFor(() => {
      expect(screen.queryByText('deny-x265')).not.toBeInTheDocument();
    });
    // A's row is also gone, and so is its refusal text, pruned rather than
    // merely unrendered because nothing keys back to a row anymore.
    expect(screen.queryByText('block-cam')).not.toBeInTheDocument();
    expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    // B never left: its refusal survives this refetch untouched.
    expect(screen.getByText(refusalB)).toBeInTheDocument();

    // Row A returns under the SAME id (1) via a later fetch -- a rule
    // recreated by another admin session, say. A NEW rule create is used to
    // cause the invalidation (a mutation whose own success is unrelated to
    // A or B), so B's still-unresolved refusal is untouched by anything
    // this step does directly.
    api.set('/api/admin/rules', {
      body: [
        { id: 1, name: 'block-cam', isAllow: false, pattern: 'CAM|TS', precedence: 10, enabled: true },
        threeRules[1],
        { id: 4, name: 'allow-remux', isAllow: true, pattern: 'REMUX', precedence: 40, enabled: true },
      ],
    });
    const addPanel = screen.getByRole('heading', { name: 'Add rule' }).closest('section')!;
    await user.type(within(addPanel).getByLabelText('Name'), 'allow-remux');
    await user.type(within(addPanel).getByLabelText('Pattern'), 'REMUX');
    await user.click(within(addPanel).getByRole('button', { name: 'Add rule' }));

    await waitFor(() => {
      expect(screen.getByText('block-cam')).toBeInTheDocument();
    });
    // The old refusal for id 1 must NOT resurface just because the id is back.
    expect(screen.queryByText(refusalA)).not.toBeInTheDocument();
    // B's own, still-unresolved refusal is unaffected by A's return.
    expect(screen.getByText(refusalB)).toBeInTheDocument();
  });
});
