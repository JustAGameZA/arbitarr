import type { ReactNode } from 'react';
import { useRef } from 'react';
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
 * 2. It redirects only on a DEFINITE "no". The states, enumerated:
 *      - first load (loading, no data yet): renders nothing (below).
 *      - loaded (data present): checked against `data.authenticated`.
 *      - errored: children render via fail-open, below.
 *      - post-error refetch: children render via the latch, below, until a
 *        real answer supersedes it.
 *      - paused/offline query: `data` stays undefined and `isLoading` stays
 *        true (react-query does not resolve a paused query to an error), so
 *        this falls into the "first load" / loading-render-nothing case, not
 *        the errored-or-latched one.
 *
 *    If the session query ERRORS, the children render. A guard that
 *    redirected on error would strand the operator at a login page during
 *    any backend hiccup, and -- worse -- would do it on a login page whose
 *    own submit needs the same backend. Failing open on error is safe
 *    because it is not the security boundary: every gated route is enforced
 *    server-side by `AdminApiKeyFilter`, so the worst case of rendering the
 *    shell without a session is surfaces that show their own 401 states.
 *    This component is a convenience, and treating it as the gate would be
 *    the actual mistake.
 *
 *    WHILE loading, though, nothing renders (arb-7m7). Rendering `children`
 *    during that window used to mount the protected route -- and its queries,
 *    including admin-gated ones -- before the very first answer came back, so
 *    a signed-out deep link to /system fired GET /api/admin/observability and
 *    got a 401 in the console a frame before the redirect happened. Unlike an
 *    error, "loading" always resolves to a real answer, so there is no
 *    stranding-on-hiccup case to protect here.
 *
 * 3. The two destinations are mutually exclusive and chosen from ONE answer:
 *    `setupRequired` and `authenticated` come from a single response, so the
 *    guard cannot bounce between /login and /setup on inconsistent reads.
 */
export function RequireSession({ children }: RequireSessionProps) {
  const { data, isLoading, isError } = useSessionQuery();
  const location = useLocation();

  // Latches once the guard has failed open on an error, and clears again the
  // moment a real answer (`data`) arrives. WITHOUT the latch, mounting
  // `children` (e.g. AppShell's TopBar, which also calls useSessionQuery) adds
  // a second observer to the same errored query; react-query's default
  // refetch-on-mount then re-fetches it, which flips `isLoading` back to true
  // for the window of that refetch. That would hide `children` again, tearing
  // TopBar back down -- removing the observer that triggered the refetch --
  // whereupon the settled error reappears and mounts it again: an infinite
  // mount/unmount loop, not merely a flicker. Cleared on `data` so a session
  // that recovers from the hiccup still gets a real authenticated/redirect
  // decision rather than staying latched open forever.
  //
  // Evaluated (2026-09-12, arb-87co): `refetchOnMount: false` does NOT retire
  // this latch. react-query v5's `shouldFetchOnMount` only consults
  // `refetchOnMount` when `data !== undefined`, and the errored session query
  // here has no data, so that option is inert in exactly this case.
  // `retryOnMount: false` would suppress the mount fetch instead, but was
  // rejected: it applies to every observer of the session query, not just
  // this guard, and leaves an errored query with no self-healing remount path
  // short of a window focus.
  const failedOpenOnce = useRef(false);
  if (isError) {
    failedOpenOnce.current = true;
  } else if (data !== undefined) {
    failedOpenOnce.current = false;
  }

  // Property 2 (error half): no redirect without a definite answer. `data` is
  // undefined on error too, and that renders the children (fail open).
  if (isError || (failedOpenOnce.current && data === undefined)) {
    return <>{children}</>;
  }

  // Property 2 (loading half, arb-7m7): render nothing until the first answer
  // arrives, so the protected element -- and its queries -- never mount on
  // the strength of an as-yet-unknown session.
  if (isLoading || data === undefined) {
    return null;
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
