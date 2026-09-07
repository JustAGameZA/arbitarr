import { Fragment, useState } from 'react';

import { QueryState, errorMessage } from '../QueryState';
import type { DecisionEntry, ReviewVerdict } from '../../api/types';
import styles from '../surface.module.css';
import local from './Suppressions.module.css';
import {
  type ShadowModeFilter,
  useDecisionsQuery,
  useReviewDecisionMutation,
} from './decisionQueries';

function formatTimestamp(value: string): string {
  return new Date(value).toLocaleString();
}

/**
 * The review controls for one decision.
 *
 * Renders the EXISTING verdict when there is one, because the write is
 * idempotent per decision: reviewing again updates the row rather than
 * appending, so the operator needs to see what they are about to change. A
 * plain pair of buttons with no current-state feedback would make re-reviewing
 * feel like it silently did nothing.
 *
 * The note is optional and only submitted alongside a verdict — a note with no
 * verdict is not a review, and the endpoint rejects it, so the UI never offers
 * that as a reachable state.
 */
function ReviewControls({ decision, onReviewed }: { decision: DecisionEntry; onReviewed: () => void }) {
  const [note, setNote] = useState(decision.reviewNote ?? '');
  const review = useReviewDecisionMutation();

  const submit = (verdict: ReviewVerdict) => {
    const trimmed = note.trim();
    review.mutate(
      {
        id: decision.id,
        // Omitted rather than sent as '' when empty: the column is nullable, and
        // an empty string would record a note the operator did not write.
        review: trimmed === '' ? { verdict } : { verdict, note: trimmed },
      },
      // Collapsed only on SUCCESS, so the row's refreshed verdict badge becomes
      // the confirmation. Left open on failure, because the error belongs beside
      // the note the operator typed and would otherwise be thrown away with it.
      { onSuccess: onReviewed },
    );
  };

  return (
    <div className={local.review}>
      <label className={local.reviewNote}>
        <span className={styles.muted}>Note (optional)</span>
        <input
          className={styles.input}
          value={note}
          onChange={(event) => setNote(event.target.value)}
          placeholder="Why was this right or wrong?"
          aria-label={`Review note for decision ${decision.id}`}
        />
      </label>

      <div className={local.reviewActions}>
        <button
          type="button"
          className={styles.button}
          disabled={review.isPending}
          onClick={() => submit('agree')}
          aria-pressed={decision.reviewVerdict === 'agree'}
        >
          Agree
        </button>
        <button
          type="button"
          className={styles.buttonDanger}
          disabled={review.isPending}
          onClick={() => submit('disagree')}
          aria-pressed={decision.reviewVerdict === 'disagree'}
        >
          Disagree
        </button>
      </div>

      {review.error !== null && (
        <p className={styles.error} role="alert">
          {errorMessage(review.error)}
        </p>
      )}
    </div>
  );
}

/**
 * The verdict a decision already carries, or nothing at all while unreviewed.
 *
 * Unreviewed renders NO badge rather than a neutral "Unreviewed" one: the table
 * is mostly unreviewed rows by nature, and a badge on every one of them would
 * make the reviewed rows — the ones worth spotting — harder to find, not easier.
 */
function VerdictBadge({ decision }: { decision: DecisionEntry }) {
  if (decision.reviewVerdict === null) {
    return <span className={styles.muted}>Not reviewed</span>;
  }

  const agreed = decision.reviewVerdict === 'agree';

  return (
    <span
      className={`${styles.badge} ${agreed ? styles.badgeOk : styles.badgeDanger}`}
      title={decision.reviewedAt === null ? undefined : `Reviewed ${formatTimestamp(decision.reviewedAt)}`}
    >
      {agreed ? 'Agreed' : 'Disagreed'}
    </span>
  );
}

function DecisionsTable({ decisions }: { decisions: DecisionEntry[] }) {
  // One row's review form open at a time: the note input holds per-row draft
  // state, and rendering an input for every row would put dozens of live
  // uncommitted drafts on screen with no way to tell which one is focused.
  const [openRow, setOpenRow] = useState<number | null>(null);

  if (decisions.length === 0) {
    return (
      <p className={styles.empty}>
        No pipeline decisions recorded yet. Run a search — each result the rule engine or the AI
        layer judges is recorded here, and you can then agree or disagree with it.
      </p>
    );
  }

  return (
    <div className={styles.tableScroll}>
      <table className={styles.table}>
        <thead>
          <tr>
            <th>Occurred</th>
            <th>Decision</th>
            <th>Reason</th>
            <th>Enforced</th>
            <th>Your verdict</th>
            <th />
          </tr>
        </thead>
        <tbody>
          {decisions.map((decision) => (
            <Fragment key={decision.id}>
              <tr>
                <td>{formatTimestamp(decision.occurredAt)}</td>
                <td>{decision.summary}</td>
                <td>{decision.reason ?? ''}</td>
                {/* Same wording as the audit-log table above: shadowMode means
                    RECORDED BUT NOT ENFORCED, and naming the column for the
                    consequence avoids the double negative a raw yes/no creates.
                    null means the row predates the flag being recorded, which is
                    not the same as "enforced" and must not render as it. */}
                <td>
                  {decision.shadowMode === null ? (
                    <span className={styles.muted}>Unknown</span>
                  ) : decision.shadowMode ? (
                    <span className={`${styles.badge} ${styles.badgeWarn}`}>Shadow only</span>
                  ) : (
                    <span className={`${styles.badge} ${styles.badgeDanger}`}>Suppressed</span>
                  )}
                </td>
                <td>
                  <VerdictBadge decision={decision} />
                </td>
                <td>
                  <button
                    type="button"
                    className={styles.buttonSecondary}
                    onClick={() => setOpenRow(openRow === decision.id ? null : decision.id)}
                  >
                    {openRow === decision.id
                      ? 'Close'
                      : decision.reviewVerdict === null
                        ? 'Review'
                        : 'Change'}
                  </button>
                </td>
              </tr>
              {openRow === decision.id && (
                <tr>
                  <td colSpan={6}>
                    <ReviewControls decision={decision} onReviewed={() => setOpenRow(null)} />
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
 * The reviewable pipeline decisions panel (#54 step 6).
 *
 * WHY THIS IS A SECOND PANEL RATHER THAN COLUMNS ON THE AUDIT-LOG TABLE ABOVE.
 * DO NOT TIDY THE TWO PANELS INTO ONE: they read different stores, and the join
 * key that would let you merge them does not exist.
 *
 *   - The table above is projected from the SUPPRESSION AUDIT LOG, as
 *     SuppressionViewEntryResponse — six fields, carrying no row id.
 *   - This table reads the SHARED EVENT STORE (EventKind.Decision rows), and the
 *     review POST addresses one by DecisionEntryResponse.Id.
 *
 * THE TRAP: SuppressionAuditLogEntry.Id DOES exist as a surrogate primary key —
 * it is simply not projected onto the wire. So it looks like the fix is to add
 * it to SuppressionViewEntryResponse and wire a review button to it. That would
 * compile, render, and be wrong: it is an id in a DIFFERENT ID SPACE, and
 * POSTing it would review whichever unrelated event row happens to share that
 * number, silently recording verdicts against the wrong decisions.
 *
 * Nor can the two rows be matched by content: the audit row keeps only
 * Candidate.Guid, because SuppressionAuditLogMapper drops the SourceName half of
 * the identity — the same missing input that leaves useExplanationQuery unable
 * to resolve a proxy guid. Genuinely joining these needs a schema change
 * (persist SourceName, or the ProxyGuid itself), which is a backend change, not
 * a UI one.
 *
 * Both panels live on this one surface deliberately (plan §3.3): decisions stay
 * in one place and no seventh sidebar entry is added.
 */
export function DecisionReviewPanel() {
  const [filter, setFilter] = useState<ShadowModeFilter>('all');
  // Paging is deliberately not wired yet: the cursor is threaded through the
  // query so adding a "Load more" is a UI change only, but a review queue whose
  // first page is the most recent decisions answers the operator's question
  // without one.
  const decisions = useDecisionsQuery(filter, null);

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Pipeline decisions</h2>
      <div className={styles.panelBody}>
        <div className={local.filterRow}>
          <label className={styles.field}>
            Enforcement
            <select
              className={styles.select}
              value={filter}
              onChange={(event) => setFilter(event.target.value as ShadowModeFilter)}
            >
              {/* Worded as the FILTER ("Shadow only decisions"), not as the
                  badge ("Shadow only"). The badge states what one row is; the
                  option states what the list is narrowed to. Reusing the badge's
                  exact words made the two indistinguishable to a text query, and
                  a reader scanning the page hit the same ambiguity. */}
              <option value="all">All decisions</option>
              <option value="shadow">Shadow-only decisions</option>
              <option value="live">Enforced decisions</option>
            </select>
          </label>
        </div>

        <QueryState
          isPending={decisions.isPending}
          error={decisions.error}
          data={decisions.data}
        >
          {(page) => <DecisionsTable decisions={page.decisions} />}
        </QueryState>
      </div>
    </section>
  );
}
