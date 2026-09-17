import { Component, type ContextType, type ErrorInfo, type ReactNode } from 'react';
import styles from './RouteError.module.css';
import { RouteErrorContext } from './RouteErrorContext';

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

  static contextType = RouteErrorContext;
  declare context: ContextType<typeof RouteErrorContext>;

  static getDerivedStateFromError(): RouteErrorState {
    return { hasError: true };
  }

  // Required by React to actually invoke the boundary; intentionally does not
  // log, inspect, or forward the error/errorInfo anywhere (see class comment).
  //
  // Also where AppShell is told the panel is up. This boundary used to write
  // `document.title` directly here, but a class component's `componentDidCatch`
  // runs during React's commit phase, while AppShell's pathname-keyed title
  // effect is a passive effect that runs AFTER commit completes for the whole
  // tree -- and since this boundary is nested INSIDE AppShell at the common
  // placement (routes.tsx's "Placement 2"), AppShell's effect always ran
  // after and silently overwrote whatever title this set. Signaling through
  // context instead removes the race by construction: AppShell is the only
  // thing that ever assigns `document.title`, keyed on `[pathname,
  // routeErrored]`, so it is free to fold this state in without anything
  // downstream racing it.
  componentDidCatch(_error: Error, _errorInfo: ErrorInfo): void {
    // no-op besides the signal: no logging, no telemetry -- React's own
    // default console.error for a caught render error is the only trace of
    // this left behind.
    this.context(true);
  }

  /**
   * Tells AppShell the panel is gone, so its title effect stops substituting
   * the error title.
   *
   * Only fires when `hasError` is true -- a boundary that never caught
   * anything never signaled AppShell and must not un-signal something it
   * never set. Ordering versus AppShell's own effect does not matter here,
   * unlike the old direct-title-write design: `RouteErrorOutlet` keys this
   * component on `pathname`, so on navigation this unmount (and this call)
   * lands in the same commit as the pathname change that reruns AppShell's
   * effect. AppShell reads `routeErrored` as plain state, not a value this
   * component hands it moment-to-moment, so whichever order the unmount and
   * AppShell's effect run in, AppShell ends up rendering the destination
   * route's own title with `routeErrored` correctly back at `false`.
   */
  componentWillUnmount(): void {
    if (this.state.hasError) {
      this.context(false);
    }
  }

  // Reload is a full `window.location.reload()`, not an in-place state reset:
  // the whole page (and this component) is torn down and rebuilt from
  // scratch, so there is no code path here that could leave AppShell's
  // `routeErrored` stuck at `true` after the operator reloads.
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
