import type { ReactNode } from 'react';

import styles from './Disclosure.module.css';

interface DisclosureProps {
  /** The always-visible label. Rendered inside `<summary>`, never as a heading. */
  summary: string;
  /**
   * Whether the content starts expanded. Defaults to collapsed, because the
   * only reason to reach for this primitive is that the content is secondary to
   * what surrounds it — a disclosure that is open by default demotes nothing.
   */
  defaultOpen?: boolean;
  children: ReactNode;
}

/**
 * A collapsible region: a native `<details>`/`<summary>` pair (arb-h9gd).
 *
 * NATIVE, NOT HAND-ROLLED, and that is the whole reason this is three lines of
 * markup rather than a component with state. `<details>` supplies the entire
 * disclosure contract for free and correctly: the expanded/collapsed state,
 * `aria-expanded` on the summary, the association between the summary and the
 * region it controls, Enter/Space activation, and — load-bearing for the test
 * that justifies this primitive's existence — removal of the collapsed content
 * from the accessibility tree AND from `display`, so RTL's `getByText` cannot
 * find it while closed. A `useState` + `hidden` reimplementation would have to
 * re-derive every one of those, and #466 (arb-sggv) is currently retrofitting
 * `aria-expanded`/`aria-controls` onto hand-rolled expanders elsewhere in this
 * tree — that is the cost this avoids paying a second time.
 *
 * The summary carries NO heading. `PageHeader` owns the single `<h1>` per route
 * and `.panelHeading` owns the `<h2>` per panel; a disclosure that promoted its
 * own label to a heading would restore exactly the DOM weight that collapsing it
 * was meant to remove, and the acceptance test asserts on heading weight.
 *
 * Deliberately NOT a controlled component and deliberately not persisted: the
 * open state is the browser's, lives for the lifetime of the mounted element,
 * and is never written to storage (the web project's CI guard rejects
 * `localStorage`/`sessionStorage` outright).
 */
export function Disclosure({ summary, defaultOpen = false, children }: DisclosureProps) {
  return (
    <details className={styles.disclosure} open={defaultOpen}>
      <summary className={styles.summary}>{summary}</summary>
      <div className={styles.content}>{children}</div>
    </details>
  );
}
