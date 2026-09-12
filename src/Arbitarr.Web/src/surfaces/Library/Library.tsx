import { PageHeader } from '../../components/shell/PageHeader';
import styles from '../surface.module.css';

/**
 * Library (arb-6l9b.5) — the eighth sidebar surface, placeholder for now.
 *
 * This bead wires the surface into the shell only: no data fetching, no tabs,
 * no query client usage. Bead 6 (arr-queues-screen plan, §4.2) fills this in
 * with the four Sonarr/Radarr queue and library tabs; until then it renders a
 * single empty state so the nav entry and route are real and testable without
 * depending on the backend beads (1/3/4), which this bead does not wait on.
 */
export default function LibraryPage() {
  return (
    <>
      <PageHeader title="Library" />
      <section className={styles.panel}>
        <div className={styles.panelBody}>
          {/* #52's empty-state rule: say what would FILL it, not merely that
              it is empty -- matching Activity.tsx's ActivityTable empty case. */}
          <p className={styles.empty}>
            Sonarr and Radarr queues and libraries will appear here.
          </p>
        </div>
      </section>
    </>
  );
}
