import { type Ref } from 'react';
import { Link } from 'react-router-dom';
import { FontAwesomeIcon } from '@fortawesome/react-fontawesome';
import { faBars, faXmark } from '@fortawesome/free-solid-svg-icons';
import { useAdminKeyStore } from '../../state/adminKeyStore';
import { useLogoutMutation, useSessionQuery } from '../../state/sessionQueries';
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
 * The top bar carries the signed-in identity and sign-out control, and below the
 * 768px shell breakpoint (#48) a drawer toggle, and nothing else.
 *
 * #44 REPLACED THE ADMIN-KEY BOX THAT USED TO LIVE HERE. Humans now authenticate
 * with a session (login page, HttpOnly cookie), so there is no longer a secret
 * for a person to paste into the chrome of every page. The admin KEY is not
 * gone and must not be removed: `adminKeyStore` and `apiFetch`'s header
 * attachment remain, because Sonarr, Radarr and scripted callers cannot complete
 * an interactive login, and because the #43 bootstrap path still needs them.
 * What changed is that a HUMAN no longer needs to hold one.
 *
 * It deliberately does NOT render the page title (AC2b). The title belongs to
 * PageHeader inside the content pane, so exactly one <h1> exists per view --
 * a second title element here would give every page two headings, and screen
 * readers would announce the shell before the content. The hamburger is a
 * second responsibility added on top of that constraint, not a relaxation of
 * it: it opens navigation, it does not name the page.
 */
export function TopBar({ className, drawerOpen, onToggleDrawer, toggleRef }: TopBarProps) {
  const serverKeyUnset = useAdminKeyStore((s) => s.serverKeyUnset);
  const { data: session } = useSessionQuery();
  const logout = useLogoutMutation();

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
    //
    // #44 EXTENDS THE SAME CONCERN TO REDIRECTS, which is the shape it now takes.
    // The original worry was an affordance that cannot succeed; a login redirect
    // loop is that worry with a URL bar -- an operator bounced between /login and
    // a guarded page, with no state they can reach and nothing to click. The
    // defence is structural rather than a check written here: /login and /setup
    // sit OUTSIDE RequireSession in routes.tsx, the guard redirects only on a
    // definite "not authenticated" (never while loading, never on error), and it
    // chooses between /login and /setup from a single response so it cannot
    // oscillate. This branch is the same rule once more: it renders no sign-in
    // affordance of its own, because the guard already owns that redirect and a
    // second route to it from inside the guarded tree is how the loop returns.
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

  if (session?.authenticated === true) {
    return (
      <header className={barClassName}>
        {drawerToggle}
        <span className={styles.status}>
          <span className={`${styles.dot} ${styles.dotSet}`} aria-hidden="true" />
          <span className={styles.statusText}>Signed in as {session.username}</span>
        </span>
        <button
          type="button"
          className={`${styles.button} ${styles.buttonSecondary}`}
          onClick={() => logout.mutate()}
          disabled={logout.isPending}
        >
          Sign out
        </button>
      </header>
    );
  }

  // Signed out, and the server HAS a key configured.
  //
  // #44: THIS BRANCH RENDERS NOTHING ACTIONABLE, AND THAT IS THE POINT.
  // It is reached only in the window before the session query resolves, or when
  // it failed -- RequireSession sends a definitively-unauthenticated visitor to
  // /login, so the shell is not normally mounted in this state at all. There is
  // deliberately no "sign in" link here: the guard owns that redirect, and a
  // second path to /login rendered from inside the guarded tree is how a
  // redirect loop gets built by accident.
  return (
    <header className={barClassName}>
      {drawerToggle}
      <span className={styles.status}>
        <span className={`${styles.dot} ${styles.dotUnset}`} aria-hidden="true" />
        <span className={styles.statusText}>Not signed in</span>
      </span>
    </header>
  );
}
