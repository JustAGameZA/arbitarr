import { useCallback, useEffect, useRef, useState } from 'react';

import { QueryState, errorMessage } from '../../QueryState';
import type { ApiKeyEntry, ApiKeyScope, CreatedApiKeyResponse } from '../../../api/types';
import { useLiveStatusStore } from '../../../state/liveStatusStore';
import styles from '../../surface.module.css';
import { useSecretEvictingMutation } from '../useSecretEvictingMutation';
import local from './ApiKeys.module.css';
import {
  useApiKeysQuery,
  useCreateApiKeyMutation,
  useRemoveApiKeyMutation,
  useRevokeApiKeyMutation,
} from './queries';
import { formatTimestamp, formatTimestampTitle } from '../../../format';

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
    'Reaches public search and download routes plus read-only admin routes — search, match explanations, the suppression view and observability. It cannot change rules, settings, sources or keys. This is the scope to hand a Sonarr or Radarr instance.',
  Admin:
    'Reaches public search and download routes, read-only admin routes, and every mutating admin route — rules, settings, sources, and this key list itself. Give it only to a caller you would trust with the box.',
};

/**
 * The two routes an *arr client is pointed at, as path literals.
 *
 * These are copied from `Program.cs`'s `app.MapGet("/torznab/api", …)` and
 * `app.MapGet("/newznab/api", …)` and must stay byte-identical to them. They are
 * hard-coded on purpose rather than derived from anything: a wrong base URL in
 * Sonarr/Radarr is the 404 this whole block exists to prevent (#337), so a path
 * that drifted would be worse than showing nothing. ApiKeys.test.tsx asserts the
 * rendered URLs end in exactly these suffixes, so a rename on the server that is
 * not mirrored here fails a test rather than shipping a dead link.
 *
 * Both families answer; the README (:110) states Torznab is preferred, and the
 * labels below say the same rather than inventing a second recommendation.
 */
const CLIENT_ROUTES = [
  { path: '/torznab/api', label: 'Torznab', note: 'preferred' },
  { path: '/newznab/api', label: 'Newznab', note: 'Usenet-oriented' },
] as const;

/** How long the visible "Copied." stays up before clearing itself (arb-zxwo). */
const COPIED_FEEDBACK_MS = 2000;

/**
 * The message a successful copy announces (arb-zxwo).
 *
 * IT NAMES WHAT WAS COPIED AND NEVER THE VALUE. Both call sites below copy
 * something the announcement must not carry: the reveal panel's is the
 * plaintext key itself, and the live region is rendered into the DOM by
 * `AppShell` — a message interpolating the value would put a live credential in
 * the shell's markup, outliving the reveal panel that is supposed to be its only
 * home (see `ApiKeysSection`'s property 1). The client URLs carry no secret, but
 * they take the same shape so the two affordances cannot drift into disagreeing
 * about it.
 */
const COPIED_MESSAGE = 'Copied.';

/**
 * Copy feedback for one button: the visible "Copied." plus the announcement
 * that makes it reach a screen reader (arb-zxwo).
 *
 * review-488's finding was that the span is visual only, so an operator who
 * cannot see it has no way to tell a successful copy from a click that did
 * nothing — and the same gap was already in the reveal panel, where a silent
 * failure costs the only copy of a credential. Both are fixed through the
 * shared region (`state/liveStatusStore.ts`) rather than by either site growing
 * an `aria-live` of its own, which is the rule the shared region exists for.
 *
 * Shared by the two call sites rather than written twice on purpose: they must
 * agree about the message, the timeout and — most of all — about announcing
 * ONLY on success. The announcement is unscoped, so two copies in a row are two
 * events and both are heard; that is the `seq` nonce's case, not arb-xzvk's.
 *
 * `token` identifies which button succeeded (a path, or `true` for a lone
 * button), so two buttons cannot both light up from one click. `null` is "no
 * copy is currently acknowledged", which is also where a FAILED copy lands — a
 * context with no clipboard API announces nothing and shows nothing.
 */
function useCopyFeedback<T>(): [T | null, (token: T) => void, () => void] {
  const [copied, setCopied] = useState<T | null>(null);
  const announce = useLiveStatusStore((state) => state.announce);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // Cleared on unmount so a timeout cannot fire setState into a component that
  // is gone — the reveal panel in particular unmounts on dismissal, which is
  // well inside the window.
  useEffect(
    () => () => {
      if (timer.current !== null) {
        clearTimeout(timer.current);
      }
    },
    [],
  );

  const succeeded = useCallback(
    (token: T) => {
      setCopied(token);
      announce(COPIED_MESSAGE);
      if (timer.current !== null) {
        // A second copy restarts the window rather than inheriting the first
        // one's remaining time, which would clear the new acknowledgement early.
        clearTimeout(timer.current);
      }
      timer.current = setTimeout(() => {
        timer.current = null;
        setCopied(null);
      }, COPIED_FEEDBACK_MS);
    },
    [announce],
  );

  const failed = useCallback(() => setCopied(null), []);

  return [copied, succeeded, failed];
}

/**
 * The *arr-facing connection URLs (arb-mn12).
 *
 * THE KEY IS NEVER IN THESE URLS, and that is not an oversight to be tidied up
 * later. The key travels as the `apikey` QUERY PARAMETER, which Sonarr and Radarr
 * append themselves from their own separate "API Key" field — so a URL with the
 * key baked in would be both wrong for the form the operator is filling in and a
 * live credential rendered into a string the reveal panel's doc explicitly
 * forbids ("not a URL"). ApiKeys.test.tsx plants a key and asserts it appears in
 * the reveal but in none of these URLs, with a positive control so the absence
 * assertion is not vacuous.
 *
 * The origin comes from the browser rather than from the server. There is no
 * advertised-base-URL setting, and adding one is a backend feature with its own
 * shape (a catalog entry, validation, precedence against the request origin); the
 * caveat below is the honest alternative. `window.location.origin` is what the
 * operator's own browser reached, which is right in the common case and wrong
 * exactly when Arbitarr sits behind a reverse proxy or the *arr runs in a
 * container that cannot resolve `localhost` — hence the sentence saying so. A
 * concrete URL with a stated caveat beats today's state, which shows no URL at
 * all.
 *
 * Rendered ONCE for the section, not per key row: the URL is identical for every
 * key — it does not vary by key, scope or label — so a per-row copy would repeat
 * an invariant string on every row of an already six-column table.
 */
function ClientUrls({ origin }: { origin: string }) {
  // Which path's copy button last succeeded, or null. Keyed by path rather than a
  // bare boolean so two buttons cannot both light up from one click.
  const [copiedPath, copySucceeded, copyFailed] = useCopyFeedback<string>();

  const copy = (path: string) => {
    // Best-effort, exactly as the reveal panel's copy is: jsdom and any
    // non-secure context lack the clipboard API, and a failed copy must not take
    // the block down with it — the URL stays on screen to be selected by hand.
    //
    // The announcement rides on the SAME success branch as the visible span
    // (arb-zxwo), never on the click: a failed or unavailable clipboard must not
    // tell a screen-reader operator the value was copied when it was not.
    void navigator.clipboard
      ?.writeText(`${origin}${path}`)
      .then(() => copySucceeded(path))
      .catch(copyFailed);
  };

  return (
    <div className={local.clientUrls}>
      <h3 className={local.clientUrlsHeading}>Point Sonarr or Radarr here</h3>
      <p className={local.clientUrlsHelp}>
        In the *arr instance, go to Settings &gt; Indexers, add a Torznab or Newznab indexer, and
        paste one of these as the URL. The API key goes in that form&rsquo;s own separate field —
        it is not part of the URL. A wrong key is reported there as &ldquo;Incorrect user
        credentials&rdquo; (error 100).
      </p>

      <ul className={local.clientUrlList}>
        {CLIENT_ROUTES.map((route) => (
          <li key={route.path} className={local.clientUrlRow}>
            <span className={local.clientUrlLabel}>
              {route.label} <span className={local.clientUrlNote}>({route.note})</span>
            </span>
            <code className={local.clientUrl}>{`${origin}${route.path}`}</code>
            <button
              type="button"
              className={styles.buttonSecondary}
              onClick={() => copy(route.path)}
              aria-label={`Copy ${route.label} URL`}
            >
              Copy
            </button>
            {copiedPath === route.path && <span className={local.copied}>Copied.</span>}
          </li>
        ))}
      </ul>

      <p className={local.clientUrlsCaveat}>
        This address is the one your browser reached Arbitarr on. The Sonarr or Radarr instance has
        to reach it too, and behind a reverse proxy — or from another container, where{' '}
        <code>localhost</code> means that container itself — the externally reachable address may
        differ. Use whichever address that client can resolve, with the same path.
      </p>
    </div>
  );
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
  const [copied, copySucceeded, copyFailed] = useCopyFeedback<true>();

  const copy = () => {
    // Best-effort: jsdom and any non-secure context lack the clipboard API, and a
    // failed copy must not take the panel down with it — the value stays on
    // screen to be selected by hand.
    //
    // arb-zxwo routes the success through the shared live region. The
    // announcement is the fixed `COPIED_MESSAGE` and carries no part of
    // `created.plaintextKey`: the region is rendered into AppShell's markup,
    // which is outside this panel and outlives it, and this panel is the
    // plaintext's only home.
    void navigator.clipboard
      ?.writeText(created.plaintextKey)
      .then(() => copySucceeded(true))
      .catch(copyFailed);
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
  onRemove,
  pending,
  failure,
}: {
  entry: ApiKeyEntry;
  confirmingId: number | null;
  onAskConfirm: (id: number) => void;
  onCancelConfirm: () => void;
  onRevoke: (id: number) => void;
  /** #98. Offered on a REVOKED row only — see the branch below for why. */
  onRemove: (id: number) => void;
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
      <td title={formatTimestampTitle(entry.createdAt)}>{formatTimestamp(entry.createdAt)}</td>
      <td title={formatTimestampTitle(entry.lastUsedAt)}>{formatTimestamp(entry.lastUsedAt)}</td>
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
          ) : id === null ? null : revoked ? (
            // #98. The remove action exists ONLY on this branch, and that placement is
            // the client half of the two-step: a live row has no control that could
            // remove it, so no sequence of clicks on one screen destroys a working
            // credential. The server refuses a live id regardless — this is not the
            // guard, it is the affordance agreeing with the guard.
            //
            // Confirm-gated like the revoke, and for a sharper reason: unlike
            // revocation, this one cannot be undone or inspected afterwards. The row
            // is gone, so a misclick has no screen left to explain itself on.
            confirming ? (
              <>
                <span className={local.confirm}>
                  Remove &ldquo;{entry.label}&rdquo; from the list?
                </span>
                <button
                  type="button"
                  className={styles.buttonDanger}
                  disabled={pending}
                  onClick={() => onRemove(id)}
                  aria-label={`Confirm remove ${entry.label}`}
                >
                  Confirm remove
                </button>
                <button
                  type="button"
                  className={styles.buttonSecondary}
                  onClick={onCancelConfirm}
                  aria-label={`Cancel remove ${entry.label}`}
                >
                  Cancel
                </button>
              </>
            ) : (
              <button
                type="button"
                className={styles.buttonSecondary}
                onClick={() => onAskConfirm(id)}
                aria-label={`Remove ${entry.label}`}
              >
                Remove
              </button>
            )
          ) : confirming ? (
            <>
              <span className={local.confirm}>Revoke &ldquo;{entry.label}&rdquo;?</span>
              <button
                type="button"
                className={styles.buttonDanger}
                disabled={pending}
                onClick={() => onRevoke(id)}
                aria-label={`Confirm revoke ${entry.label}`}
              >
                Confirm revoke
              </button>
              <button
                type="button"
                className={styles.buttonSecondary}
                onClick={onCancelConfirm}
                aria-label={`Cancel revoke ${entry.label}`}
              >
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
 *    purpose; dropping it from the list here would hide the history the retention
 *    was for. Since #98 the operator can remove one DELIBERATELY, and that is not
 *    a softening of this: the row leaves because somebody asked for this key by
 *    name, never because the client decided a tombstone had stopped being
 *    interesting. The remove control exists on revoked rows only, so the live half
 *    of the list has no affordance that could destroy a working credential.
 * 3. The AC5 last-admin-key refusal is the SERVER's message, verbatim, via
 *    `errorMessage`. There is no client-side count of admin keys, because a guess
 *    that disagreed with the server would either block a legal revocation or
 *    invent a refusal.
 */
export function ApiKeysSection() {
  const keys = useApiKeysQuery();
  const create = useCreateApiKeyMutation();
  const revoke = useRevokeApiKeyMutation();
  const remove = useRemoveApiKeyMutation();

  // The plaintext's ONLY home. Not a ref, not storage, not the cache.
  const [created, setCreated] = useState<CreatedApiKeyResponse | null>(null);
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  // Revoke's refusal, held per id in a Map rather than one shared scalar plus
  // `revoke.error`: `revoke.error` names only the LATEST call, so a second
  // revoke in flight would overwrite the first row's message with the second's
  // (or clear it early on the second's success) -- arb-39g3. A row's own entry
  // is cleared when THAT row starts a new attempt and when THAT row succeeds;
  // another row's activity never touches it.
  const [revokeFailures, setRevokeFailures] = useState<ReadonlyMap<number, unknown>>(new Map());
  // #98's refusal, held per id in a Map for the identical reason: `onRemove`
  // resets the mutation once it settles (for fresh state on the next attempt,
  // not for secret eviction — remove's variables are a bare id, so this
  // mutation never goes through `useSecretEvictingMutation`), so `remove.error`
  // is undefined by the time the row renders and a branch that consulted it
  // would show nothing. Keyed by id for the same reason as revokeFailures: two
  // removes in flight must not let the second overwrite the first row's text.
  const [removeFailures, setRemoveFailures] = useState<ReadonlyMap<number, unknown>>(new Map());
  // The real invariant the per-row `pending` prop rests on: `confirmingId`
  // being section-wide means only one row's Confirm is ever RENDERED at a
  // time, but nothing stops the operator moving it to a different row while
  // an earlier revoke or remove is still in flight -- there is no guard
  // against it. `revoke.variables`/`remove.variables` can only ever name the
  // LATEST call, so a second mutate started before the first settles would
  // silently stop showing the first row as pending. These Sets are kept in
  // local state instead: an id is added right before its `mutate` and removed
  // once THAT call settles, so however many revokes or removes are running at
  // once, each row reads pending only for its own — never a shared flag that
  // would disable every other row, which is the arb-kytb defect this file
  // already removed once.
  const [pendingRevokeIds, setPendingRevokeIds] = useState<ReadonlySet<number>>(new Set());
  const [pendingRemoveIds, setPendingRemoveIds] = useState<ReadonlySet<number>>(new Set());
  // Both outcomes of a create are held HERE rather than read back off the
  // mutation, because the mutation is reset (via `settle`/`create.reset()`
  // below) the moment it settles: after that `create.data` and `create.error`
  // are both undefined, so a render that consulted them would show neither
  // the key nor the server's refusal.
  const [createFailure, setCreateFailure] = useState<unknown>(null);

  // Prune `revokeFailures`/`removeFailures` entries whose row has left the
  // list -- converged with Rules's identical fix (arb-gn4z). A refusal for an
  // id no longer present renders nowhere, but stays keyed to that id forever
  // otherwise; if the SAME id is later reused (a legacy row aside, ids come
  // from the server) a stale refusal would surface again under a fetch the
  // operator has nothing to do with. Runs off `keys.data` rather than the
  // per-call callbacks, since a row can also vanish through another admin
  // session's action with no local callback of this component's own to hook.
  //
  // Guarded by a functional update that returns the SAME Map reference when
  // nothing needs pruning, so a fetch that changes unrelated fields (not row
  // membership) does not produce a new Map identity and does not re-trigger
  // this effect's own setState -- no render loop. A row that is still
  // present is left untouched: this only ever deletes, it never clears or
  // rewrites a surviving entry.
  useEffect(() => {
    const data = keys.data;
    if (data === undefined) {
      return;
    }
    const liveIds = new Set(data.map((entry) => entry.id).filter((id): id is number => id !== null));
    setRevokeFailures((failures) => {
      let changed = false;
      const next = new Map(failures);
      for (const id of failures.keys()) {
        if (!liveIds.has(id)) {
          next.delete(id);
          changed = true;
        }
      }
      return changed ? next : failures;
    });
    setRemoveFailures((failures) => {
      let changed = false;
      const next = new Map(failures);
      for (const id of failures.keys()) {
        if (!liveIds.has(id)) {
          next.delete(id);
          changed = true;
        }
      }
      return changed ? next : failures;
    });
  }, [keys.data]);

  const { settle } = useSecretEvictingMutation();

  /**
   * Create is the one mutation in this file whose secret rides on `data`
   * (the plaintext key), not on `variables` — so its success path captures
   * `response` itself rather than going through `settle`'s error-only shape,
   * while its failure path uses `settle` like every other secret-bearing
   * write. Either way `create.reset()` runs from THIS per-call site; see
   * `useSecretEvictingMutation` for why a hook-level reset in `queries.ts`
   * would swallow the response before it reaches here.
   */
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
          create.reset();
        },
        onError: (error) => settle(create, error, setCreateFailure),
      },
    );
  };

  /**
   * Dropping the reveal drops the rendered copy — the last one left.
   *
   * There is deliberately no `create.reset()` here: the cached copy is already
   * gone, evicted the moment the create settled (see `useSecretEvictingMutation`'s
   * module doc for why no caller ever needs a second reset once settle has run),
   * so this only has to clear the state the panel renders from.
   */
  const onDismissReveal = () => {
    setCreated(null);
  };

  const onRevoke = (id: number) => {
    // The previous refusal for THIS row must not outlive its attempt; another
    // row's, still unresolved, must survive this call untouched.
    setRevokeFailures((failures) => {
      const next = new Map(failures);
      next.delete(id);
      return next;
    });
    // Same reason as create: the previous refusal must not outlive its attempt.
    revoke.reset();
    setPendingRevokeIds((ids) => new Set(ids).add(id));
    revoke.mutate(id, {
      onSuccess: () => {
        setConfirmingId(null);
        setRevokeFailures((failures) => {
          const next = new Map(failures);
          next.delete(id);
          return next;
        });
      },
      onError: (error) => {
        setRevokeFailures((failures) => new Map(failures).set(id, error));
      },
      onSettled: () => {
        setPendingRevokeIds((ids) => {
          const next = new Set(ids);
          next.delete(id);
          return next;
        });
      },
    });
  };

  /**
   * Remove an already-revoked key's row (#98).
   *
   * The capture-then-reset shape: both outcomes are recorded into component state
   * from the PER-CALL callbacks, and only then is the mutation reset. The order is
   * load-bearing for the same reason it is on the create — `Mutation.execute`
   * awaits the hook-level callbacks BEFORE dispatching the settle action that runs
   * these, so a reset from `queries.ts` would remove the observer these arrive on
   * and swallow the server's refusal silently. Called from here the capture has
   * already happened, so the synchronous reset is correct.
   *
   * The refusal itself is never generated here. A live key is refused by the
   * server, in the server's words, and rendered verbatim through `errorMessage` —
   * exactly as the AC5 last-admin-key refusal is. The client counts nothing and
   * pre-checks nothing.
   */
  const onRemove = (id: number) => {
    // The previous refusal for THIS row must not outlive its attempt, or the
    // operator reads a rejection the server has not issued for the row now
    // under the cursor. Another row's, still unresolved, must survive this
    // call untouched.
    setRemoveFailures((failures) => {
      const next = new Map(failures);
      next.delete(id);
      return next;
    });
    remove.reset();
    setPendingRemoveIds((ids) => new Set(ids).add(id));
    remove.mutate(id, {
      onSuccess: () => {
        // The row is gone from the refetched list, so the confirm it was showing
        // has nothing left to confirm.
        setConfirmingId(null);
        setRemoveFailures((failures) => {
          const next = new Map(failures);
          next.delete(id);
          return next;
        });
        remove.reset();
      },
      onError: (error) => {
        setRemoveFailures((failures) => new Map(failures).set(id, error));
        remove.reset();
      },
      onSettled: () => {
        setPendingRemoveIds((ids) => {
          const next = new Set(ids);
          next.delete(id);
          return next;
        });
      },
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

        {/*
          Above the create form, not below the table: the URL is what a key is
          FOR, so an operator reads where the key goes before minting one. Read
          from the live location rather than held in state — there is nothing to
          subscribe to, the origin cannot change without a navigation, and a
          module-level constant would freeze the value captured at import.
        */}
        <ClientUrls origin={window.location.origin} />

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
                a script — its own revocable credential. A key of either scope works on the search
                and download routes, so a read-only key is all a Sonarr or Radarr instance needs.
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
                          onRemove={onRemove}
                          // Per row, not the section-wide `revoke.isPending ||
                          // remove.isPending` (arb-kytb): a shared boolean
                          // disabled every row's action while any ONE revoke or
                          // remove was in flight, which is wrong for the same
                          // reason a section-wide error would be — the call
                          // belongs to one key.
                          //
                          // Tracked via `pendingRevokeIds`/`pendingRemoveIds`
                          // rather than `revoke.variables === entry.id`: the
                          // mutation's own `variables` names only the LATEST
                          // call, and nothing stops a second row's revoke or
                          // remove from starting while an earlier one is still
                          // outstanding. `confirmingId` being section-wide means
                          // only one row's Confirm is ever RENDERED at a time,
                          // but `onAskConfirm`/`setConfirmingId` has no guard
                          // against moving it to a different row mid-flight —
                          // so two mutates CAN run at once, and a single
                          // `variables` comparison could not tell them apart.
                          // These Sets are keyed by id, so however many revokes
                          // or removes are running at once, each row reads
                          // pending only for its own. Converged with the Rules
                          // surface's identical fix (arb-nizy).
                          pending={
                            entry.id !== null &&
                            (pendingRevokeIds.has(entry.id) || pendingRemoveIds.has(entry.id))
                          }
                          // The refusal belongs to ONE key. AC5's message names the
                          // key it refused ("'X' is the last API key with admin
                          // scope"), so it renders in that key's row rather than
                          // under the table, where it would read as a statement
                          // about the list.
                          // The `entry.id !== null` guard keeps the legacy row out of
                          // this comparison entirely: its id is null, and a null key
                          // is never inserted into either Map, so a legacy row can
                          // never collide with another row's refusal.
                          // #98 adds the remove refusal to the same slot. The two
                          // cannot collide: a row is either live (revoke can fail on
                          // it) or revoked (remove can), never both, so this reads
                          // as "whichever refusal this row has" rather than as a
                          // precedence rule that would need one. Both sides read the
                          // CAPTURED copy from their own Map, keyed by id, so a
                          // second row's refusal in flight can never overwrite this
                          // row's (arb-39g3).
                          failure={
                            entry.id === null
                              ? null
                              : removeFailures.has(entry.id)
                                ? removeFailures.get(entry.id)
                                : revokeFailures.has(entry.id)
                                  ? revokeFailures.get(entry.id)
                                  : null
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
