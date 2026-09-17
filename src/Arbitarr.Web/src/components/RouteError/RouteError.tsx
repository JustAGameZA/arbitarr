import { Component, type ErrorInfo, type ReactNode } from 'react';
import styles from './RouteError.module.css';

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

  static getDerivedStateFromError(): RouteErrorState {
    return { hasError: true };
  }

  // Required by React to actually invoke the boundary; intentionally does not
  // log, inspect, or forward the error/errorInfo anywhere (see class comment).
  componentDidCatch(_error: Error, _errorInfo: ErrorInfo): void {
    // no-op: no logging, no telemetry -- React's own default console.error
    // for a caught render error is the only trace of this left behind.
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
