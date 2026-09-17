import { useEffect, useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState, errorMessage } from '../QueryState';
import type { FilterRule, UpsertFilterRuleRequest } from '../../api/types';
import styles from '../surface.module.css';
import local from './Rules.module.css';
import {
  useCreateRuleMutation,
  useDeleteRuleMutation,
  useRulesQuery,
  useTestRuleMutation,
  useUpdateRuleMutation,
} from './queries';

/** The editor's own state: precedence is a string while it is being typed. */
interface RuleDraft {
  name: string;
  isAllow: boolean;
  pattern: string;
  precedence: string;
  enabled: boolean;
}

const BLANK_DRAFT: RuleDraft = {
  name: '',
  isAllow: true,
  pattern: '',
  precedence: '100',
  enabled: true,
};

const draftOf = (rule: FilterRule): RuleDraft => ({
  name: rule.name,
  isAllow: rule.isAllow,
  pattern: rule.pattern,
  precedence: String(rule.precedence),
  enabled: rule.enabled,
});

/**
 * The draft as the endpoint wants it.
 *
 * Precedence is sent as whatever number the field parses to, with no clamping
 * and no client-side range check: the server validates and rejects, and a
 * silently-adjusted value is exactly the behaviour AC9 forbids. A blank or
 * non-numeric entry becomes NaN, which serializes to null and draws the
 * server's own "required" rejection rather than a message invented here.
 */
function toRequest(draft: RuleDraft): UpsertFilterRuleRequest {
  return {
    name: draft.name,
    isAllow: draft.isAllow,
    pattern: draft.pattern,
    precedence: Number(draft.precedence),
    enabled: draft.enabled,
  };
}

function RuleForm({
  draft,
  onChange,
  onSubmit,
  onCancel,
  submitLabel,
  busy,
  error,
}: {
  draft: RuleDraft;
  onChange: (draft: RuleDraft) => void;
  onSubmit: () => void;
  onCancel?: () => void;
  submitLabel: string;
  busy: boolean;
  /** The server's rejection for THIS form's last submit, or null/undefined. */
  error?: unknown;
}) {
  const set = <K extends keyof RuleDraft>(name: K, value: RuleDraft[K]) =>
    onChange({ ...draft, [name]: value });

  return (
    <form
      className={styles.form}
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit();
      }}
    >
      <label className={styles.field}>
        Name
        <input
          className={styles.input}
          value={draft.name}
          onChange={(event) => set('name', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Pattern
        <input
          className={styles.input}
          value={draft.pattern}
          onChange={(event) => set('pattern', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Precedence
        <input
          type="number"
          className={`${styles.input} ${styles.inputNarrow}`}
          value={draft.precedence}
          onChange={(event) => set('precedence', event.target.value)}
        />
      </label>
      <label className={styles.field}>
        Verdict
        <select
          className={styles.select}
          value={draft.isAllow ? 'allow' : 'deny'}
          onChange={(event) => set('isAllow', event.target.value === 'allow')}
        >
          <option value="allow">Allow</option>
          <option value="deny">Deny</option>
        </select>
      </label>
      <label className={styles.checkboxField}>
        <input
          type="checkbox"
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
      {error !== null && error !== undefined && (
        <p className={styles.error} role="alert">
          {errorMessage(error)}
        </p>
      )}
    </form>
  );
}

/**
 * Filter rules (AC9): list, create, edit, delete and dry-run test.
 *
 * Every rejection shown here is the server's own text. There is no client-side
 * validation layer in front of the writes -- a second opinion about what is
 * valid drifts from the real rules and, worse, hides them.
 */
export default function RulesPage() {
  const rules = useRulesQuery();
  const create = useCreateRuleMutation();
  const update = useUpdateRuleMutation();
  const remove = useDeleteRuleMutation();
  const test = useTestRuleMutation();

  const [newDraft, setNewDraft] = useState<RuleDraft>(BLANK_DRAFT);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editDraft, setEditDraft] = useState<RuleDraft>(BLANK_DRAFT);
  const [testTitle, setTestTitle] = useState('');
  const [confirmingId, setConfirmingId] = useState<number | null>(null);
  // #98/ApiKeys's shape (A1), then arb-39g3: the CAPTURED copy of the error is
  // held per id in a Map rather than in one shared scalar, since `remove.error`
  // is shared across rows and a second refusal in flight would otherwise
  // overwrite the first -- row A's text vanishing or appearing under row B. A
  // row's own entry is cleared when THAT row starts a new attempt (onDelete)
  // and when THAT row's delete succeeds; another row's activity never touches it.
  const [deleteFailures, setDeleteFailures] = useState<ReadonlyMap<number, unknown>>(new Map());
  // The real invariant this rests on: `confirmingId` being section-wide means
  // only one row's Confirm is ever RENDERED at a time, but nothing stops the
  // operator moving `confirmingId` to a different row while an earlier
  // `remove.mutate` is still in flight -- there is no guard against it. A
  // single `remove.variables === rule.id` check tracks only the LATEST call,
  // so a second delete started before the first settles would silently stop
  // showing the first row as pending. This Set is kept in local state instead,
  // one id added right before each `mutate` and removed once IT settles, so
  // every row still in flight reads pending regardless of how many others have
  // started since -- and unlike the removed shared-boolean defect, a row not
  // in this Set stays fully operable no matter how many other deletes are
  // running.
  const [pendingDeleteIds, setPendingDeleteIds] = useState<ReadonlySet<number>>(new Set());

  // Prune `deleteFailures` entries whose row has left the list -- a refusal for
  // an id no longer present renders nowhere, but stays keyed to that id
  // forever otherwise, and if the SAME id is later reused (a new rule created
  // after the server recycles ids) it would surface with the earlier row's
  // stale refusal. Runs off `rules.data` rather than the delete callbacks
  // themselves, since a row can also vanish through another operator's
  // action -- a concurrent delete from a second admin session -- with no
  // local callback of this component's own to hook.
  //
  // Guarded by a functional update that returns the SAME Map reference when
  // nothing needs pruning, so a fetch that changes row order or unrelated
  // fields (not row membership) does not produce a new Map identity and does
  // not re-trigger this effect's own setState -- no render loop. A row that
  // is still present is left untouched: this only ever deletes, it never
  // clears or rewrites a surviving entry.
  useEffect(() => {
    const data = rules.data;
    if (data === undefined) {
      return;
    }
    const liveIds = new Set(data.map((rule) => rule.id));
    setDeleteFailures((failures) => {
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
  }, [rules.data]);

  const startEditing = (rule: FilterRule) => {
    // A rejected save from a PREVIOUS edit target must not outlive it: without
    // this reset, `update.error` stays on the mutation until the next save
    // settles, so opening a different rule's editor rendered the last rule's
    // refusal inside this one's form.
    update.reset();
    setEditingId(rule.id);
    setEditDraft(draftOf(rule));
  };

  const onDelete = (id: number) => {
    // The previous refusal for THIS row must not outlive its attempt, or the
    // operator reads a rejection the server has not issued for the row now
    // under the cursor. Only this row's entry is cleared -- another row's
    // refusal, still unresolved, must survive this call untouched.
    setDeleteFailures((failures) => {
      const next = new Map(failures);
      next.delete(id);
      return next;
    });
    setPendingDeleteIds((ids) => new Set(ids).add(id));
    remove.mutate(id, {
      onSuccess: () => {
        // The row is gone from the refetched list, so the confirm it was
        // showing has nothing left to confirm.
        setConfirmingId(null);
        setDeleteFailures((failures) => {
          const next = new Map(failures);
          next.delete(id);
          return next;
        });
      },
      onError: (error) => {
        setDeleteFailures((failures) => new Map(failures).set(id, error));
      },
      onSettled: () => {
        setPendingDeleteIds((ids) => {
          const next = new Set(ids);
          next.delete(id);
          return next;
        });
      },
    });
  };

  return (
    <>
      <PageHeader title="Rules" description="Allow and deny rules applied to incoming releases." />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Rules</h2>
        <div className={styles.panelBody}>
          <QueryState isPending={rules.isPending} error={rules.error} data={rules.data}>
            {(data) =>
              data.length === 0 ? (
                <p className={styles.empty}>
                  No rules defined. Add one below to allow or deny releases matching a pattern.
                </p>
              ) : (
                <div className={styles.tableScroll}>
                  <table className={styles.table}>
                    <thead>
                      <tr>
                        <th>Precedence</th>
                        <th>Name</th>
                        <th>Verdict</th>
                        <th>Pattern</th>
                        <th>Enabled</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      {data.map((rule) => (
                        <tr key={rule.id}>
                          <td>{rule.precedence}</td>
                          <td>{rule.name}</td>
                          <td>
                            <span
                              className={`${styles.badge} ${rule.isAllow ? styles.badgeOk : styles.badgeDanger}`}
                            >
                              {rule.isAllow ? 'Allow' : 'Deny'}
                            </span>
                          </td>
                          <td>
                            <code className={local.pattern}>{rule.pattern}</code>
                          </td>
                          <td>{rule.enabled ? 'Yes' : 'No'}</td>
                          <td>
                            <div className={local.rowActions}>
                              <button
                                type="button"
                                className={styles.buttonSecondary}
                                onClick={() => startEditing(rule)}
                              >
                                Edit
                              </button>
                              {confirmingId === rule.id ? (
                                <>
                                  <span className={local.confirm}>
                                    Delete &ldquo;{rule.name}&rdquo;?
                                  </span>
                                  <button
                                    type="button"
                                    className={styles.buttonDanger}
                                    // Per-row pending (A1): only THIS row's delete
                                    // in flight disables its own button, tracked via
                                    // `pendingDeleteIds` rather than
                                    // `remove.isPending && remove.variables === rule.id`.
                                    // The mutation's own `variables` holds only the
                                    // LATEST call's argument, so it cannot tell two
                                    // concurrent deletes apart -- and nothing stops a
                                    // second row's delete from starting while an
                                    // earlier one is still in flight: `confirmingId`
                                    // being section-wide means only one row's Confirm
                                    // is ever rendered at once, but `setConfirmingId`
                                    // has no guard against moving to a different row
                                    // mid-flight. `pendingDeleteIds` is a Set keyed by
                                    // id instead, so however many deletes are running
                                    // at once, each row reads pending only for its own.
                                    // Copying ApiKeys's PRE-arb-kytb shared
                                    // `revoke.isPending || remove.isPending` boolean
                                    // here would disable every row's Delete again --
                                    // the exact defect this bead removes -- so this
                                    // must stay per-id, never a shared flag.
                                    disabled={pendingDeleteIds.has(rule.id)}
                                    onClick={() => onDelete(rule.id)}
                                    aria-label={`Confirm delete ${rule.name}`}
                                  >
                                    Confirm delete
                                  </button>
                                  <button
                                    type="button"
                                    className={styles.buttonSecondary}
                                    onClick={() => setConfirmingId(null)}
                                    aria-label={`Cancel delete ${rule.name}`}
                                  >
                                    Cancel
                                  </button>
                                </>
                              ) : (
                                <button
                                  type="button"
                                  className={styles.buttonDanger}
                                  onClick={() => setConfirmingId(rule.id)}
                                  aria-label={`Delete ${rule.name}`}
                                >
                                  Delete
                                </button>
                              )}
                            </div>
                            {deleteFailures.has(rule.id) && (
                              <p className={`${styles.error} ${local.rowError}`} role="alert">
                                {errorMessage(deleteFailures.get(rule.id))}
                              </p>
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
        </div>
      </section>

      {editingId !== null && (
        <section className={styles.panel}>
          <h2 className={styles.panelHeading}>Edit rule</h2>
          <div className={styles.panelBody}>
            <RuleForm
              draft={editDraft}
              onChange={setEditDraft}
              submitLabel={update.isPending ? 'Saving…' : 'Save changes'}
              busy={update.isPending}
              error={update.error}
              onCancel={() => {
                // Same reason as startEditing: Cancel leaves the mutation's own
                // error sitting there for whichever rule is edited next.
                update.reset();
                setEditingId(null);
              }}
              onSubmit={() =>
                update.mutate(
                  { id: editingId, rule: toRequest(editDraft) },
                  // The editor stays open on failure, holding what was typed,
                  // so the operator can correct it against the server's reason.
                  { onSuccess: () => setEditingId(null) },
                )
              }
            />
          </div>
        </section>
      )}

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Add rule</h2>
        <div className={styles.panelBody}>
          <RuleForm
            draft={newDraft}
            onChange={setNewDraft}
            submitLabel={create.isPending ? 'Adding…' : 'Add rule'}
            busy={create.isPending}
            error={create.error}
            onSubmit={() =>
              create.mutate(toRequest(newDraft), { onSuccess: () => setNewDraft(BLANK_DRAFT) })
            }
          />
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Test a rule</h2>
        <div className={styles.panelBody}>
          <p className={styles.muted}>
            Runs the rule in the “Add rule” form above against a title without saving it.
          </p>
          <form
            className={styles.form}
            onSubmit={(event) => {
              event.preventDefault();
              test.mutate({
                name: newDraft.name,
                isAllow: newDraft.isAllow,
                pattern: newDraft.pattern,
                precedence: Number(newDraft.precedence),
                title: testTitle,
              });
            }}
          >
            <label className={styles.field}>
              Release title
              <input
                className={styles.input}
                value={testTitle}
                onChange={(event) => setTestTitle(event.target.value)}
              />
            </label>
            <button type="submit" className={styles.button} disabled={test.isPending}>
              Test
            </button>
          </form>
          {test.error !== null && (
            <p className={styles.error} role="alert">
              {errorMessage(test.error)}
            </p>
          )}
          {test.data !== undefined && test.error === null && (
            <p className={local.verdict}>
              Verdict: <strong>{test.data.verdict}</strong>
            </p>
          )}
        </div>
      </section>
    </>
  );
}
