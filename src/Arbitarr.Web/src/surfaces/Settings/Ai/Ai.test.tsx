import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AiSection } from './Ai';
import { useAdminKeyStore } from '../../../state/adminKeyStore';
import { mockApi } from '../../../test/mockApi';
import { renderSurface } from '../../../test/renderSurface';

const ROUTE = '/api/admin/ai/ollama';
const TEST_ROUTE = '/api/admin/ai/ollama/test';

/** RFC 5737 TEST-NET-1: non-routable, and no real address enters committed content. */
const CONFIGURED = { baseUrl: 'http://192.0.2.10:11434', model: 'qwen2.5:7b-instruct-q4_K_M' };

/**
 * A probe result. `models` defaults to empty, matching the server: an instance
 * with nothing pulled answers Ok with no names, and every failing outcome carries
 * an empty list because there was no model list to read.
 */
function probe(
  outcome: string,
  message: string,
  models: string[] = [],
): { success: boolean; outcome: string; message: string; models: string[] } {
  return { success: outcome === 'Ok', outcome, message, models };
}

/** The body of the last PUT the page sent, parsed. */
function lastPutBody(api: ReturnType<typeof mockApi>): Record<string, unknown> {
  const puts = api.calls.filter((call) => call.method === 'PUT');
  expect(puts.length).toBeGreaterThan(0);
  return JSON.parse(puts[puts.length - 1]!.body!) as Record<string, unknown>;
}

describe('AI backend section', () => {
  beforeEach(() => {
    useAdminKeyStore.setState({ key: null, serverKeyUnset: false });
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * The address is SHOWN, not hidden behind a "replace it" field. That is the
   * deliberate difference from the webhook and source-key fields on this same
   * surface: this value is not a credential, so making the operator retype it
   * blind would defend nothing and cost them the ability to check it.
   */
  it('shows the stored base URL in an editable text field', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    const field = await screen.findByLabelText('Ollama base URL');
    expect(field).toHaveValue('http://192.0.2.10:11434');
    // Not a password field: masking a non-secret only stops the operator reading it.
    expect(field).toHaveAttribute('type', 'text');
  });

  it('states that a change needs no restart', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    expect(await screen.findByText(/no restart/i)).toBeInTheDocument();
  });

  it('sends the edited URL and reports the save', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    const field = await screen.findByLabelText('Ollama base URL');
    await userEvent.clear(field);
    await userEvent.type(field, 'http://192.0.2.20:11434');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    // The model rides along unchanged: one Save, one PUT, and the server applies
    // both or neither. Sending only the address would work against the server
    // (the field is optional) but would make a model edit need a second click.
    await waitFor(() =>
      expect(lastPutBody(api)).toEqual({
        baseUrl: 'http://192.0.2.20:11434',
        model: 'qwen2.5:7b-instruct-q4_K_M',
      }),
    );
    expect(await screen.findByText('Saved.')).toBeInTheDocument();
  });

  /**
   * The server rejects and never clamps, and its exact words are what the
   * operator reads. This also pins that the rejection SURVIVES on screen: it is
   * captured from the per-call `onError` into component state owned above the
   * remount boundary, so it is not blanked a tick after it appears.
   */
  it('renders the server rejection verbatim rather than a guess of its own', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    const field = await screen.findByLabelText('Ollama base URL');
    api.set(ROUTE, {
      status: 400,
      body: { error: "'ftp://ollama.example.com' is not a valid absolute http(s) URL." },
    });

    await userEvent.clear(field);
    await userEvent.type(field, 'ftp://ollama.example.com');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/is not a valid absolute http\(s\) URL/);
    expect(screen.queryByText('Saved.')).not.toBeInTheDocument();
  });

  /**
   * No client-side validation in front of the server. A guard here would either
   * block a value the server accepts or invent a rejection it never issued, so
   * even an obviously-bad value is still SENT and the server's answer is what is
   * shown.
   */
  it('sends an invalid value rather than blocking it locally', async () => {
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    const field = await screen.findByLabelText('Ollama base URL');
    api.set(ROUTE, { status: 400, body: { error: 'rejected' } });

    await userEvent.clear(field);
    await userEvent.type(field, 'not-a-url');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(lastPutBody(api)).toEqual({
        baseUrl: 'not-a-url',
        model: 'qwen2.5:7b-instruct-q4_K_M',
      }),
    );
  });

  it('posts no body when testing the connection', async () => {
    const api = mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe('Ok', 'Connected successfully.') },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    await waitFor(() => expect(api.callsTo(TEST_ROUTE).length).toBe(1));
    // The address under test is the STORED one, read server-side: there is
    // nothing for the client to submit, which is also why no body is sent.
    expect(api.callsTo(TEST_ROUTE)[0]!.body).toBeUndefined();
  });

  it('reports a successful probe with the server wording', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: {
        body: probe('Ok', 'Connected successfully and Ollama answered with its model list.'),
      },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(await screen.findByText('Connected')).toBeInTheDocument();
    expect(screen.getByText(/answered with its model list/)).toBeInTheDocument();
  });

  /**
   * <b>THE DISTINCTNESS ASSERTION.</b> Every outcome must render a DIFFERENT
   * label, because one red "failed" makes the button decorative — a wrong port,
   * a bad certificate and a base URL pointing at another service are three
   * different fixes.
   *
   * Asserted by COUNT rather than case by case: collapsing two outcomes into one
   * label would still pass a per-case test whose expectations were updated to
   * match, whereas counting the distinct labels makes the collapse itself the
   * failure.
   */
  it('renders four distinct labels, one per outcome', async () => {
    const outcomes = ['Ok', 'Unreachable', 'TlsFailure', 'UnexpectedResponse'];
    const labels: string[] = [];

    for (const outcome of outcomes) {
      const api = mockApi({
        [ROUTE]: { body: CONFIGURED },
        [TEST_ROUTE]: { body: probe(outcome, `wording for ${outcome}`) },
      });
      const view = renderSurface(<AiSection />);

      await screen.findByLabelText('Ollama base URL');
      await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));
      await screen.findByText(`wording for ${outcome}`);

      // The short badge, read from the outcome head beside the server's wording.
      const badge = view.container.querySelector('[class*="testHead"] span');
      expect(badge).not.toBeNull();
      labels.push(badge!.textContent!);

      view.unmount();
      expect(api.callsTo(TEST_ROUTE).length).toBe(1);
      vi.unstubAllGlobals();
    }

    expect(labels).toHaveLength(outcomes.length);
    expect(new Set(labels).size).toBe(outcomes.length);
  });

  /**
   * A failed REQUEST is not a probe outcome and must not be dressed as one: a
   * rejected admin key or a server that is down says nothing about Ollama, so no
   * outcome badge is shown at all.
   */
  it('distinguishes a failed request from a probe verdict', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { status: 401, body: { error: 'Admin API key required.' } },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    // `apiFetch` substitutes its own wording for a 401 — the server's text would
    // tell the operator nothing they can act on, whereas "enter it again" names
    // the fix. Asserted as the client renders it, not as the server sent it.
    expect(await screen.findByRole('alert')).toHaveTextContent(/admin API key was rejected/i);
    // None of the outcome labels: this was not a verdict about the backend.
    expect(screen.queryByText('Unreachable')).not.toBeInTheDocument();
    expect(screen.queryByText('Not Ollama')).not.toBeInTheDocument();
    expect(screen.queryByText('Connected')).not.toBeInTheDocument();
  });

  /**
   * The probe checks the STORED address, not what happens to be in the field —
   * so the page has to say so, or a passing test after an unsaved edit reads as
   * approving the edit.
   */
  it('says the test checks the saved address', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    expect(screen.getByText(/save an edit before testing it/i)).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------------
  // #112: the model picker.
  // -----------------------------------------------------------------------------

  /**
   * Before any test has run there is nothing to choose FROM, so the stored value
   * is shown as text. A one-option select would look like a choice and offer
   * none, which reads as the page being broken.
   */
  it('shows the stored model as text before any test has run', async () => {
    mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    const model = await screen.findByLabelText('Ollama model');
    expect(model).toHaveTextContent('qwen2.5:7b-instruct-q4_K_M');
    // Specifically NOT a select: there is nothing to pick from yet.
    expect(model.tagName).not.toBe('SELECT');
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.getByText(/Run the connection test to choose/i)).toBeInTheDocument();
  });

  /**
   * <b>THE PICKER.</b> After a successful test the names the server reported
   * become the options — the whole point of #112: an operator can no longer name
   * a model their instance has never pulled, because the list is the instance's
   * own.
   */
  it('offers the reported models in a select after a successful test', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: {
        body: probe('Ok', 'Connected successfully.', [
          'qwen2.5:7b-instruct-q4_K_M',
          'llama3.1:8b',
          'phi4:14b',
        ]),
      },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    const select = await screen.findByRole('combobox', { name: 'Ollama model' });
    expect(select).toHaveValue('qwen2.5:7b-instruct-q4_K_M');
    expect(
      Array.from(select.querySelectorAll('option')).map((option) => option.textContent),
    ).toEqual(['qwen2.5:7b-instruct-q4_K_M', 'llama3.1:8b', 'phi4:14b']);
  });

  /** Choosing a model and saving stores it, in the same PUT as the address. */
  it('sends the chosen model when saving', async () => {
    const api = mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: {
        body: probe('Ok', 'Connected successfully.', ['qwen2.5:7b-instruct-q4_K_M', 'phi4:14b']),
      },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    const select = await screen.findByRole('combobox', { name: 'Ollama model' });
    await userEvent.selectOptions(select, 'phi4:14b');
    await userEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(lastPutBody(api)).toEqual({
        baseUrl: 'http://192.0.2.10:11434',
        model: 'phi4:14b',
      }),
    );
  });

  /**
   * The stored model stays selectable even when the probe did not list it — a
   * model pulled and then removed, or a name seeded from configuration. Dropping
   * it would silently change what is saved the moment the operator touches Save,
   * which is a change they never asked for.
   */
  it('keeps the stored model selectable when the instance no longer reports it', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe('Ok', 'Connected successfully.', ['llama3.1:8b', 'phi4:14b']) },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    const select = await screen.findByRole('combobox', { name: 'Ollama model' });
    // Still the stored value, and it is still an option rather than having been
    // silently replaced by whatever happened to be first in the reported list.
    expect(select).toHaveValue('qwen2.5:7b-instruct-q4_K_M');
    expect(
      Array.from(select.querySelectorAll('option')).map((option) => option.textContent),
    ).toEqual(['qwen2.5:7b-instruct-q4_K_M', 'llama3.1:8b', 'phi4:14b']);
  });

  /**
   * An instance that has pulled nothing answers Ok with an EMPTY list. That is a
   * healthy instance, not a failure — the probe still reports Connected — but it
   * is not a list to pick from either, so the text rendering stays.
   */
  it('does not offer a picker when a successful test reports no models', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe('Ok', 'Connected successfully.', []) },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    // The probe DID succeed — so this test is about the empty list, not about a
    // failure being mistaken for one.
    expect(await screen.findByText('Connected')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Ollama model')).toHaveTextContent('qwen2.5:7b-instruct-q4_K_M');
  });

  /**
   * A FAILING probe reports no models, and must not produce a picker either. A
   * list built from a failed probe is exactly the "green tick, wrong model"
   * state #112 exists to end.
   */
  it('does not offer a picker when the probe failed', async () => {
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: { body: probe('Unreachable', 'Could not reach Ollama.') },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    expect(await screen.findByText('Unreachable')).toBeInTheDocument();
    expect(screen.queryByRole('combobox')).not.toBeInTheDocument();
  });

  /**
   * The model names are DATA, never prose. They appear as options and nowhere
   * else — in particular they are not spliced into the server's fixed wording,
   * which is what keeps the outcome union at four members and the message derived
   * from the closed enum alone.
   *
   * POSITIVE CONTROL: the same search is first shown to FIND the name where it
   * genuinely is (the select), so its absence from the message paragraph is a
   * property rather than a search that could never have matched.
   */
  it('renders the reported names only as options, never inside the server wording', async () => {
    const distinctive = 'placeholder-model-that-must-not-appear-in-prose';
    mockApi({
      [ROUTE]: { body: CONFIGURED },
      [TEST_ROUTE]: {
        body: probe('Ok', 'Connected successfully and Ollama answered with its model list.', [
          distinctive,
        ]),
      },
    });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    await userEvent.click(screen.getByRole('button', { name: 'Test connection' }));

    // Existence + detectability: the name really is on the page, in the select.
    const select = await screen.findByRole('combobox', { name: 'Ollama model' });
    expect(select.textContent).toContain(distinctive);

    // And it is absent from the server's wording, which is rendered as-is.
    const message = screen.getByText(/answered with its model list/);
    expect(message.textContent).not.toContain(distinctive);
  });

  it('attaches the admin key to its reads', async () => {
    useAdminKeyStore.setState({ key: 'placeholder-admin-key', serverKeyUnset: false });
    const api = mockApi({ [ROUTE]: { body: CONFIGURED } });
    renderSurface(<AiSection />);

    await screen.findByLabelText('Ollama base URL');
    expect(api.adminKeyOn(ROUTE)).toBe('placeholder-admin-key');
  });
});
