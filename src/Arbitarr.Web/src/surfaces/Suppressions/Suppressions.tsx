import { Fragment, useState } from 'react';

import { ApiError } from '../../api/client';
import { PageHeader } from '../../components/shell/PageHeader';
import { QueryState, errorMessage } from '../QueryState';
import type { SuppressionViewEntry } from '../../api/types';
import styles from '../surface.module.css';
import local from './Suppressions.module.css';
import { useExplanationQuery, useSuppressionsQuery } from './queries';

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

/**
 * The original-vs-rewritten title pair for one suppressed release (AC11).
 *
 * The 404 branch is the expected outcome today, not an edge case: see the gap
 * documented on useExplanationQuery. It is written as a sentence that names the
 * cause, because "404" on its own would read as a bug in this page rather than
 * a missing column in the audit log.
 */
function Explanation({ releaseIdentifier }: { releaseIdentifier: string }) {
  const explanation = useExplanationQuery(releaseIdentifier);

  if (explanation.isPending || explanation.data === undefined) {
    return <p className={styles.muted}>Loading…</p>;
  }

  if (explanation.error !== null) {
    return (
      <p className={styles.error} role="alert">
        {explanation.error instanceof ApiError && explanation.error.status === 404
          ? 'No stored explanation for this release. The audit log records the upstream guid only, while the explanation lookup is keyed on the proxy guid, so suppressed releases cannot be resolved to their titles yet.'
          : errorMessage(explanation.error)}
      </p>
    );
  }

  return (
    <dl className={local.titles}>
      <dt>Title used for matching</dt>
      <dd>{explanation.data.title}</dd>
      <dt>Original title</dt>
      <dd>{explanation.data.originalTitle}</dd>
    </dl>
  );
}

function SuppressionsTable({ entries }: { entries: SuppressionViewEntry[] }) {
  // Only one row's explanation is open at a time: each one is its own request,
  // and expanding them all at once would fire a request per row on a page whose
  // whole purpose is to review a long list.
  const [openRow, setOpenRow] = useState<number | null>(null);

  if (entries.length === 0) {
    return <p className={styles.empty}>No suppressed or de-ranked results.</p>;
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Occurred</th>
            <th>Release</th>
            <th>Query key</th>
            <th>Layer</th>
            <th>Reason</th>
            <th>Enforced</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {entries.map((entry, index) => (
            <Fragment key={`${entry.occurredAt}:${entry.releaseIdentifier}:${index}`}>
              <tr>
                <td>{formatTimestamp(entry.occurredAt)}</td>
                <td className={local.identifier}>{entry.releaseIdentifier}</td>
                <td>{entry.queryKey}</td>
                {/* The layer that acted: a rule name for the rule-engine
                    layers, or a stable label such as "ai"/"pass" for the
                    others. This is the attribution AC11 asks for. */}
                <td>
                  <span className={styles.badge}>{entry.layer}</span>
                </td>
                <td>{entry.reason}</td>
                {/* shadowMode means the decision was RECORDED BUT NOT ENFORCED.
                    The legacy page printed the raw flag as "yes"/"no" under a
                    "Shadow Mode" heading, which inverts the sense an operator
                    reads at a glance: "yes" looked like the suppression
                    happened. Naming the column for the consequence removes the
                    double negative. */}
                <td>
                  {entry.shadowMode ? (
                    <span className={`${styles.badge} ${styles.badgeWarn}`}>Shadow only</span>
                  ) : (
                    <span className={`${styles.badge} ${styles.badgeDanger}`}>Suppressed</span>
                  )}
                </td>
                <td>
                  <button
                    type="button"
                    className={styles.buttonSecondary}
                    onClick={() => setOpenRow(openRow === index ? null : index)}
                  >
                    {openRow === index ? 'Hide titles' : 'Show titles'}
                  </button>
                </td>
              </tr>
              {openRow === index && (
                <tr>
                  <td colSpan={7}>
                    <Explanation releaseIdentifier={entry.releaseIdentifier} />
                  </td>
                </tr>
              )}
            </Fragment>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Suppressions (AC11).
 *
 * A read-only view over the append-only suppression audit log: every
 * suppressed-or-de-ranked result, attributed to the layer that acted, with the
 * reason recorded at the time and the original-vs-rewritten title pair on
 * demand.
 */
export default function SuppressionsPage() {
  // The submitted filter, not the typed one: the query key is a server-side
  // WHERE, so refetching on every keystroke would issue a request per character.
  const [draft, setDraft] = useState('');
  const [filter, setFilter] = useState('');
  const suppressions = useSuppressionsQuery(filter);

  return (
    <>
      <PageHeader
        title="Suppressions"
        description="Every suppressed or de-ranked result, attributed to the layer that acted."
      />

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Filter</h2>
        <div className={styles.panelBody}>
          <form
            className={styles.form}
            onSubmit={(event) => {
              event.preventDefault();
              setFilter(draft);
            }}
          >
            <label className={styles.field}>
              Query key
              <input
                className={styles.input}
                value={draft}
                onChange={(event) => setDraft(event.target.value)}
                placeholder="All queries"
              />
            </label>
            <button type="submit" className={styles.button}>
              Apply
            </button>
          </form>
        </div>
      </section>

      <section className={styles.panel}>
        <h2 className={styles.panelHeading}>Decisions</h2>
        <div className={styles.panelBody}>
          <QueryState
            isPending={suppressions.isPending}
            error={suppressions.error}
            data={suppressions.data}
          >
            {(entries) => <SuppressionsTable entries={entries} />}
          </QueryState>
        </div>
      </section>
    </>
  );
}
