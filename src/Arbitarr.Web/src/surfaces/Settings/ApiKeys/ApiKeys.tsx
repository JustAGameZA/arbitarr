import { useState } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import type { ApiKeyEntry, ApiKeyScope, CreatedApiKeyResponse } from '../../../api/types';
import styles from '../../surface.module.css';
import local from './ApiKeys.module.css';
import { useApiKeysQuery, useCreateApiKeyMutation, useRevokeApiKeyMutation } from './queries';

/**
 * What each scope actually reaches, in the vocabulary the routing layer uses.
 *
 * This is deliberately phrased as reach rather than as a permission name: an
 * operator choosing between two words on a dropdown cannot tell what "ReadOnly"
 * costs them until it is spelled out which routes answer it. The split mirrors
 * `RouteClassification` exactly and invents no second taxonomy — read keys reach
 * the read-only admin surfaces, admin keys additionally reach the mutating ones.
 */
const SCOPE_EXPLANATION: Record<ApiKeyScope, string> = {
  ReadOnly:
    'Reaches read-only admin routes — search, match explanations, the suppression view and observability. It cannot change rules, settings, sources or keys. This is the scope to hand a Sonarr or Radarr instance.',
  Admin:
    'Reaches everything a read key does, plus every mutating admin route — rules, settings, sources, and this key list itself. Give it only to a caller you would trust with the box.',
};

function formatTimestamp(value: string | null): string {
  if (value === null) {
    return '—';
  }
  return new Date(value).toLocaleString();
}

/**
 * The one-time reveal (AC2).
 *
 * The plaintext arrives here as a prop from the parent's component state and is
 * never written anywhere else — not `localStorage`, not `sessionStorage`, not the
 * query cache, not a URL. There is no route that could show it again, because
 * only its hash was stored, so the copy on this panel is the operator's last
 * chance and the panel says exactly that.
 *
 * Dismissal is gated on an explicit acknowledgement rather than a bare close
 * button: a stray click that destroyed the only copy of a credential is a failure
 * with no recovery path, and a checkbox is the cheapest thing that makes the
 * operator assert they have the value.
 */
function CreatedKeyReveal({
  created,
  onDismiss,
}: {
  created: CreatedApiKeyResponse;
  onDismiss: () => void;
}) {
  const [acknowledged, setAcknowledged] = useState(false);
  const [copied, setCopied] = useState(false);

  const copy = () => {
    // Best-effort: jsdom and any non-secure context lack the clipboard API, and a
    // failed copy must not take the panel down with it — the value stays on
    // screen to be selected by hand.
    void navigator.clipboard
      ?.writeText(created.plaintextKey)
      .then(() => setCopied(true))
      .catch(() => setCopied(false));
  };

  return (
    <div className={local.reveal} role="alert" aria-labelledby="api-key-reveal-heading">
      <h3 id="api-key-reveal-heading" className={local.revealHeading}>
        Copy &ldquo;{created.key.label}&rdquo; now — this is the only time it will ever be shown
      </h3>
      <p className={local.revealWarning}>
        Only a hash of this key is stored, so there is no screen, route or support procedure that
        can show it again. If you lose it, revoke this key and create another.
      </p>

      <div className={local.revealValue}>
        <code className={local.secret}>{created.plaintextKey}</code>
        <button type="button" className={styles.buttonSecondary} onClick={copy}>
          Copy
        </button>
        {copied && <span className={local.copied}>Copied.</span>}
      </div>

      <label className={local.acknowledge}>
        <input
          type="checkbox"
          checked={acknowledged}
          onChange={(event) => setAcknowledged(event.target.checked)}
        />
        I have copied this key somewhere safe.
      </label>

      <button type="button" className={styles.button} disabled={!acknowledged} onClick={onDismiss}>
        Dismiss
      </button>
    </div>
  );
}

/** Create form: label plus scope, with the scope choice explained rather than just offered. */
function CreateKeyForm({
  onCreate,
  pending,
  failure,
}: {
  onCreate: (label: string, scope: ApiKeyScope) => void;
  pending: boolean;
  failure: unknown;
}) {
  const [label, setLabel] = useState('');
  const [scope, setScope] = useState<ApiKeyScope>('ReadOnly');

  return (
    <div className={local.createForm}>
      <form
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          // Straight to the server. The repository already rejects empty,
          // over-long and colliding labels with its own words; a client-side
          // pre-check would only be a second, drifting copy of that floor.
          onCreate(label, scope);
        }}
      >
        <label className={styles.field}>
          Label
          <input
            className={styles.input}
            aria-label="New key label"
            value={label}
            onChange={(event) => setLabel(event.target.value)}
            placeholder="Sonarr"
          />
        </label>
        <label className={styles.field}>
          Scope
          <select
            className={styles.select}
            aria-label="New key scope"
            value={scope}
            onChange={(event) => setScope(event.target.value as ApiKeyScope)}
          >
            <option value="ReadOnly">Read only</option>
            <option value="Admin">Admin</option>
          </select>
        </label>
        <button type="submit" className={styles.button} disabled={pending}>
          Create key
        </button>
      </form>

      <p className={local.scopeHelp}>{SCOPE_EXPLANATION[scope]}</p>

      {failure !== null && failure !== undefined && (
        <p className={styles.error} role="alert">
          {errorMessage(failure)}
        </p>
      )}
    </div>
  );
}

function KeyRow({
  entry,
  confirmingId,
  onAskConfirm,
  onCancelConfirm,
  onRevoke,
  pending,
  failure,
}: {
  entry: ApiKeyEntry;
  confirmingId: number | null;
  onAskConfirm: (id: number) => void;
  onCancelConfirm: () => void;
  onRevoke: (id: number) => void;
  pending: boolean;
  /** The server's refusal for THIS key, or null. Never a client-side guess. */
  failure: unknown;
}) {
  const revoked = entry.revokedAt !== null;
  const id = entry.id;
  const confirming = id !== null && confirmingId === id;

  return (
    <tr className={revoked ? local.tombstone : undefined}>
      <td>
        <span className={local.label}>{entry.label}</span>
      </td>
      <td>{entry.scope === 'Admin' ? 'Admin' : 'Read only'}</td>
      <td>{formatTimestamp(entry.createdAt)}</td>
      <td>{formatTimestamp(entry.lastUsedAt)}</td>
      <td>
        {revoked ? (
          <span className={`${styles.badge} ${styles.badgeDanger}`}>
            Revoked {formatTimestamp(entry.revokedAt)}
          </span>
        ) : (
          <span className={`${styles.badge} ${styles.badgeOk}`}>Active</span>
        )}
      </td>
      <td>
        <div className={local.rowActions}>
          {entry.isLegacy ? (
            // The absence of a revoke button is answered exactly where the button
            // would be. This row is synthesised from the configured shared key, not
            // from a table row, so there is nothing a DELETE could address — and an
            // operator who is not told that reads the gap as a bug.
            <span className={local.legacyNote}>
              The pre-existing shared key, from the server configuration rather than this list. It
              cannot be revoked here; remove it from the server configuration instead.
            </span>
          ) : revoked || id === null ? null : confirming ? (
            <>
              <span className={local.confirm}>Revoke &ldquo;{entry.label}&rdquo;?</span>
              <button
                type="button"
                className={styles.buttonDanger}
                disabled={pending}
                onClick={() => onRevoke(id)}
              >
                Confirm revoke
              </button>
              <button type="button" className={styles.buttonSecondary} onClick={onCancelConfirm}>
                Cancel
              </button>
            </>
          ) : (
            <button
              type="button"
              className={styles.buttonDanger}
              onClick={() => onAskConfirm(id)}
              aria-label={`Revoke ${entry.label}`}
            >
              Revoke
            </button>
          )}
        </div>
        {failure !== null && failure !== undefined && (
          <p className={`${styles.error} ${local.rowError}`} role="alert">
            {errorMessage(failure)}
          </p>
        )}
      </td>
    </tr>
  );
}

/**
 * API keys (#82, the UI half of #58).
 *
 * Three properties this section exists to hold, each of which a tidy-up would
 * break silently:
 *
 * 1. The created plaintext lives in COMPONENT STATE ONLY, for as long as the
 *    reveal panel is open, and is then dropped. It is never persisted to browser
 *    storage or the query cache, because only its hash is stored server-side and
 *    a copy anywhere else outlives the one-shot guarantee the API is built on.
 * 2. A revoked key stays rendered as a tombstone. The server keeps the row on
 *    purpose; removing it from the list here would hide the history the retention
 *    was for.
 * 3. The AC5 last-admin-key refusal is the SERVER's message, verbatim, via
 *    `errorMessage`. There is no client-side count of admin keys, because a guess
 *    that disagreed with the server would either block a legal revocation or
 *    invent a refusal.
 */
export function ApiKeysSection() {
  const keys = useApiKeysQuery();
  const create = useCreateApiKeyMutation();
  const revoke = useRevokeApiKeyMutation();

  // The plaintext's ONLY home. Not a ref, not storage, not the cache.
  const [created, setCreated] = useState<CreatedApiKeyResponse | null>(null);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  const [revokeFailedId, setRevokeFailedId] = useState<number | null>(null);
  // Both outcomes of a create are held HERE rather than read back off the
  // mutation, because the mutation is reset the moment it settles (see
  // `dropCreateFromCache`): after that `create.data` and `create.error` are both
  // undefined, so a render that consulted them would show neither the key nor the
  // server's refusal.
  const [createFailure, setCreateFailure] = useState<unknown>(null);

  /**
   * Drop the settled create from the MutationCache.
   *
   * `reset()` releases the mutation, and the `gcTime: 0` in `queries.ts` is what
   * makes that collection immediate rather than five minutes late — together they
   * are what stops `state.data`, plaintext and all, sitting in the cache where the
   * devtools or the console can still read it.
   *
   * WHERE this is called from is load-bearing. It must run from the per-call
   * callbacks passed to `mutate(vars, { ... })` below — never from a hook-level
   * `onSuccess`/`onSettled` in `queries.ts`. `Mutation.execute` awaits the
   * hook-level callbacks BEFORE it dispatches the settle action, and it is that
   * dispatch which notifies the observer and runs these per-call ones. Since
   * `MutationObserver.reset()` clears its current mutation and removes the
   * observer, and the notify path is gated on `hasListeners()`, a reset from up
   * there does not merely race the capture below: it deletes the callback's only
   * delivery route, so the reveal never receives the key and a rejection is
   * swallowed with no message shown. Called from here the capture has already
   * happened, so a plain synchronous reset is correct and no deferral is needed.
   */
  const dropCreateFromCache = () => {
    create.reset();
  };

  const onCreate = (label: string, scope: ApiKeyScope) => {
    setCreated(null);
    // Clear the previous attempt's error before starting a new one. Without this
    // the last rejection stays on screen underneath the retry — the operator
    // reads a refusal the server has not issued for the value now in the field.
    setCreateFailure(null);
    create.reset();
    create.mutate(
      { label, scope },
      {
        onSuccess: (response) => {
          setCreated(response);
          dropCreateFromCache();
        },
        onError: (error) => {
          setCreateFailure(error);
          dropCreateFromCache();
        },
      },
    );
  };

  /**
   * Dropping the reveal drops the rendered copy — the last one left.
   *
   * There is deliberately no `create.reset()` here: the cached copy is already
   * gone, dropped by `dropCreateFromCache` the moment the create settled, so this
   * only has to clear the state the panel renders from. A reset here as well would
   * be unfalsifiable — removing it changes no observable behaviour — and a line no
   * test can hold accountable is one a later edit can quietly break.
   */
  const onDismissReveal = () => {
    setCreated(null);
  };

  const onRevoke = (id: number) => {
    setRevokeFailedId(null);
    // Same reason as create: the previous refusal must not outlive its attempt.
    revoke.reset();
    revoke.mutate(id, {
      onSuccess: () => setConfirmingId(null),
      onError: () => setRevokeFailedId(id),
    });
  };

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>API keys</h2>
      <div className={styles.panelBody}>
        <p className={local.intro}>
          Named, scoped credentials for the callers that talk to this instance. Each one can be
          revoked on its own, so a compromised or retired caller does not mean re-keying every
          other.
        </p>

        {created !== null && (
          <CreatedKeyReveal created={created} onDismiss={onDismissReveal} />
        )}

        {/*
          `failure` is the captured copy, never `create.error`: the mutation is
          reset once it settles, so its own error is gone by the time this renders.
        */}
        <CreateKeyForm onCreate={onCreate} pending={create.isPending} failure={createFailure} />

        <QueryState isPending={keys.isPending} error={keys.error} data={keys.data}>
          {(entries) =>
            entries.length === 0 ? (
              <p className={styles.empty}>
                No API keys yet. Create one above to give a caller — a Sonarr or Radarr instance, or
                a script — its own revocable credential.
              </p>
            ) : (
              <>
                <div className={styles.tableScroll}>
                  <table className={styles.table}>
                    <thead>
                      <tr>
                        <th>Label</th>
                        <th>Scope</th>
                        <th>Created</th>
                        <th>Last used</th>
                        <th>State</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {entries.map((entry) => (
                        <KeyRow
                          key={entry.id ?? `legacy:${entry.label}`}
                          entry={entry}
                          confirmingId={confirmingId}
                          onAskConfirm={setConfirmingId}
                          onCancelConfirm={() => setConfirmingId(null)}
                          onRevoke={onRevoke}
                          pending={revoke.isPending}
                          // The refusal belongs to ONE key. AC5's message names the
                          // key it refused ("'X' is the last API key with admin
                          // scope"), so it renders in that key's row rather than
                          // under the table, where it would read as a statement
                          // about the list.
                          // The `entry.id !== null` guard keeps the legacy row out of
                          // this comparison entirely: its id is null and so is
                          // revokeFailedId's initial value, so null === null would
                          // hand an unrevokable row somebody else's refusal the
                          // moment either of those invariants shifted.
                          failure={
                            entry.id !== null && revokeFailedId === entry.id ? revoke.error : null
                          }
                        />
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )
          }
        </QueryState>
      </div>
    </section>
  );
}
