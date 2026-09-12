import { forwardRef } from 'react';
import { NavLink } from 'react-router-dom';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import {
  faGauge,
  faMagnifyingGlass,
  faFilter,
  faBan,
  faClockRotateLeft,
  faListCheck,
  faGear,
  faServer,
  type IconDefinition,
} from '@fortawesome/free-solid-svg-icons';
import styles from './SidebarNav.module.css';

interface NavEntry {
  label: string;
  to: string;
  icon: IconDefinition;
  /** Optional group heading rendered above this entry. */
  group?: string;
}

/**
 * The eight addressable nav entries (AC5), in this order.
 *
 * EIGHT as of arb-6l9b.5, which added Library. It was seven before that, since
 * #55 added Activity, and six before that: the five product surfaces were
 * Dashboard, Search, Rules, Suppressions and Settings, with System as the one
 * additional section. An earlier draft counted Settings and System both as
 * product surfaces and again as sections, which is why this comment states the
 * accounting rather than just asserting a number.
 *
 * Activity is a product surface, not a section: it answers an operator question
 * ("what did Arbitarr do, and why") the way Suppressions does, rather than
 * being configuration or diagnostics. It sits beside Suppressions because the
 * two read the same decisions from opposite ends -- Suppressions lists what was
 * withheld, Activity lists everything that happened, those included.
 *
 * Library is likewise a product surface: it answers "what does each configured
 * *arr have queued and in its library", the same shape of operator question as
 * Activity and Suppressions, not configuration or diagnostics.
 *
 * SidebarNav.test.tsx asserts the count exactly -- not `>=` -- so a dropped
 * entry and a smuggled-in one both fail. A new surface means updating that test,
 * routes.tsx and routes.titles.ts in the same change; this comment exists to
 * catch the case where someone adds an entry here and nowhere else.
 */
export const NAV_ENTRIES: readonly NavEntry[] = [
  { label: 'Dashboard', to: '/', icon: faGauge },
  { label: 'Search', to: '/search', icon: faMagnifyingGlass },
  { label: 'Rules', to: '/rules', icon: faFilter },
  { label: 'Suppressions', to: '/suppressions', icon: faBan },
  { label: 'Activity', to: '/activity', icon: faClockRotateLeft },
  { label: 'Library', to: '/library', icon: faListCheck },
  { label: 'Settings', to: '/settings', icon: faGear, group: 'Settings' },
  { label: 'System', to: '/system', icon: faServer, group: 'System' },
];

/**
 * `ref` is forwarded so AppShell can move focus into the drawer's first
 * focusable element when it opens, and back out to the toggle when it closes.
 *
 * The nav takes NO open/closed prop. Below the 768px shell breakpoint it is the
 * <aside> around it that slides off-canvas, driven by `data-open` in
 * AppShell.module.css (arb-759). A second open-state class here would be a
 * competing transform on a nested element, which is the defect that fix
 * removed.
 */
export const SidebarNav = forwardRef<HTMLElement>(function SidebarNav(_props, ref) {
  return (
    <nav ref={ref} className={styles.nav} aria-label="Main">
      <div className={styles.brand}>Arbitarr</div>
      {NAV_ENTRIES.map((entry) => (
        <div key={entry.to}>
          {entry.group !== undefined && <div className={styles.groupLabel}>{entry.group}</div>}
          <NavLink
            to={entry.to}
            // `end` only on the index route: without it "/" would match every
            // path and the Dashboard row would stay active on every surface.
            end={entry.to === '/'}
            className={({ isActive }) =>
              isActive ? `${styles.item} ${styles.itemActive}` : styles.item
            }
          >
            <FontAwesomeIcon icon={entry.icon} className={styles.icon} fixedWidth />
            <span>{entry.label}</span>
          </NavLink>
        </div>
      ))}
    </nav>
  );
});
