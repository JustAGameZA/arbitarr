import { Outlet, useLocation } from 'react-router-dom';
import { RouteError } from './RouteError';

/**
 * The shell-level half of the two-level boundary placement (see RouteError.tsx
 * for the split). Mounted as a pathless layout route nested INSIDE the AppShell
 * layout route in routes.tsx, wrapping only the <Outlet /> that renders each
 * page -- not the shell itself -- so a throwing surface is caught below the
 * sidebar and top bar, which stay mounted and keep working.
 *
 * `key={pathname}` is load-bearing, not decorative: RouteError's `hasError`
 * state is set once by getDerivedStateFromError and never cleared on its own,
 * so without a changing key the panel would still be showing after the
 * operator clicks a different nav item -- the boundary has no other way to
 * learn that the child it is guarding has changed underneath it. Keying on
 * the route remounts RouteError (and therefore the guarded page) on every
 * navigation, discarding the stale error state along with the old subtree.
 */
export function RouteErrorOutlet() {
  const { pathname } = useLocation();

  return (
    <RouteError key={pathname}>
      <Outlet />
    </RouteError>
  );
}
