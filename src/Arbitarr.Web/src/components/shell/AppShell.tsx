import type { ReactNode } from 'react';
import { useEffect } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { SidebarNav } from './SidebarNav';
import { TopBar } from './TopBar';
import { resolveDocumentTitle } from '../../routes.titles';
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
 *
 * Because the shell never remounts, it is also the single always-mounted place
 * to keep the browser tab title in sync with the route (documentTitle.test.tsx):
 * a per-page hook would need six call sites and a new surface could silently
 * forget it, so this effect -- keyed on the pathname -- is the one source.
 */
export function AppShell({ children }: AppShellProps) {
  const { pathname } = useLocation();

  useEffect(() => {
    document.title = resolveDocumentTitle(pathname);
  }, [pathname]);

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
