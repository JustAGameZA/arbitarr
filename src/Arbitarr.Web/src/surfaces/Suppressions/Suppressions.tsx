import { Fragment, useState } from 'react';

import { ApiError } from '../../api/client';
import { PageHeader } from '../../components/shell/PageHeader';
import {
  PageToolbar,
  PageToolbarButton,
  PageToolbarInput,
  PageToolbarSection,
} from '../../components/shell/toolbar';
import { QueryState, errorMessage } from '../QueryState';
import type { SuppressionViewEntry } from '../../api/types';
import styles from '../surface.module.css';
import local from './Suppressions.module.css';
import { DecisionReviewPanel } from './DecisionReview';
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
 *
 * BEHAVIOUR CHANGE, arb-z505: that sentence never reached the screen before.
 * The hand-rolled branches this replaces tested pending FIRST, as
 * `isPending || data === undefined` — and `data` is `undefined` on an error, so
 * the error branch below it was unreachable and every failure, 404 included,
 * rendered a permanent "Loading…" spinner. QueryState tests error first, which
 * is the precedence this component's own comment always assumed, so the
 * sentence now renders for the case it was written for. Pinned by
 * Suppressions.test.tsx's "renders the 404 sentence rather than a permanent
 * spinner" test, which fails against the old ordering.
 */
function Explanation({ releaseIdentifier }: { releaseIdentifier: string }) {
  const explanation = useExplanationQuery(releaseIdentifier);

  return (
    <QueryState
      isPending={explanation.isPending}
      error={explanation.error}
      data={explanation.data}
      renderError={(error) =>
        error instanceof ApiError && error.status === 404
          ? 'No stored explanation for this release. The audit log records the upstream guid only, while the explanation lookup is keyed on the proxy guid, so suppressed releases cannot be resolved to their titles yet.'
          : errorMessage(error)
      }
    >
      {(loaded) => (
        <dl className={local.titles}>
          <dt>Title used for matching</dt>
          <dd>{loaded.title}</dd>
          <dt>Original title</dt>
          <dd>{loaded.originalTitle}</dd>
        </dl>
      )}
    </QueryState>
  );
}

function SuppressionsTable({ entries }: { entries: SuppressionViewEntry[] }) {
  // Only one row's explanation is open at a time: each one is its own request,
  // and expanding them all at once would fire a request per row on a page whose
  // whole purpose is to review a long list.
  const [openRow, setOpenRow] = useState<number | null>(null);

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
            <th>Release</th>
            <th>Query key</th>
            <th>Layer</th>
            <th>Reason</th>
            <th>Enforced</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {entries.map((entry, index) => {
            const isOpen = openRow === index;
            // index alone is unique within this rendered array -- that is
            // all aria-controls needs. The ISO timestamp's ':' and '+' are
            // valid there (getElementById and AT IDREF resolution do not
            // care), but they make the id unsafe to use in a bare '#id' CSS
            // selector, so they are left out rather than included for
            // "extra" uniqueness the index doesn't need.
            const detailId = `suppression-titles-${index}`;

            return (
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
                      aria-expanded={isOpen}
                      aria-controls={isOpen ? detailId : undefined}
                      onClick={() => setOpenRow(isOpen ? null : index)}
                    >
                      {isOpen ? 'Hide titles' : 'Show titles'}
                    </button>
                  </td>
                </tr>
                {isOpen && (
                  <tr>
                    <td id={detailId} colSpan={7}>
                      <Explanation releaseIdentifier={entry.releaseIdentifier} />
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
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
