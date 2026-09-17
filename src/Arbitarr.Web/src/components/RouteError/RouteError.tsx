import { Component, type ErrorInfo, type ReactNode } from 'react';
import styles from './RouteError.module.css';
import { APP_NAME } from '../../routes.titles';

/**
 * The tab title shown while the error panel is up.
 *
 * Built from `APP_NAME` rather than a literal, so it follows the same
 * "<Name> — Arbitarr" suffix convention every other title in routes.titles.ts
 * uses instead of inventing a second one here.
 */
const ERROR_TITLE = `Something went wrong — ${APP_NAME}`;

interface RouteErrorProps {
  children: ReactNode;
}

interface RouteErrorState {
  hasError: boolean;
}

/**
 * Error boundary panel, used at two levels of the route tree (routes.tsx).
 *
 * React Router's `errorElement` / `ErrorBoundary` route config is a Data Mode
 * feature (`createBrowserRouter` + `RouterProvider`) -- this app boots with a
 * plain `<BrowserRouter>` and `<Routes>` (main.tsx, routes.tsx), so that prop
 * has no effect here and is not wired up. A plain React error boundary is the
 * only mechanism that actually catches a render-phase throw in Declarative
 * Mode; see the React Router docs on error boundaries for the split between
 * the two modes.
 *
 * Placement 1, the backstop: wrapped directly around `<Routes>` itself in
 * `AppRoutes()`, so it can catch a throw from ANY route element, including the
 * two that render outside the shell (login/setup -- see routes.tsx's #44
 * comment) and a throw from the shell layout route (AppShell) itself. At this
 * level the sidebar and top bar are not siblings of the boundary, so they do
 * not survive a caught error here.
 *
 * Placement 2, the common case: `RouteErrorOutlet` (this directory) wraps only
 * the per-page `<Outlet />`, nested as a pathless layout route INSIDE the
 * AppShell route. A throw from a page surface is caught there, below the
 * chrome, so the sidebar and top bar stay mounted and usable -- without this
 * component reaching into AppShell's own file, which stays untouched. See
 * RouteErrorOutlet's own comment for why it must remount on navigation.
 *
 * Deliberately does not render the caught error's message, stack, or any
 * derived text -- an Error can carry a URL path or other caller-supplied
 * content, and CLAUDE.md's secrets policy treats that the same as a raw
 * secret. It also does not log the error itself; React's own default
 * console.error for a caught render error is left as-is.
 */
export class RouteError extends Component<RouteErrorProps, RouteErrorState> {
  state: RouteErrorState = { hasError: false };

  /**
   * The title AppShell's own pathname-keyed effect had set, captured the
   * moment the panel takes over -- never read back afterwards, only restored
   * on unmount. Captured in `componentDidCatch` rather than at construction:
   * this boundary is remounted per-navigation (`RouteErrorOutlet`'s `key`) and
   * also wraps the whole route tree at the backstop placement, so the "normal"
   * title on ANY given mount is whatever AppShell (or Login/Setup) already put
   * there, not a value this component could know in advance.
   */
  private previousTitle: string | null = null;

  static getDerivedStateFromError(): RouteErrorState {
    return { hasError: true };
  }

  // Required by React to actually invoke the boundary; intentionally does not
  // log, inspect, or forward the error/errorInfo anywhere (see class comment).
  // Also where the tab title switches to the fixed error title: this runs
  // exactly once per caught error, after the render that shows the panel, so
  // it cannot race the AppShell effect that set the surface's normal title
  // moments earlier -- it simply overwrites whatever that left behind.
  componentDidCatch(_error: Error, _errorInfo: ErrorInfo): void {
    // no-op besides the title swap: no logging, no telemetry -- React's own
    // default console.error for a caught render error is the only trace of
    // this left behind.
    this.previousTitle = document.title;
    document.title = ERROR_TITLE;
  }

  /**
   * Restores whatever title was showing before this boundary took over.
   *
   * Only fires when `hasError` is true -- a boundary that never caught
   * anything never touched `document.title` and must not restore a value it
   * never captured. The two effects cannot fight: AppShell's own effect is
   * keyed on `pathname` and reruns independently on every navigation, setting
   * the NEW route's title regardless of what this restores it to first: since
   * `RouteErrorOutlet` keys this component on `pathname`, the boundary that
   * caught the error unmounts (running this) on the very navigation that also
   * reruns AppShell's effect, so whichever order they land in, AppShell's
   * write for the destination route is the one left standing.
   */
  componentWillUnmount(): void {
    if (this.state.hasError && this.previousTitle !== null) {
      document.title = this.previousTitle;
    }
  }

  private handleReload = (): void => {
    window.location.reload();
  };

  render(): ReactNode {
    if (!this.state.hasError) {
      return this.props.children;
    }

    return (
      <div className={styles.panel} role="alert">
        <h1 className={styles.title}>Something went wrong</h1>
        <p className={styles.description}>
          This page ran into a problem and could not continue. Reloading usually fixes it.
        </p>
        <div className={styles.actions}>
          <button type="button" className={styles.button} onClick={this.handleReload}>
            Reload
          </button>
          <a className={styles.link} href="/">
            Back to the dashboard
          </a>
        </div>
      </div>
    );
  }
}
