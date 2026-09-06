import type { ReactNode } from 'react';
import { Outlet } from 'react-router-dom';
import { SidebarNav } from './SidebarNav';
import { TopBar } from './TopBar';
import styles from './AppShell.module.css';

interface AppShellProps {
  /** Test seam: lets a test render arbitrary content without a router outlet. */
  children?: ReactNode;
}

/**
 * The persistent chrome: sidebar, top bar, and a content pane that routes swap
 * beneath. The shell itself never remounts on navigation, so the sidebar keeps
 * its scroll position and the nav does not flicker between routes.
 *
 * DOM order is load-bearing: <aside> precedes <main>. jsdom has no layout
 * engine, so document order is the only structural relationship a test can
 * assert -- offsetWidth is 0 there even for a literal 210px, and a var()-valued
 * longhand reads back unsubstituted.
 */
export function AppShell({ children }: AppShellProps) {
  return (
    <div className={styles.shell}>
      <aside className={styles.sidebar}>
        <SidebarNav />
      </aside>
      <TopBar className={styles.topbar} />
      <main className={styles.content}>
        <div className={styles.contentInner}>{children ?? <Outlet />}</div>
      </main>
    </div>
  );
}
