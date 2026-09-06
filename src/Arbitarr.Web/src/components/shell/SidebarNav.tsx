import { forwardRef } from 'react';
import { NavLink } from 'react-router-dom';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import {
  faGauge,
  faMagnifyingGlass,
  faFilter,
  faBan,
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
 * The six addressable nav entries (AC5), in this order.
 *
 * Six, not seven: the five product surfaces are Dashboard, Search, Rules,
 * Settings and Suppressions, and System is the one additional section. An
 * earlier draft counted Settings and System both as product surfaces and again
 * as sections. SidebarNav.test.tsx asserts the count exactly -- not `>=` -- so
 * a dropped entry and a smuggled-in one both fail.
 */
export const NAV_ENTRIES: readonly NavEntry[] = [
  { label: 'Dashboard', to: '/', icon: faGauge },
  { label: 'Search', to: '/search', icon: faMagnifyingGlass },
  { label: 'Rules', to: '/rules', icon: faFilter },
  { label: 'Suppressions', to: '/suppressions', icon: faBan },
  { label: 'Settings', to: '/settings', icon: faGear, group: 'Settings' },
  { label: 'System', to: '/system', icon: faServer, group: 'System' },
];

interface SidebarNavProps {
  /**
   * Below the 768px shell breakpoint the nav is an off-canvas drawer (#48);
   * this toggles the translate that slides it on/off screen. Above the
   * breakpoint it is inert -- the CSS that reads it is itself inside a
   * max-width query -- so desktop callers can simply omit it.
   */
  isOpen?: boolean;
}

/**
 * `ref` is forwarded so AppShell can move focus into the drawer's first
 * focusable element when it opens, and back out to the toggle when it closes.
 */
export const SidebarNav = forwardRef<HTMLElement, SidebarNavProps>(function SidebarNav(
  { isOpen = false },
  ref,
) {
  const navClassName = isOpen ? `${styles.nav} ${styles.navOpen}` : styles.nav;

  return (
    <nav ref={ref} className={navClassName} aria-label="Main">
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
