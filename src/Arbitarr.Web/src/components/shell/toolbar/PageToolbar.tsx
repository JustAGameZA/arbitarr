import type { ReactNode } from 'react';

import styles from './PageToolbar.module.css';

interface PageToolbarProps {
  /**
   * The toolbar's accessible name. Required, not optional: an unnamed
   * role="toolbar" is an anonymous landmark in the accessibility tree, and a
   * page that grows a second toolbar would give a screen-reader user two
   * indistinguishable ones.
   */
  label: string;
  children: ReactNode;
}

/**
 * The page toolbar row — filters and view controls, directly under PageHeader.
 *
 * It is a SIBLING of PageHeader rather than its `actions` slot: `actions`
 * renders inline with the <h1> on the title row, and the *arr shell this is
 * modelled on puts these controls on their own row beneath it.
 *
 * It deliberately renders NO heading element of any level. PageHeader owns the
 * one <h1> per route (AC2b, asserted by pageTitle.test.tsx); a toolbar that
 * captioned itself with a heading would either break that rule or add a
 * second-level heading to a row that is a control strip, not a section.
 */
export function PageToolbar({ label, children }: PageToolbarProps) {
  return (
    <div role="toolbar" aria-label={label} className={styles.toolbar}>
      {children}
    </div>
  );
}
