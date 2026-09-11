import styles from './PageToolbar.module.css';

interface PageToolbarButtonProps {
  label: string;
  onClick: () => void;
  disabled?: boolean;
}

/** A plain action button sized for the toolbar row. */
export function PageToolbarButton({ label, onClick, disabled = false }: PageToolbarButtonProps) {
  return (
    <button type="button" className={styles.button} onClick={onClick} disabled={disabled}>
      {label}
    </button>
  );
}
