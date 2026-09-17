import { useState } from 'react';

import { PageHeader } from '../../components/shell/PageHeader';
import {
  PageToolbar,
  PageToolbarButton,
  PageToolbarInput,
  PageToolbarSection,
} from '../../components/shell/toolbar';
import { QueryState } from '../QueryState';
import type { SuppressionViewEntry } from '../../api/types';
import styles from '../surface.module.css';
import local from './Suppressions.module.css';
import { DecisionReviewPanel } from './DecisionReview';
import { useSuppressionsQuery } from './queries';

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

function SuppressionsTable({ entries }: { entries: SuppressionViewEntry[] }) {
  if (entries.length === 0) {
    return (
      <p className={styles.empty}>
        Nothing suppressed or de-ranked yet. Entries appear here once a rule or the AI layer acts on a
        release.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Occurred</th>
            {/* This is the upstream identifier the audit row was written with
                (Candidate.Guid), not a release title -- there is no title in
                this row to show. See the KNOWN BACKEND GAP note above the
                identifier cell for why no row can offer a titles lookup from
                it today. */}
            <th>Upstream identifier</th>
            <th>Query key</th>
            <th>Layer</th>
            <th>Reason</th>
            <th>Enforced</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry, index) => (
            <tr key={`${entry.occurredAt}:${entry.releaseIdentifier}:${index}`}>
              <td>{formatTimestamp(entry.occurredAt)}</td>
              {/* KNOWN BACKEND GAP: this is the raw upstream identifier the
                  audit log stores (Candidate.Guid), never a release title,
                  and the explanation lookup this surface used to offer here is
                  keyed on ProxyGuid -- a different value this row does not
                  carry (see queries.ts's history for the full chain). Every
                  such lookup would 404, so no "Show titles" control is
                  offered from this column; closing that gap needs a schema
                  change under src/Arbitarr.Api/ and src/Arbitarr.Data/ plus
                  the open product question of whether the audit log should
                  carry the lookup key at all. The value can still run long,
                  so it keeps the title attribute + selectable-text pattern
                  Activity uses for its own long machine-shaped values. */}
              <td className={local.identifier} title={entry.releaseIdentifier}>
                {entry.releaseIdentifier}
              </td>
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
            </tr>
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
 * reason recorded at the time. It no longer offers an original-vs-rewritten
 * title lookup per row: see the KNOWN BACKEND GAP comment on the identifier
 * column for why that control could only ever 404.
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

      {/* The toolbar is a sibling of PageHeader, not its `actions` slot: the
          filter belongs on its own row under the title, the way the *arr shell
          arranges them (arb-ajrv; this was a `.panel` headed "Filter" until
          then).

          EXPLICIT SUBMIT IS KEPT. The query key is a server-side WHERE, so the
          alternative -- debounced live filtering like the Logs tab -- would
          issue a request per pause on a free-text field whose values are long
          machine-shaped strings a user pastes rather than types. Migrating the
          panel was never meant to change when the request fires, and the
          difference is visible to the operator, so Apply stays and
          PageToolbarButton finally has a caller. */}
      <PageToolbar label="Suppression filters">
        <PageToolbarSection align="end">
          {/* Enter still applies the filter. The <form> this replaced gave that
              for free via its submit button; PageToolbarButton is a
              type="button" by design, so the keyboard path is wired here
              explicitly rather than being silently dropped. */}
          <PageToolbarInput
            label="Query key"
            value={draft}
            placeholder="All queries"
            onChange={setDraft}
            onSubmit={() => setFilter(draft)}
          />
          <PageToolbarButton label="Apply" onClick={() => setFilter(draft)} />
        </PageToolbarSection>
      </PageToolbar>

      <section className={styles.panel}>
        {/* "Suppression audit log", not the bare "Decisions" it was called when
            this was the only table on the surface. #54 added a second table of
            genuinely different rows below, and two panels both called some
            variant of "decisions" would read as duplicates of one list rather
            than as the two distinct stores they are. */}
        <h2 className={styles.panelHeading}>Suppression audit log</h2>
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

      {/* The reviewable half (#54). A separate panel because it reads a
          different store — see DecisionReviewPanel's own note on why these
          rows, and not the audit-log rows above, are the ones that can carry a
          verdict. */}
      <DecisionReviewPanel />
    </>
  );
}
