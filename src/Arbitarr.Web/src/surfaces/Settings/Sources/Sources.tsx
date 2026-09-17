import { useState } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import { useSecretEvictingMutation } from '../useSecretEvictingMutation';
import local from './Sources.module.css';
import {
  useCreateSourceMutation,
  useDeleteSourceMutation,
  useSourcesQuery,
  useTestSourceMutation,
  useUpdateSourceMutation,
} from './queries';
import {
  PROXY_ACCESS_MODE,
  REDIRECT_ACCESS_MODE,
  type CreateSourceRequest,
  type SourceSummary,
  type UpdateSourceRequest,
} from './types';

/**
 * The two NZB access modes the form offers, in the order they are shown — Proxy
 * first, because it is the default and the one that does not expose the indexer
 * key.
 *
 * SPELLED EXACTLY AS THE SERVER ACCEPTS THEM. `SourceRepository` matches this
 * value by exact ordinal name and rejects `'redirect'`, `'REDIRECT'`, `' 1 '`
 * and `'1'` alike, so a casing or whitespace "tidy-up" here turns every save
 * into a 400.
 */
const ACCESS_MODES = [PROXY_ACCESS_MODE, REDIRECT_ACCESS_MODE] as const;

/**
 * The operator-facing label for each mode. Each says what ARBITARR does, not
 * what the setting is called, because "Redirect" alone does not tell an operator
 * that a credential changes hands — the warning below the control carries the
 * consequence, and this carries the behaviour.
 */
const ACCESS_MODE_LABELS: Record<string, string> = {
  [PROXY_ACCESS_MODE]: 'Proxy — Arbitarr downloads the file and serves it',
  [REDIRECT_ACCESS_MODE]: 'Redirect — send the client to the indexer',
};

/**
 * The operator-facing label for each probe outcome, keyed by the server's
 * closed `SourceProbeOutcome` enum.
 *
 * FIVE DISTINCT LABELS, NOT ONE RED "FAILED" (§3.3/AC4). The four failure modes
 * have entirely different fixes — a wrong port, an expired certificate, a stale
 * key, and a base URL pointing at a reverse proxy are not the same problem — so
 * collapsing them into a single verdict makes the button decorative. The
 * server's longer `message` is rendered underneath and says what to check next;
 * this is the short badge that makes the five scannable at a glance.
 *
 * NONE OF THIS TEXT IS DERIVED FROM THE KEY, and none of it can be: the server's
 * enum carries no string field, so there is no upstream body, exception message,
 * or submitted credential anywhere in the path that produces it. Do not add a
 * free-text field to carry a server message through — that is the leak this
 * shape prevents.
 */
const OUTCOME_LABELS: Record<string, string> = {
  Ok: 'Connected',
  Unreachable: 'Unreachable',
  TlsFailure: 'TLS failure',
  AuthenticationFailed: 'API key rejected',
  UnexpectedResponse: 'Unexpected response',
};

/**
 * Falls back to the outcome string itself rather than to a generic "failed".
 *
 * If the server ever grows a sixth outcome, an operator seeing its raw enum name
 * still learns more than one seeing "Error" — and the fallback is safe to render
 * because the outcome is a closed enum name, never free text.
 */
const outcomeLabel = (outcome: string): string => OUTCOME_LABELS[outcome] ?? outcome;

/** The editor's own state. Every field is a string while it is being typed. */
interface SourceDraft {
  kind: string;
  displayName: string;
  baseUrl: string;
  enabled: boolean;
  /**
   * A REPLACEMENT for the stored key, never the stored key itself — there is no
   * stored value to seed this from, by design. Empty means "the operator typed
   * nothing", which the request builders translate into an ABSENT field.
   */
  apiKey: string;
  /**
   * `'Proxy'` or `'Redirect'`. Seeded from the stored value on the edit form and
   * from `PROXY_ACCESS_MODE` on the add form — never left empty, because an
   * empty string is a 400 at the server and, more importantly, because there is
   * no "unset" state an operator should be able to leave this in: the column
   * always holds one of the two.
   */
  nzbAccessMode: string;
}

const BLANK_DRAFT: SourceDraft = {
  kind: 'NzbHydra',
  displayName: '',
  baseUrl: '',
  enabled: true,
  apiKey: '',
  /**
   * PROXY, NOT REDIRECT, and this line is the UI half of the owner's ship-OFF
   * ruling. A new source must never expose its indexer key because the operator
   * added it and touched nothing else; opting in has to be a deliberate act.
   */
  nzbAccessMode: PROXY_ACCESS_MODE,
};

/**
 * Seeds the edit form from a source. Note that `apiKey` starts EMPTY and not
 * from any server value: `SourceSummary` has no field that could hold one, and
 * adding a masked or placeholder value here to "make the form feel complete"
 * would mean either inventing a value or round-tripping a real one through the
 * browser. Both are the thing the write-only contract exists to prevent.
 */
const draftOf = (source: SourceSummary): SourceDraft => ({
  kind: source.kind,
  displayName: source.displayName,
  baseUrl: source.baseUrl,
  enabled: source.enabled,
  apiKey: '',
  // Unlike apiKey, this DOES seed from the server value: it is not a secret, it
  // has no write-only contract, and showing the stored mode is the whole point
  // of putting the control on the edit form.
  nzbAccessMode: source.nzbAccessMode,
});

/**
 * The create body. `apiKey` is included only when something was typed, so a
 * source added without a key is created without one rather than with an empty
 * string masquerading as a credential.
 */
function toCreateRequest(draft: SourceDraft): CreateSourceRequest {
  const request: CreateSourceRequest = {
    kind: draft.kind,
    displayName: draft.displayName,
    baseUrl: draft.baseUrl,
    enabled: draft.enabled,
    // Always sent, unlike apiKey: the form showed the operator a mode, so the
    // save must mean it rather than leaning on the server default happening to
    // agree with the control's initial value.
    nzbAccessMode: draft.nzbAccessMode,
  };
  if (draft.apiKey !== '') {
    request.apiKey = draft.apiKey;
  }
  return request;
}

/**
 * The edit body, and the single most dangerous function in this file.
 *
 * `kind`, `displayName` and `baseUrl` are ALWAYS sent, even unchanged: omitting
 * one makes it `string.Empty` server-side and the update is then rejected as a
 * validation failure. `apiKey` is sent ONLY when the operator typed a
 * replacement, because for that one field omission means "leave the stored key
 * alone" and sending an empty string would blank a working credential on an
 * unrelated edit. Three fields where absence is fatal, one where presence is —
 * see `UpdateSourceRequest`'s doc for why the contract is shaped that way.
 *
 * `nzbAccessMode` is a FOURTH case and is always sent. Server-side its absence
 * means "leave the stored mode alone", so omitting it would be safe in the
 * `apiKey` sense — but the form always DISPLAYS a mode, and a displayed value
 * that a save does not carry is a value that can silently diverge from what is
 * stored. For this column that divergence is the difference between the indexer
 * key staying server-side and being handed to the client.
 */
function toUpdateRequest(draft: SourceDraft): UpdateSourceRequest {
  const request: UpdateSourceRequest = {
    kind: draft.kind,
    displayName: draft.displayName,
    baseUrl: draft.baseUrl,
    enabled: draft.enabled,
    nzbAccessMode: draft.nzbAccessMode,
  };
  if (draft.apiKey !== '') {
    request.apiKey = draft.apiKey;
  }
  return request;
}

function SourceForm({
  draft,
  onChange,
  onSubmit,
  onCancel,
  submitLabel,
  busy,
  idPrefix,
  hasApiKey,
}: {
  draft: SourceDraft;
  onChange: (draft: SourceDraft) => void;
  onSubmit: () => void;
  onCancel?: () => void;
  submitLabel: string;
  busy: boolean;
  /** Namespaces the field ids so the add and edit forms can coexist on the page. */
  idPrefix: string;
  /**
   * Whether a key is already stored, for the edit form's wording. `undefined` on
   * the add form, where there is nothing to replace.
   */
  hasApiKey?: boolean;
}) {
  const set = <K extends keyof SourceDraft>(name: K, value: SourceDraft[K]) =>
    onChange({ ...draft, [name]: value });

  const keyLabel =
    hasApiKey === true ? 'Replace API key' : hasApiKey === false ? 'Set API key' : 'API key';

  return (
    <form
      className={styles.form}
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit();
      }}
    >
      <label className={styles.field}>
        Kind
        <input
          className={styles.input}
          aria-label={`${idPrefix} kind`}
          value={draft.kind}
          onChange={(event) => set('kind', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Display name
        <input
          className={styles.input}
          aria-label={`${idPrefix} display name`}
          value={draft.displayName}
          onChange={(event) => set('displayName', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Base URL
        <input
          className={styles.input}
          aria-label={`${idPrefix} base URL`}
          value={draft.baseUrl}
          onChange={(event) => set('baseUrl', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        {keyLabel}
        <input
          // Password type so the value the operator types is not shoulder-read
          // and is not offered to a browser's plain-text autofill history. The
          // value lives in component state for the life of the form and is sent
          // to the server; it is never written to localStorage, sessionStorage,
          // a query string, or the query cache.
          type="password"
          className={styles.input}
          autoComplete="new-password"
          aria-label={`${idPrefix} API key`}
          value={draft.apiKey}
          onChange={(event) => set('apiKey', event.target.value)}
        />
      </label>
      {/*
        arb-x7w8.14 — THE EXPOSURE WARNING, AND IT IS A CO-REQUIREMENT OF THE
        SERVER ACCEPTING 'Redirect' AT ALL. `SourceRepository` refused this value
        outright until this control existed, because the owner's ruling is that
        the mode ships OFF and per-indexer opt-in AND that the operator is told
        what it costs. SettingsCatalog's discipline applied to a source column:
        an operator is never shown a bare, unexplained value.

        THE WARNING IS ASSOCIATED WITH THE CONTROL VIA aria-describedby, not
        merely placed next to it. A warning a screen reader never reaches while
        the select has focus is decoration; this one is announced with the
        control, which is what makes the opt-in informed for every operator
        rather than only the sighted ones.

        RENDERED ONLY WHEN Redirect IS SELECTED. A permanent warning beside a
        setting that is safe by default trains an operator to ignore it, and it
        would then be ignored at the one moment it matters.
      */}
      <label className={styles.field}>
        NZB access mode
        <select
          className={styles.input}
          aria-label={`${idPrefix} nzb access mode`}
          aria-describedby={
            draft.nzbAccessMode === REDIRECT_ACCESS_MODE ? `${idPrefix}-access-mode-warning` : undefined
          }
          value={draft.nzbAccessMode}
          onChange={(event) => set('nzbAccessMode', event.target.value)}
        >
          {ACCESS_MODES.map((mode) => (
            <option key={mode} value={mode}>
              {ACCESS_MODE_LABELS[mode] ?? mode}
            </option>
          ))}
        </select>
      </label>
      {draft.nzbAccessMode === REDIRECT_ACCESS_MODE && (
        <p id={`${idPrefix}-access-mode-warning`} className={local.accessModeWarning} role="note">
          This indexer&rsquo;s API key will be visible to Sonarr, Radarr and anything else that can
          read the download response: Arbitarr answers with a redirect to the indexer&rsquo;s own
          URL, and that URL contains the key. Redirect mode saves Arbitarr the download bandwidth.
          Choose Proxy to keep the key on the server.
        </p>
      )}
      <label className={styles.checkboxField}>
        <input
          type="checkbox"
          aria-label={`${idPrefix} enabled`}
          checked={draft.enabled}
          onChange={(event) => set('enabled', event.target.checked)}
        />
        Enabled
      </label>
      <button type="submit" className={styles.button} disabled={busy}>
        {submitLabel}
      </button>
      {onCancel !== undefined && (
        <button type="button" className={styles.buttonSecondary} onClick={onCancel}>
          Cancel
        </button>
      )}
    </form>
  );
}

/**
 * #53 stage 53d — the Sources section, mounted into the Settings surface.
 *
 * <h3>Why this is a Settings SECTION and not a seventh nav surface</h3>
 * A sidebar entry was considered and rejected. `SidebarNav.tsx` carries an AC5
 * comment stating the surface count is seven and accounting for why; adding
 * Sources would make it eight and would put configuration in two places, since
 * everything else an operator configures already lives behind Settings. Sources
 * are configuration in exactly the sense the Settings catalog below them is, so
 * they belong on the same page. THE NAV COUNT IS THEREFORE UNCHANGED AT SEVEN,
 * and `SidebarNav.tsx`'s comment and `SidebarNav.test.tsx`'s exact-count
 * assertion are both deliberately untouched by this change.
 *
 * <h3>Scope: which upstream addresses are editable here</h3>
 * The owner's instruction (2026-09-07) is that every configurable upstream
 * address is settable from the settings pages, with a test button. Enumerated
 * from `Program.cs`'s `GetSection` calls at the time of writing:
 *
 * - `Arbitarr:Sources:NzbHydra` — a source. COVERED here: it is seeded as a
 *   `Source` row, and every row on this list is editable with a test button.
 * - `Arbitarr:Ai:Ollama` — an address, but NOT a source. DELIBERATELY NOT HERE.
 *   It is an AI backend rather than an indexer: it is not searched, it does not
 *   appear in the sources health table, it carries no Torznab/Newznab API key,
 *   and the connectivity probe on this page speaks the source API, so a shared
 *   test button would report `UnexpectedResponse` against a perfectly healthy
 *   Ollama. Putting it in the sources list would make "source" mean two
 *   different things in the same table. It has its own section with its own
 *   probe -- `Settings/Ai/Ai.tsx` (#89), which speaks Ollama's `/api/tags` and
 *   reports its own four outcomes; it is not silently skipped.
 * - `Arbitarr:Ai` and `Arbitarr:ClientApiKeys` — no upstream address between
 *   them (model/feature settings and locally-minted credentials respectively),
 *   so nothing to surface.
 */
export function SourcesSection() {
  const sources = useSourcesQuery();
  const create = useCreateSourceMutation();
  const update = useUpdateSourceMutation();
  const remove = useDeleteSourceMutation();
  const test = useTestSourceMutation();

  const [newDraft, setNewDraft] = useState<SourceDraft>(BLANK_DRAFT);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editDraft, setEditDraft] = useState<SourceDraft>(BLANK_DRAFT);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  const [testedId, setTestedId] = useState<number | null>(null);
  /**
   * The last write rejection, held HERE rather than read off the mutation.
   *
   * The mutations reset() on settle so no apiKey-bearing `variables` linger in
   * the MutationCache (see queries.ts). Holding the message here rather than
   * reading `create.error` / `update.error` is what makes that reset free of
   * consequence: reset() clears mutation `error`, so a banner sourced from it
   * would blank at the same instant the credential does. Component state keeps
   * the server's exact words on screen while the cached key still goes away
   * immediately.
   */
  const [writeError, setWriteError] = useState<string | null>(null);

  const editing = sources.data?.find((source) => source.id === editingId);
  const tested = sources.data?.find((source) => source.id === testedId);

  const startEditing = (source: SourceSummary) => {
    setEditingId(source.id);
    setEditDraft(draftOf(source));
  };

  const { settle } = useSecretEvictingMutation();

  /**
   * Settle handler shared by every write that can carry an apiKey.
   *
   * Delegates the capture-then-evict half to `useSecretEvictingMutation`
   * (arb-689) — see that module for why eviction must happen from here, a
   * per-call site, and never from a hook-level callback in `queries.ts`. This
   * function's own job is the part that hook cannot know: dropping the typed
   * key from BOTH forms on settle. The rest of a rejected draft is
   * deliberately kept so the operator can correct it and resubmit, but the
   * secret is not part of what needs correcting — they can retype it — and
   * holding it in component state (and therefore in the rendered input)
   * after the request has settled keeps a copy alive for no benefit.
   * Clearing it here is what makes the DOM sweep in the leak test true rather
   * than merely close.
   */
  const settleWrite = (mutation: { reset: () => void }, error: unknown) => {
    // `settle` hands back the RAW error — `writeError` here is a rendered
    // string, unlike ApiKeys.tsx's `createFailure`, which stays `unknown` and
    // is formatted at render time instead. Format it here, at the one place
    // this file turns an error into displayed text.
    settle(mutation, error, (captured) =>
      setWriteError(captured === null ? null : errorMessage(captured)),
    );
    setNewDraft((draft) => (draft.apiKey === '' ? draft : { ...draft, apiKey: '' }));
    setEditDraft((draft) => (draft.apiKey === '' ? draft : { ...draft, apiKey: '' }));
  };

  /**
   * Enable/disable from the row, without opening the editor. It still sends the
   * full body — `kind`, `displayName` and `baseUrl` included — because omitting
   * them is a validation failure, not a no-op. `apiKey` stays absent, which is
   * what keeps a toggle from wiping the stored credential.
   */
  const toggleEnabled = (source: SourceSummary) =>
    update.mutate(
      {
        id: source.id,
        source: {
          kind: source.kind,
          displayName: source.displayName,
          baseUrl: source.baseUrl,
          enabled: !source.enabled,
        },
      },
      { onSettled: (_data, error) => settleWrite(update, error) },
    );

  return (
    <section className={styles.panel}>
      <div className={local.headingRow}>
        <h2 className={styles.panelHeading}>Sources</h2>
        {/* Same convention as the Maintenance interval setting's badge
            (Settings.tsx's SettingRow, entry.requiresRestart): sources are
            edited here but only take effect on the next restart, so the
            badge matches the copy directly below it rather than introducing
            a second wording for the same fact. A sibling of the <h2>, not a
            child -- nesting it inside would fold "Restart required" into the
            heading's accessible name alongside "Sources". */}
        <span className={`${styles.badge} ${styles.badgeWarn}`}>Restart required</span>
      </div>
      <div className={styles.panelBody}>
        <p className={styles.muted}>
          Indexer sources Arbitarr searches. These are stored in the database and take effect on
          the next restart; environment variables are read only to seed this list on a first run.
        </p>

        {writeError !== null && (
          <p className={`${styles.error} ${local.writeError}`} role="alert">
            {writeError}
          </p>
        )}

        {/* The list's pending/error/503 treatment is QueryState's, not this
            file's (arb-z505): the four-arm ternary this replaces was a fourth
            private copy of it, and a copy is a place the shared treatment can
            be improved everywhere except here. The empty-list arm is NOT part
            of that triad — an empty list is a loaded state, so it stays a
            surface concern inside `children(data)`. */}
        <QueryState isPending={sources.isPending} error={sources.error} data={sources.data}>
          {(loaded) =>
            loaded.length === 0 ? (
              <p className={styles.empty}>
                No sources configured — add an NZBHydra2 base URL and API key below to start
                searching.
              </p>
            ) : (
              <div className={styles.tableScroll}>
                <table className={styles.table}>
                  <thead>
                    <tr>
                      <th>Kind</th>
                      <th>Name</th>
                      <th>Base URL</th>
                      <th>Enabled</th>
                      <th>API key</th>
                      <th>Created</th>
                      <th>Updated</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {loaded.map((source) => (
                      <tr key={source.id}>
                        <td>{source.kind}</td>
                        <td>{source.displayName}</td>
                        <td>
                          <code className={local.url}>{source.baseUrl}</code>
                        </td>
                        <td>
                          <span className={`${styles.badge} ${source.enabled ? styles.badgeOk : ''}`}>
                            {source.enabled ? 'Enabled' : 'Disabled'}
                          </span>
                        </td>
                        <td>
                          {/* The entire read surface for the secret: a boolean the
                              server derived. There is no value here to reveal,
                              mask, or copy — SourceResponse has no field that could
                              carry one. */}
                          <span
                            className={`${styles.badge} ${source.hasApiKey ? styles.badgeOk : styles.badgeWarn}`}
                          >
                            {source.hasApiKey ? 'Configured' : 'Not configured'}
                          </span>
                        </td>
                        <td className={styles.muted}>{new Date(source.createdAt).toLocaleString()}</td>
                        <td className={styles.muted}>{new Date(source.updatedAt).toLocaleString()}</td>
                        <td className={local.rowActions}>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => {
                              setTestedId(source.id);
                              test.mutate(source.id);
                            }}
                            disabled={test.isPending}
                          >
                            Test
                          </button>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => toggleEnabled(source)}
                            disabled={update.isPending}
                          >
                            {source.enabled ? 'Disable' : 'Enable'}
                          </button>
                          <button
                            type="button"
                            className={styles.buttonSecondary}
                            onClick={() => startEditing(source)}
                          >
                            Edit
                          </button>
                          {confirmingId === source.id ? (
                            <>
                              <button
                                type="button"
                                className={styles.buttonDanger}
                                onClick={() => {
                                  setConfirmingId(null);
                                  // remove carries only an id, never a key, so it
                                  // needs no gcTime/reset treatment — but its
                                  // rejection still has to reach the same banner.
                                  remove.mutate(source.id, {
                                    onSettled: (_data, error) =>
                                      setWriteError(
                                        error === null || error === undefined
                                          ? null
                                          : errorMessage(error),
                                      ),
                                  });
                                }}
                                disabled={remove.isPending}
                              >
                                Confirm remove
                              </button>
                              <button
                                type="button"
                                className={styles.buttonSecondary}
                                onClick={() => setConfirmingId(null)}
                              >
                                Keep
                              </button>
                            </>
                          ) : (
                            <button
                              type="button"
                              className={styles.buttonDanger}
                              onClick={() => setConfirmingId(source.id)}
                            >
                              Remove
                            </button>
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )
          }
        </QueryState>

        {/* Removing a source deletes its stored key with it, and the key cannot
            be read back out to restore it — so the destructive action is
            two-step rather than a single click next to Edit. */}
        {confirmingId !== null && (
          <p className={local.confirm} role="alert">
            Removing this source also deletes its stored API key. The key cannot be recovered and
            will have to be entered again.
          </p>
        )}

        {/* One result region for the whole table, naming the source it belongs
            to. With several sources a bare verdict is ambiguous — the operator
            cannot tell which row it answered — and the probe is the one control
            here whose result is worth nothing if attributed to the wrong
            source. */}
        {tested !== undefined && (
          <div className={local.testResult}>
            {test.isPending ? (
              <p className={styles.muted}>Testing {tested.displayName}…</p>
            ) : test.error !== null && test.error !== undefined ? (
              <p className={styles.error} role="alert">
                {errorMessage(test.error)}
              </p>
            ) : test.data !== undefined ? (
              <p role="status">
                <strong>{tested.displayName}</strong>{' '}
                {/* Keyed off the outcome enum, not the `success` boolean, so the
                    badge cannot drift from the five outcomes: they are the
                    contract, and `success` is a second encoding of the same
                    fact that could disagree with it. */}
                <span
                  className={`${styles.badge} ${test.data.outcome === 'Ok' ? styles.badgeOk : styles.badgeDanger}`}
                >
                  {outcomeLabel(test.data.outcome)}
                </span>{' '}
                <span className={styles.muted}>{test.data.message}</span>
              </p>
            ) : null}
          </div>
        )}
      </div>

      {editing !== undefined && editingId !== null && (
        <div className={local.subPanel}>
          <h3 className={local.subHeading}>Edit source</h3>
          <p className={styles.muted}>
            {editing.hasApiKey
              ? 'An API key is stored for this source. It cannot be shown. Leave the key field empty to keep it, or type a new one to replace it.'
              : 'No API key is stored for this source. Type one to set it, or leave the field empty.'}
          </p>
          <SourceForm
            draft={editDraft}
            onChange={setEditDraft}
            idPrefix="Edit source"
            hasApiKey={editing.hasApiKey}
            submitLabel={update.isPending ? 'Saving…' : 'Save changes'}
            busy={update.isPending}
            onCancel={() => setEditingId(null)}
            onSubmit={() =>
              update.mutate(
                { id: editingId, source: toUpdateRequest(editDraft) },
                {
                  // The editor stays open on failure, holding what was typed, so
                  // the operator can correct it against the server's own reason.
                  onSuccess: () => setEditingId(null),
                  onSettled: (_data, error) => settleWrite(update, error),
                },
              )
            }
          />
        </div>
      )}

      <div className={local.subPanel}>
        <h3 className={local.subHeading}>Add source</h3>
        <SourceForm
          draft={newDraft}
          onChange={setNewDraft}
          idPrefix="New source"
          submitLabel={create.isPending ? 'Adding…' : 'Add source'}
          busy={create.isPending}
          onSubmit={() =>
            create.mutate(toCreateRequest(newDraft), {
              onSuccess: () => setNewDraft(BLANK_DRAFT),
              onSettled: (_data, error) => settleWrite(create, error),
            })
          }
        />
      </div>
    </section>
  );
}
