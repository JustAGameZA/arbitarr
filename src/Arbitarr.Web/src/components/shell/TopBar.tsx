import { useState, type FormEvent, type Ref } from 'react';
import { Link } from 'react-router-dom';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faBars, faXmark } from '@fortawesome/free-solid-svg-icons';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import styles from './TopBar.module.css';

interface TopBarProps {
  /** Grid placement class supplied by AppShell; the bar owns its own internals. */
  className?: string;
  /**
   * Whether the off-canvas drawer (#48) is currently open. Undefined means
   * "no toggle" -- AppShell only passes it, and therefore only mounts the
   * hamburger, below the 768px shell breakpoint.
   */
  drawerOpen?: boolean;
  /** Opens/closes the drawer. Required alongside `drawerOpen`. */
  onToggleDrawer?: () => void;
  /**
   * Lets AppShell return focus here when the drawer closes -- the toggle is
   * the element focus logically returns to, since it is what the operator
   * activated to open the drawer in the first place.
   */
  toggleRef?: Ref<HTMLButtonElement>;
}

/**
 * The top bar carries the admin-key affordance, and below the 768px shell
 * breakpoint (#48) a drawer toggle, and nothing else.
 *
 * It deliberately does NOT render the page title (AC2b). The title belongs to
 * PageHeader inside the content pane, so exactly one <h1> exists per view --
 * a second title element here would give every page two headings, and screen
 * readers would announce the shell before the content. The hamburger is a
 * second responsibility added on top of that constraint, not a relaxation of
 * it: it opens navigation, it does not name the page.
 */
export function TopBar({ className, drawerOpen, onToggleDrawer, toggleRef }: TopBarProps) {
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

  // AppShell always wires these props up -- the shell has no reliable way to
  // know the viewport width in JS (jsdom does not evaluate media queries, and
  // a resize listener would be one more thing to keep in sync). Instead
  // `.drawerToggle`'s own min-width override hides it above the 768px shell
  // breakpoint (AC3: no toggle above 768px). It is visible by default and
  // hidden above the breakpoint, not the reverse, precisely so that jsdom --
  // which applies a stylesheet's unconditional rules but never matches a
  // @media query -- renders it visible in every test.
  const drawerToggle =
    onToggleDrawer === undefined ? null : (
      <button
        ref={toggleRef}
        type="button"
        className={styles.drawerToggle}
        aria-label={drawerOpen === true ? 'Close navigation' : 'Open navigation'}
        aria-expanded={drawerOpen === true}
        onClick={onToggleDrawer}
      >
        <FontAwesomeIcon icon={drawerOpen === true ? faXmark : faBars} fixedWidth />
      </button>
    );

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
        {drawerToggle}
        <span className={styles.status}>
          <span className={`${styles.dot} ${styles.dotServerUnset}`} aria-hidden="true" />
          {/*
           * Truncates rather than wraps at narrow widths (#48 AC4). The Link
           * below is the operator's only route out of a fresh install with no
           * admin key, so it sits outside this span and keeps its own space on
           * the line instead of being squeezed by it.
           */}
          <span className={styles.statusText}>No admin API key is configured on the server.</span>
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
        {drawerToggle}
        <span className={styles.status}>
          <span className={`${styles.dot} ${styles.dotSet}`} aria-hidden="true" />
          <span className={styles.statusText}>Admin key set</span>
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
      {drawerToggle}
      <span className={styles.status}>
        <span className={`${styles.dot} ${styles.dotUnset}`} aria-hidden="true" />
        <span className={styles.statusText}>No admin key</span>
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
