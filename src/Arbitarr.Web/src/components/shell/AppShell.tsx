import type { ReactNode } from 'react';
import { useEffect, useRef, useState } from 'react';
import { Outlet, useLocation } from 'react-router-dom';
import { SidebarNav } from './SidebarNav';
import { TopBar } from './TopBar';
import { ERROR_TITLE, resolveDocumentTitle } from '../../routes.titles';
import { RouteErrorContext } from '../RouteError/RouteErrorContext';
import { useLiveStatusStore } from '../../state/liveStatusStore';
import { useTableDensityStore } from '../../state/tableDensityStore';
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
 * It is also the single writer of `document.title` overall: RouteError (the
 * error boundary nested below this shell, routes.tsx's "Placement 2") does
 * not touch `document.title` itself, it signals this component through
 * `RouteErrorContext` and this effect substitutes `ERROR_TITLE` while the
 * panel is up. See RouteErrorContext.tsx for why that indirection exists.
 *
 * Below the 768px shell breakpoint (#48) it also owns the off-canvas drawer's
 * open/close state. This is ephemeral UI state scoped to one mounted
 * component, not app state another surface needs to read, so it is a plain
 * useState here rather than something added to the Zustand stores.
 */
export function AppShell({ children }: AppShellProps) {
  const { pathname } = useLocation();
  const density = useTableDensityStore((state) => state.density);
  const liveStatusMessage = useLiveStatusStore((state) => state.message);
  const liveStatusSeq = useLiveStatusStore((state) => state.seq);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [routeErrored, setRouteErrored] = useState(false);
  const drawerRef = useRef<HTMLElement>(null);
  const toggleRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    document.title = routeErrored ? ERROR_TITLE : resolveDocumentTitle(pathname);
  }, [pathname, routeErrored]);

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
      {/*
        The one shared aria-live region (arb-tku8). `polite` so it queues
        behind whatever the screen reader is already announcing rather than
        interrupting it the way the existing `role="alert"` errors do --
        pending/success states are not urgent enough to earn that. Mounted
        here, unconditionally and always with the same identity, for the same
        reason the document-title effect lives on the shell rather than a
        per-page hook: a per-surface region is the thing this bead replaces,
        and a component that unmounts on navigation would drop whatever
        announcement was in flight when a route change happens to land.

        `key={liveStatusSeq}` forces React to tear down and remount this
        element on every `announce`, even when the new message string is
        identical to the last one (liveStatusStore.ts's `seq`) -- without it,
        two consecutive identical "Saved." announcements leave `state.message`
        unchanged, AppShell (which selects the primitive `state.message`)
        never re-renders, and the second announcement never reaches the DOM
        for a screen reader to pick up. `seq` itself never renders, only the
        message text does, so this does not change what is announced -- only
        that a repeat is guaranteed to produce a DOM mutation an assistive
        technology can detect. A live region briefly leaving and re-entering
        the accessibility tree on each announcement is how AT already expects
        to pick up a change to one; that already happens for every attribute
        or text change, keyed or not.
      */}
      <p key={liveStatusSeq} className={styles.liveStatus} role="status" aria-live="polite">
        {liveStatusMessage}
      </p>
      {/*
        `data-open` drives the off-canvas transform below 768px (arb-759); above
        the breakpoint it is inert, because the only CSS that reads it sits
        inside the max-width query. It is therefore safe to write it
        unconditionally, which is necessary: JS cannot see a media query any
        more than jsdom can.

        Deliberately NOT mirrored into `aria-hidden`. That would have to be
        written unconditionally too, and on desktop -- where the sidebar is
        permanently visible and `drawerOpen` is permanently false -- it would
        hide the whole primary navigation from screen readers, a worse defect
        than the one being fixed. The `visibility: hidden` in the mobile rule
        removes the closed drawer from the accessibility tree and from the tab
        order at exactly the width where that is correct, and nowhere else.
      */}
      <aside className={styles.sidebar} data-open={drawerOpen}>
        <SidebarNav ref={drawerRef} />
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
        {/*
          `data-density` is read by ONE rule in surface.module.css, which drops
          the vertical padding of every `.table` beneath it (arb-br4). It is
          written here rather than per-surface because ten surfaces render that
          table class: one attribute on the always-mounted wrapper means a new
          table follows the preference by existing inside the shell, with no
          call site to remember. Row height only -- see the rule's comment.
        */}
        <div className={styles.contentInner} data-density={density}>
          <RouteErrorContext.Provider value={setRouteErrored}>
            {children ?? <Outlet />}
          </RouteErrorContext.Provider>
        </div>
      </main>
    </div>
  );
}
