import { useState, type FormEvent } from 'react';
import { Navigate } from 'react-router-dom';

import { errorMessage } from '../QueryState';
import { useSessionQuery, useSetupMutation } from '../../state/sessionQueries';
import styles from '../surface.module.css';
import local from './Login.module.css';

/**
 * #44: first-run account creation.
 *
 * Reachable only while the instance has NO accounts. That is enforced on the
 * SERVER -- `POST /api/auth/setup` refuses once one exists, atomically, and also
 * refuses a caller who is not on the local network. This component's redirect
 * below is a courtesy so an operator who bookmarks /setup is not shown a form
 * that would be rejected; it is not the control, and treating it as one would be
 * the mistake, since anything can post to the endpoint directly.
 *
 * Shares Login's stylesheet: they are the same card on the same signed-out page,
 * and two files would drift.
 */
export default function Setup() {
  const { data: session } = useSessionQuery();
  const setup = useSetupMutation();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');

  // The instance is already claimed -- there is nothing to set up, so send them
  // to the form that can actually succeed. `replace` keeps /setup out of history
  // so Back does not return here and bounce again.
  if (session !== undefined && !session.setupRequired) {
    return <Navigate to={session.authenticated ? '/' : '/login'} replace />;
  }

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (username.trim() === '' || password === '') {
      return;
    }
    setup.mutate({ username: username.trim(), password });
    setPassword('');
  };

  return (
    <div className={local.page}>
      <main className={`${styles.panel} ${local.card}`}>
        <div className={styles.panelBody}>
          <h1 className={local.title}>Set up Arbitarr</h1>
          <p className={local.subtitle}>
            Create the operator account for this instance. This screen is only
            available until the first account exists.
          </p>

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
                // "new-password" so a password manager offers to GENERATE one
                // rather than autofilling an unrelated saved credential -- which
                // matters more than usual here, since there is no reset path if
                // the operator later cannot reproduce what they typed.
                autoComplete="new-password"
                value={password}
                onChange={(event) => setPassword(event.target.value)}
              />
            </label>

            {setup.isError && (
              // Verbatim from the server, which owns the rules this can break:
              // the length floor, and the "already claimed" and "not on the local
              // network" refusals. Restating them here would be a second copy to
              // keep true.
              <p className={styles.error} role="alert">
                {errorMessage(setup.error)}
              </p>
            )}

            <div className={local.actions}>
              <button
                type="submit"
                className={`${styles.button} ${local.submit}`}
                disabled={setup.isPending}
              >
                {setup.isPending ? 'Creating account…' : 'Create account'}
              </button>
            </div>
          </form>

          {/*
            Stated before they choose a password rather than after they forget
            it. There is no reset path by design (see UserEntry), so the moment
            this matters most is the moment the password is being chosen. The
            recovery runbook itself lives in the README (arb-hhb moved it off
            Login.tsx for the same reason); this paragraph only needs to point
            there, not restate the database steps.
          */}
          <p className={local.help}>
            Choose a passphrase you will not lose. There is no password reset:{' '}
            <a
              href="https://github.com/JustAGameZA/arbitarr#signing-in-and-what-to-do-when-you-cannot"
              target="_blank"
              rel="noopener noreferrer"
            >
              how to recover a locked-out instance
            </a>
            .
          </p>
        </div>
      </main>
    </div>
  );
}
