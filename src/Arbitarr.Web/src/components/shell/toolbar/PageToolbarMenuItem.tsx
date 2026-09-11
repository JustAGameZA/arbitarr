import styles from './PageToolbar.module.css';

interface PageToolbarMenuItemProps {
  label: string;
  /**
   * Whether this item is the active one in its group. It drives `aria-checked`,
   * which is how a screen-reader user learns the current filter — the visual
   * tick alone does not reach them.
   */
  checked: boolean;
  /**
   * 'radio' for a single-select group (pick one kind, one time window),
   * 'checkbox' for an independent toggle.
   */
  kind?: 'radio' | 'checkbox';
  onSelect: () => void;
}

/** One selectable row inside a PageToolbarMenu panel. */
export function PageToolbarMenuItem({
  label,
  checked,
  kind = 'radio',
  onSelect,
}: PageToolbarMenuItemProps) {
  return (
    <button
      type="button"
      role={kind === 'checkbox' ? 'menuitemcheckbox' : 'menuitemradio'}
      aria-checked={checked}
      className={styles.menuItem}
      onClick={onSelect}
    >
      {label}
    </button>
  );
}
