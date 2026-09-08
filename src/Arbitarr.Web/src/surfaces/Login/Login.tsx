import { useState, type FormEvent } from 'react';
import { Navigate, useLocation } from 'react-router-dom';

import { errorMessage } from '../QueryState';
import { useLoginMutation, useSessionQuery } from '../../state/sessionQueries';
import styles from '../surface.module.css';
import local from './Login.module.css';

interface RedirectState {
  from?: string;
}

/**
 * #44: the sign-in surface.
 *
 * Rendered OUTSIDE the AppShell and outside `RequireSession` -- both deliberate.
 * Outside the shell because a signed-out visitor has no navigation to offer; and
 * outside the guard because a guard wrapping its own redirect target is the
 * redirect loop the plan promotes to a test. See `RequireSession` for the three
 * properties that together make the loop impossible.
 */
export default function Login() {
  const { data: session } = useSessionQuery();
  const login = useLoginMutation();
  const location = useLocation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  // THE REJECTION IS HELD HERE, NOT READ FROM `login.error`.
  //
  // The mutation is dropped from the cache as soon as it settles (see
  // `dropLoginFromCache`), so that the plaintext password does not sit in
  // react-query's retained `variables`. After that `login.error` is undefined, so
  // a render consulting it would show the server's refusal for a moment and then
  // silently lose it -- worse than showing none. Capturing the message here at
  // the moment of failure decouples what the operator sees from the cache entry
  // that must not persist. Both properties are asserted together in
  // Login.test.tsx. This mirrors `createFailure` in Settings/ApiKeys/ApiKeys.tsx,
  // which #93 introduced for the same reason.
  const [failure, setFailure] = useState<string | null>(null);

  /**
   * Drop the settled login from the MutationCache.
   *
   * `reset()` releases the mutation and the `gcTime: 0` in `sessionQueries.ts`
   * makes that collection immediate rather than five minutes late -- together
   * they are what stops the plaintext password sitting in `variables` where the
   * devtools or the console could still read it.
   *
   * WHERE this is called from is load-bearing, and it is the same rule #93
   * recorded for the minted API key: it must run from the per-call callbacks
   * passed to `mutate(vars, { ... })` -- never from a hook-level `onSettled` in
   * `sessionQueries.ts`. Those are awaited BEFORE the settle action is
   * dispatched, and it is that dispatch which runs these per-call ones; a reset
   * from up there deletes the callback's only delivery route, so a failed
   * sign-in is swallowed with no message shown. Called from here the capture has
   * already happened, so a plain synchronous reset is correct.
   */
  const dropLoginFromCache = () => {
    login.reset();
  };

  // Already signed in -- an operator who navigates to /login with a live session
  // should land in the app, not stare at a form asking for credentials they have
  // already supplied. `replace` keeps /login out of history so Back does not
  // return here and bounce straight out again.
  if (session?.authenticated === true) {
    const from = (location.state as RedirectState | null)?.from;
    return <Navigate to={from ?? '/'} replace />;
  }

  // A fresh install has no account to sign into. Sending them on to /setup is
  // the same anti-stranding rule the guard follows, applied from the other side:
  // this form could never succeed here, so it must not be what they are shown.
  if (session?.setupRequired === true) {
    return <Navigate to="/setup" replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (username.trim() === '' || password === '') {
      return;
    }
    setFailure(null);
    login.mutate(
      { username: username.trim(), password },
      {
        onSuccess: dropLoginFromCache,
        onError: (error) => {
          setFailure(errorMessage(error));
          dropLoginFromCache();
        },
      },
    );
    // The password is dropped from component state as soon as the mutation has
    // it, so it does not sit in a React tree for the lifetime of the page.
    setPassword('');
  };

  return (
    <div className={local.page}>
      <main className={`${styles.panel} ${local.card}`}>
        <div className={styles.panelBody}>
          <h1 className={local.title}>Sign in to Arbitarr</h1>
          <p className={local.subtitle}>Enter your operator account details.</p>

          <form className={styles.form} onSubmit={submit}>
            <label className={styles.field}>
              Username
              <input
                className={styles.input}
                type="text"
                autoComplete="username"
                value={username}
                onChange={(event) => setUsername(event.target.value)}
              />
            </label>

            <label className={styles.field}>
              Password
              <input
                className={styles.input}
                type="password"
                autoComplete="current-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </label>

            {failure !== null && (
              // The server's own message, verbatim: it says only "the username or
              // password is incorrect" and never which half was wrong, because
              // distinguishing them turns the route into a username oracle. It
              // also carries the rate-limit message when that applies, so
              // inventing copy here would lose the more useful of the two.
              <p className={styles.error} role="alert">
                {failure}
              </p>
            )}

            <div className={local.actions}>
              <button
                type="submit"
                className={`${styles.button} ${local.submit}`}
                disabled={login.isPending}
              >
                {login.isPending ? 'Signing in…' : 'Sign in'}
              </button>
            </div>
          </form>

          {/*
            THE RECOVERY STORY, STATED WHERE IT IS NEEDED. There is no password
            reset in Arbitarr, by design (see UserEntry) -- a homelab appliance
            has no mail transport to send one, and a LAN-reachable reset would be
            a second authentication path weaker than the first. An operator meets
            that fact here, at the moment it becomes relevant, rather than
            discovering it only in the README after locking themselves out.
          */}
          <p className={local.help}>
            There is no password reset. If you are locked out, restore the
            configuration database from a backup, or remove the rows from the
            users table in <code>arbitarr.db</code> via the config bind mount and
            set the account up again.
          </p>
        </div>
      </main>
    </div>
  );
}
