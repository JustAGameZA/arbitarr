import type { ReactNode } from 'react';
import { Navigate, useLocation } from 'react-router-dom';

import { useSessionQuery } from '../../state/sessionQueries';

interface RequireSessionProps {
  children: ReactNode;
}

/**
 * #44: sends an unauthenticated human to /login, or to /setup on a fresh install.
 *
 * ## Why this cannot loop, stated as the rule it follows
 *
 * The plan promotes "a login redirect never loops" to a test, and `TopBar`'s
 * load-bearing comment records the same concern in its older form: never leave
 * the operator somewhere they cannot act. Three properties give that here, and
 * all three matter:
 *
 * 1. The `/login` and `/setup` routes are NOT wrapped by this guard. They are
 *    siblings of the guarded shell in the route table, not children of it. A
 *    guard that wrapped its own redirect target is the classic loop, and no
 *    amount of care inside this component would fix it -- the route table is
 *    what prevents it.
 *
 * 2. It redirects only on a DEFINITE "no". While the session query is
 *    loading, and if it ERRORS, the children render. A guard that redirected on
 *    error would strand the operator at a login page during any backend hiccup,
 *    and -- worse -- would do it on a login page whose own submit needs the same
 *    backend. Failing open here is safe because it is not the security boundary:
 *    every gated route is enforced server-side by `AdminApiKeyFilter`, so the
 *    worst case of rendering the shell without a session is surfaces that show
 *    their own 401 states. This component is a convenience, and treating it as
 *    the gate would be the actual mistake.
 *
 * 3. The two destinations are mutually exclusive and chosen from ONE answer:
 *    `setupRequired` and `authenticated` come from a single response, so the
 *    guard cannot bounce between /login and /setup on inconsistent reads.
 */
export function RequireSession({ children }: RequireSessionProps) {
  const { data, isLoading, isError } = useSessionQuery();
  const location = useLocation();

  // Property 2: no redirect without a definite answer. `data` is undefined while
  // loading and on error, and both of those render the children.
  if (isLoading || isError || data === undefined) {
    return <>{children}</>;
  }

  if (data.authenticated) {
    return <>{children}</>;
  }

  // A fresh install has nothing to log into; sending them to /login would
  // present a form no credential can satisfy -- the same "stranded operator"
  // shape TopBar's comment warns about, in a new place.
  const destination = data.setupRequired ? '/setup' : '/login';

  // `state.from` lets the login surface return the operator to where they were
  // aiming, so signing in does not silently dump them on the dashboard.
  // `replace` keeps the guarded URL out of history, so Back from /login does not
  // return to a page that will immediately redirect here again.
  return <Navigate to={destination} replace state={{ from: location.pathname }} />;
}
