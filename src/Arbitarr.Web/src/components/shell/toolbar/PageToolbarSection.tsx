import type { ReactNode } from 'react';

import styles from './PageToolbar.module.css';

interface PageToolbarSectionProps {
  /**
   * Which end of the row this group sits at. The *arr convention this follows
   * is context and actions on the left, view/filter menus on the right.
   */
  align?: 'start' | 'end';
  children: ReactNode;
}

/** A group of toolbar controls anchored to one end of the row. */
export function PageToolbarSection({ align = 'start', children }: PageToolbarSectionProps) {
  return (
    <div className={align === 'end' ? styles.sectionEnd : styles.section}>{children}</div>
  );
}
