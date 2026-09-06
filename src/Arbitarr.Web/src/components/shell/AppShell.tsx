import type { ReactNode } from 'react';
import { useEffect, useRef, useState } from 'react';
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
 *
 * Below the 768px shell breakpoint (#48) it also owns the off-canvas drawer's
 * open/close state. This is ephemeral UI state scoped to one mounted
 * component, not app state another surface needs to read, so it is a plain
 * useState here rather than something added to the Zustand stores.
 */
export function AppShell({ children }: AppShellProps) {
  const { pathname } = useLocation();
  const [drawerOpen, setDrawerOpen] = useState(false);
  const drawerRef = useRef<HTMLElement>(null);
  const toggleRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    document.title = resolveDocumentTitle(pathname);
  }, [pathname]);

  // Auto-close on navigation: a drawer left open after following a link would
  // sit on top of the very content the operator just asked to see.
  // setDrawerOpen is stable (useState), so pathname is the only real dependency.
  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  // Move focus into the drawer on open, and back to the toggle on close, so a
  // keyboard user is never left with focus on an element that just left the
  // flow (open) or lost pointer proximity to what they were doing (close).
  // The first nav link, not the <nav> landmark itself, is the target -- a
  // landmark is not normally a tab stop, and a screen reader user expects
  // focus to land on something actionable.
  useEffect(() => {
    if (drawerOpen) {
      drawerRef.current?.querySelector<HTMLElement>('a')?.focus();
    } else {
      toggleRef.current?.focus();
    }
  }, [drawerOpen]);

  useEffect(() => {
    if (!drawerOpen) {
      return undefined;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setDrawerOpen(false);
      }
    };
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [drawerOpen]);

  return (
    <div className={styles.shell}>
      <aside className={styles.sidebar}>
        <SidebarNav ref={drawerRef} isOpen={drawerOpen} />
      </aside>
      {drawerOpen && (
        <div
          className={styles.backdrop}
          onClick={() => setDrawerOpen(false)}
          // Decorative click target, not a control of its own: Escape and the
          // toggle button both close the drawer too, so this does not need to
          // be independently keyboard-operable.
          aria-hidden="true"
        />
      )}
      <TopBar
        className={styles.topbar}
        drawerOpen={drawerOpen}
        onToggleDrawer={() => setDrawerOpen((open) => !open)}
        toggleRef={toggleRef}
      />
      <main className={styles.content}>
        <div className={styles.contentInner}>{children ?? <Outlet />}</div>
      </main>
    </div>
  );
}
