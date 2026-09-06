import type { ReactNode } from 'react';
import styles from './PageHeader.module.css';

interface PageHeaderProps {
  title: string;
  description?: string;
  /** Right-aligned controls (a refresh button, a filter, a primary action). */
  actions?: ReactNode;
}

/**
 * The one and only page title element (AC2b).
 *
 * It renders inside the content pane, not in the top bar, so each view has
 * exactly one <h1> and the accessibility tree reads title-then-content. A test
 * asserts `getAllByRole('heading', { level: 1 })` has length 1 on a rendered
 * route, which fails the moment a second title is added to the chrome.
 */
export function PageHeader({ title, description, actions }: PageHeaderProps) {
  return (
    <div className={styles.header}>
      <div>
        <h1 className={styles.title}>{title}</h1>
        {description !== undefined && <p className={styles.description}>{description}</p>}
      </div>
      {actions !== undefined && <div className={styles.actions}>{actions}</div>}
    </div>
  );
}
