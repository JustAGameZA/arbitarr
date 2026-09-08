import { useState } from 'react';

import { errorMessage } from '../../QueryState';
import styles from '../../surface.module.css';
import local from './Account.module.css';
import { useChangePasswordMutation } from './queries';

/**
 * Account (#96) -- the signed-in operator's own credential, above the machines'.
 *
 * Three properties this section exists to hold, each of which a tidy-up would
 * break silently:
 *
 * 1. NEITHER PASSWORD OUTLIVES THE REQUEST THAT CARRIES IT. The fields are
 *    cleared on both paths, and the mutation is reset after each call so the
 *    MutationCache -- which retains `variables` by default -- keeps nothing. See
 *    `dropChangeFromCache` for why the reset lives HERE and not in `queries.ts`.
 * 2. THE CONFIRM FIELD IS COMPARED CLIENT-SIDE ONLY AND IS NEVER SENT. It is not
 *    a second opinion on a server rule -- there is no server rule about it -- it
 *    is a typo guard for a value the operator cannot see. There is deliberately
 *    NO client-side length pre-check: the server rejects and never clamps, and a
 *    guess here would either block a value the server accepts or invent a
 *    rejection it never issued, exactly as the settings editor and `ApiKeys`
 *    both avoid doing.
 * 3. THE REJECTION IS THE SERVER'S EXACT WORDS, via `errorMessage`, held in
 *    component state rather than read off `change.error` -- which is undefined
 *    after the reset. The same reason `Login.tsx` and `ApiKeys.tsx` both do it.
 *
 * The operationally surprising part is stated in the panel itself rather than
 * left to the README alone: every OTHER signed-in browser is signed out by the
 * change, and the one in use stays signed in.
 */
export function AccountSection() {
  const change = useChangePasswordMutation();

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');

  // Both outcomes are held HERE rather than read back off the mutation, because
  // the mutation is reset the moment it settles (see `dropChangeFromCache`):
  // after that `change.error` is undefined, so a render that consulted it would
  // show the operator nothing at all where a refusal belongs.
  const [saved, setSaved] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  /**
   * Drop the settled change from the MutationCache.
   *
   * `reset()` releases the mutation, and the `gcTime: 0` in `queries.ts` is what
   * makes that collection immediate rather than five minutes late -- together
   * they are what stops `state.variables`, both passwords included, sitting in
   * the cache where the devtools or the console can still read it.
   *
   * WHERE this is called from is load-bearing. It must run from the per-call
   * callbacks passed to `mutate(vars, { ... })` below -- never from a hook-level
   * `onSuccess`/`onSettled` in `queries.ts`. `Mutation.execute` awaits the
   * hook-level callbacks BEFORE it dispatches the settle action, and it is that
   * dispatch which notifies the observer and runs these per-call ones. Since
   * `MutationObserver.reset()` clears its current mutation and removes the
   * observer, and the notify path is gated on `hasListeners()`, a reset from up
   * there does not merely race the capture below: it deletes the callback's only
   * delivery route, so the server's rejection is swallowed with no message shown.
   * `Account.test.tsx` drives exactly that variant as a control. Called from
   * here the capture has already happened, so a plain synchronous reset is
   * correct and no deferral is needed.
   */
  const dropChangeFromCache = () => {
    change.reset();
  };

  /**
   * Clears every field.
   *
   * Called on BOTH paths, not only on success: a rejected attempt leaves the
   * typed passwords in the DOM otherwise, which is the copy that outlives the
   * request for as long as the operator leaves the tab open. They retype on a
   * retry, which costs a few seconds and is the right trade for a credential.
   */
  const clearFields = () => {
    setCurrentPassword('');
    setNewPassword('');
    setConfirmPassword('');
  };

  const submit = (event: React.FormEvent) => {
    event.preventDefault();

    setSaved(false);
    setFailure(null);

    if (newPassword !== confirmPassword) {
      // The one thing decided here rather than by the server, because the server
      // never sees the confirmation field and could not decide it.
      setFailure('The new password and its confirmation do not match.');
      return;
    }

    change.reset();
    change.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          setSaved(true);
          clearFields();
          dropChangeFromCache();
        },
        onError: (error) => {
          setFailure(errorMessage(error));
          clearFields();
          dropChangeFromCache();
        },
      },
    );
  };

  return (
    <section className={styles.panel}>
      <h2 className={styles.panelHeading}>Account</h2>
      <div className={styles.panelBody}>
        <p className={local.explanation}>
          Changing your password signs out every other browser that is signed in to this instance.
          The one you are using now stays signed in. There is no password reset, so choose a
          passphrase you will remember.
        </p>

        <form className={styles.form} onSubmit={submit}>
          <label className={styles.field}>
            Current password
            <input
              className={styles.input}
              type="password"
              aria-label="Current password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
            />
          </label>

          <label className={styles.field}>
            New password
            <input
              className={styles.input}
              type="password"
              aria-label="New password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
            />
          </label>

          <label className={styles.field}>
            Confirm new password
            <input
              className={styles.input}
              type="password"
              aria-label="Confirm new password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
            />
          </label>

          <button type="submit" className={styles.button} disabled={change.isPending}>
            Change password
          </button>
        </form>

        {failure !== null && (
          <p className={styles.error} role="alert">
            {failure}
          </p>
        )}
        {saved && failure === null && <p className={styles.success}>Saved.</p>}
      </div>
    </section>
  );
}
