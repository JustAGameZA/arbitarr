import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import styles from './TopBar.module.css';

interface TopBarProps {
  /** Grid placement class supplied by AppShell; the bar owns its own internals. */
  className?: string;
}

/**
 * The top bar carries the admin-key affordance and nothing else.
 *
 * It deliberately does NOT render the page title (AC2b). The title belongs to
 * PageHeader inside the content pane, so exactly one <h1> exists per view --
 * a second title element here would give every page two headings, and screen
 * readers would announce the shell before the content.
 */
export function TopBar({ className }: TopBarProps) {
  const key = useAdminKeyStore((s) => s.key);
  const serverKeyUnset = useAdminKeyStore((s) => s.serverKeyUnset);
  const setKey = useAdminKeyStore((s) => s.setKey);
  const clearKey = useAdminKeyStore((s) => s.clearKey);
  const [draft, setDraft] = useState('');

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = draft.trim();
    if (trimmed === '') {
      return;
    }
    setKey(trimmed);
    // Drop the plaintext out of component state as soon as the store has it.
    setDraft('');
  };

  const barClassName = className === undefined ? styles.topbar : `${styles.topbar} ${className}`;

  if (serverKeyUnset) {
    // A key prompt here would STILL strand the operator: the server has no key
    // set, so no value they type can satisfy the gate. That reasoning is
    // unchanged, and is why this branch still renders no input field.
    //
    // What changed (#43) is that this is no longer a DEAD end. The server now
    // admits admin requests arriving from the local network while no key is
    // configured, so an operator reading this can actually reach Settings and
    // set one. Hence a link rather than a bare statement of fact -- the old
    // copy described a problem the operator had no way to act on.
    return (
      <header className={barClassName}>
        <span className={styles.status}>
          <span className={`${styles.dot} ${styles.dotServerUnset}`} aria-hidden="true" />
          No admin API key is configured on the server.
        </span>
        <Link className={styles.link} to="/settings">
          Set one in Settings
        </Link>
      </header>
    );
  }

  if (key !== null) {
    return (
      <header className={barClassName}>
        <span className={styles.status}>
          <span className={`${styles.dot} ${styles.dotSet}`} aria-hidden="true" />
          Admin key set
        </span>
        <button
          type="button"
          className={`${styles.button} ${styles.buttonSecondary}`}
          onClick={clearKey}
        >
          Clear admin key
        </button>
      </header>
    );
  }

  return (
    <header className={barClassName}>
      <span className={styles.status}>
        <span className={`${styles.dot} ${styles.dotUnset}`} aria-hidden="true" />
        No admin key
      </span>
      <form className={styles.form} onSubmit={submit}>
        <input
          className={styles.input}
          // type="password" so the key is not shoulder-surfable, and
          // autoComplete="off" so the browser never offers to save it.
          type="password"
          autoComplete="off"
          aria-label="Admin API key"
          placeholder="Admin API key"
          value={draft}
          onChange={(event) => setDraft(event.target.value)}
        />
        <button type="submit" className={styles.button}>
          Set admin key
        </button>
      </form>
    </header>
  );
}
