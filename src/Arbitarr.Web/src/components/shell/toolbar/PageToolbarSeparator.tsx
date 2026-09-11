import styles from './PageToolbar.module.css';

/**
 * A visual divider between groups of toolbar controls.
 *
 * `aria-hidden` on purpose: it carries no information a screen reader could
 * use, and role="separator" inside a toolbar would announce a boundary that
 * means nothing to a non-visual reader.
 */
export function PageToolbarSeparator() {
  return <span aria-hidden="true" className={styles.separator} />;
}
