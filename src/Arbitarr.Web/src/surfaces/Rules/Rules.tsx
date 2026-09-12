import { useState } from 'react';

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
}: {
  draft: RuleDraft;
  onChange: (draft: RuleDraft) => void;
  onSubmit: () => void;
  onCancel?: () => void;
  submitLabel: string;
  busy: boolean;
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

  const startEditing = (rule: FilterRule) => {
    setEditingId(rule.id);
    setEditDraft(draftOf(rule));
  };

  const writeError = create.error ?? update.error ?? remove.error;

  return (
    <>
      <PageHeader title="Rules" description="Allow and deny rules applied to incoming releases." />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Rules</h2>
        <div className={styles.panelBody}>
          {writeError !== null && writeError !== undefined && (
            <p className={`${styles.error} ${local.writeError}`} role="alert">
              {errorMessage(writeError)}
            </p>
          )}

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
                          <td className={local.rowActions}>
                            <button
                              type="button"
                              className={styles.buttonSecondary}
                              onClick={() => startEditing(rule)}
                            >
                              Edit
                            </button>
                            <button
                              type="button"
                              className={styles.buttonDanger}
                              onClick={() => remove.mutate(rule.id)}
                              disabled={remove.isPending}
                            >
                              Delete
                            </button>
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
              onCancel={() => setEditingId(null)}
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
