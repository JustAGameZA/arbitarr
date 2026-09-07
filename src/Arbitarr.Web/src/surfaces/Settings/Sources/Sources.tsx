import { useState } from 'react';

import { errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import local from './Sources.module.css';
import {
  useCreateSourceMutation,
  useDeleteSourceMutation,
  useSourcesQuery,
  useTestSourceMutation,
  useUpdateSourceMutation,
} from './queries';
import type { CreateSourceRequest, SourceSummary, UpdateSourceRequest } from './types';

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
}

const BLANK_DRAFT: SourceDraft = {
  kind: 'NzbHydra',
  displayName: '',
  baseUrl: '',
  enabled: true,
  apiKey: '',
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
 */
function toUpdateRequest(draft: SourceDraft): UpdateSourceRequest {
  const request: UpdateSourceRequest = {
    kind: draft.kind,
    displayName: draft.displayName,
    baseUrl: draft.baseUrl,
    enabled: draft.enabled,
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
 *   different things in the same table. It needs its own section with its own
 *   probe, tracked as a follow-up; it is not silently skipped.
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

  const editing = sources.data?.find((source) => source.id === editingId);
  const writeError = create.error ?? update.error ?? remove.error;

  const startEditing = (source: SourceSummary) => {
    setEditingId(source.id);
    setEditDraft(draftOf(source));
  };

  /**
   * Enable/disable from the row, without opening the editor. It still sends the
   * full body — `kind`, `displayName` and `baseUrl` included — because omitting
   * them is a validation failure, not a no-op. `apiKey` stays absent, which is
   * what keeps a toggle from wiping the stored credential.
   */
  const toggleEnabled = (source: SourceSummary) =>
    update.mutate({
      id: source.id,
      source: {
        kind: source.kind,
        displayName: source.displayName,
        baseUrl: source.baseUrl,
        enabled: !source.enabled,
      },
    });

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Sources</h2>
      <div className={styles.panelBody}>
        <p className={styles.muted}>
          Indexer sources Arbitarr searches. These are stored in the database and take effect on
          the next restart; environment variables are read only to seed this list on a first run.
        </p>

        {writeError !== null && writeError !== undefined && (
          <p className={`${styles.error} ${local.writeError}`} role="alert">
            {errorMessage(writeError)}
          </p>
        )}

        {sources.error !== null && sources.error !== undefined ? (
          <p className={styles.error} role="alert">
            {errorMessage(sources.error)}
          </p>
        ) : sources.isPending || sources.data === undefined ? (
          <p className={styles.muted}>Loading…</p>
        ) : sources.data.length === 0 ? (
          <p className={styles.empty}>
            No sources configured — add an NZBHydra2 base URL and API key below to start searching.
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
                {sources.data.map((source) => (
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
                              remove.mutate(source.id);
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
        )}

        {/* Removing a source deletes its stored key with it, and the key cannot
            be read back out to restore it — so the destructive action is
            two-step rather than a single click next to Edit. */}
        {confirmingId !== null && (
          <p className={local.confirm} role="alert">
            Removing this source also deletes its stored API key. The key cannot be recovered and
            will have to be entered again.
          </p>
        )}

        {testedId !== null && (
          <div className={local.testResult}>
            {test.isPending ? (
              <p className={styles.muted}>Testing…</p>
            ) : test.error !== null && test.error !== undefined ? (
              <p className={styles.error} role="alert">
                {errorMessage(test.error)}
              </p>
            ) : test.data !== undefined ? (
              <p role="status">
                <span
                  className={`${styles.badge} ${test.data.success ? styles.badgeOk : styles.badgeDanger}`}
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
                // The editor stays open on failure, holding what was typed, so
                // the operator can correct it against the server's own reason.
                { onSuccess: () => setEditingId(null) },
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
            create.mutate(toCreateRequest(newDraft), { onSuccess: () => setNewDraft(BLANK_DRAFT) })
          }
        />
      </div>
    </section>
  );
}
