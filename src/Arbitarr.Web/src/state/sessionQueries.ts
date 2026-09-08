import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { apiFetch } from '../api/client';

/**
 * The shape of GET /api/auth/session (Arbitarr.Api.Security.SessionResponse).
 *
 * Note what is absent: no token, and deliberately no field that could carry one.
 * The session cookie is `HttpOnly`, so script cannot read it and nothing in this
 * app ever holds it -- which is what makes the "session token never reaches
 * localStorage/sessionStorage" property structural rather than a rule someone
 * has to remember. The admin key is different (it must be readable to be put in
 * a header), which is why adminKeyStore exists and this does not mirror it.
 */
export interface SessionState {
  authenticated: boolean;
  username: string | null;
  /** True when the instance has no accounts at all -- route to /setup, not /login. */
  setupRequired: boolean;
}

export const SESSION_QUERY_KEY = ['auth', 'session'] as const;

/**
 * Who the current viewer is.
 *
 * `retry: false` because a 401 here is an ANSWER, not a failure to retry: the
 * endpoint returns 200 with `authenticated: false` for an anonymous caller, so
 * anything that actually throws is a transport or server problem, and retrying
 * it just delays the guard's decision.
 */
export function useSessionQuery() {
  return useQuery<SessionState>({
    queryKey: SESSION_QUERY_KEY,
    queryFn: () => apiFetch<SessionState>('/api/auth/session'),
    retry: false,
    // Refetched on focus so a session that expired while the tab sat in the
    // background is noticed when the operator comes back, rather than on their
    // next mutation as a confusing failure.
    refetchOnWindowFocus: true,
  });
}

interface Credentials {
  username: string;
  password: string;
}

/**
 * Signs in. The server sets the session cookie on the response; nothing here
 * touches the token, because nothing here can see it.
 *
 * THE PASSWORD MUST NOT SURVIVE THIS CALL, AND TWO THINGS ARE NEEDED FOR THAT.
 * react-query retains each mutation's `variables` -- here `{ username, password
 * }` -- on the cached mutation, so a plain `useMutation` leaves the plaintext
 * password reachable from the QueryClient for the lifetime of the page. Clearing
 * the component's own state (which `Login` also does) does not help: that is a
 * different copy. Both halves below are load-bearing and are asserted by
 * "the password never outlives the request that carries it" in Login.test.tsx,
 * on the success path AND the failure path.
 */
export function useLoginMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    // `gcTime: 0` is load-bearing, not a tuning knob, and it is exactly the idiom
    // #93 established for the minted API key in Settings/ApiKeys/queries.ts --
    // one rule for this codebase's credential-carrying mutations rather than two.
    // react-query keeps a settled mutation's `variables` -- here the plaintext
    // password -- on the MutationCache for the default five minutes, reachable
    // from the devtools or the console. Zero drops it the moment its last
    // observer leaves, which is what makes `Login`'s per-call reset erase the
    // value rather than merely detach it. Neither half is sufficient alone.
    //
    // Do NOT reset from a hook-level `onSuccess`/`onSettled` here, however obvious
    // a home it looks: those are awaited BEFORE the settle action is dispatched,
    // and that dispatch is what runs the per-call callbacks in `Login.tsx` which
    // capture the rejection. Resetting from here removes the observer they arrive
    // on, so a failed sign-in is swallowed with no message shown. The reset
    // belongs after the capture -- see `dropLoginFromCache` in Login.tsx.
    gcTime: 0,
    mutationFn: (credentials: Credentials) =>
      apiFetch<SessionState>('/api/auth/login', {
        method: 'POST',
        body: JSON.stringify(credentials),
      }),
    onSuccess: (session) => {
      // Seeded rather than invalidated, so the guard re-renders with the new
      // identity immediately instead of flashing the login page again while a
      // refetch is in flight -- a one-frame bounce back to /login is exactly the
      // "loop" the route guard exists to avoid.
      queryClient.setQueryData(SESSION_QUERY_KEY, session);
    },
  });
}

/** Creates the first account on a fresh install, and signs in as it. */
export function useSetupMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: (credentials: Credentials) =>
      apiFetch<SessionState>('/api/auth/setup', {
        method: 'POST',
        body: JSON.stringify(credentials),
      }),
    onSuccess: (session) => {
      queryClient.setQueryData(SESSION_QUERY_KEY, session);
    },
  });
}

/**
 * Signs out. The server revokes the session row -- the half that matters, since
 * clearing a cookie alone would leave a copied token working.
 */
export function useLogoutMutation() {
  const queryClient = useQueryClient();

  return useMutation({
    mutationFn: () => apiFetch<void>('/api/auth/logout', { method: 'POST' }),
    onSuccess: () => {
      queryClient.setQueryData<SessionState>(SESSION_QUERY_KEY, (previous) => ({
        authenticated: false,
        username: null,
        // Preserved from the previous answer: signing out does not create or
        // remove accounts, so claiming setup is required would send the operator
        // to a first-run screen that would refuse them.
        setupRequired: previous?.setupRequired ?? false,
      }));
      // Everything else in the cache was fetched as the signed-in operator.
      queryClient.removeQueries({ predicate: (query) => query.queryKey !== SESSION_QUERY_KEY });
    },
  });
}
