import type { ReactNode } from 'react';
import { useEffect, useRef } from 'react';

import { AdminKeyNotConfiguredError, AdminKeyRejectedError, ApiError } from '../api/client';
import { serverReason } from '../api/types';
import { useLiveStatusStore } from '../state/liveStatusStore';
import styles from './surface.module.css';

/**
 * The message shown when the SERVER has no admin key configured (AC6-503).
 *
 * Exported so the surface tests assert against the same string the component
 * renders, rather than a copy that can drift out of agreement with it.
 */
export const SERVER_KEY_UNSET_MESSAGE =
  'No admin API key is configured on the server. Set one in the server configuration; entering a key here cannot help until it is.';

/**
 * Turns any thrown error into the string a surface should display.
 *
 * AC9/AC10 require the server's own rejection verbatim, so an ApiError carrying
 * an `{ error }` body yields exactly that text and nothing is invented around it.
 */
export function errorMessage(error: unknown): string {
  if (error instanceof AdminKeyNotConfiguredError) {
    return SERVER_KEY_UNSET_MESSAGE;
  }
  if (error instanceof AdminKeyRejectedError) {
    return 'The admin API key was rejected. Enter it again in the top bar.';
  }
  if (error instanceof ApiError) {
    return serverReason(error.body, error.message);
  }
  if (error instanceof Error) {
    return error.message;
  }
  return 'Request failed.';
}

interface QueryStateProps<T> {
  isPending: boolean;
  error: unknown;
  data: T | undefined;
  children: (data: T) => ReactNode;
}

/**
 * Announces `message` through the shell's shared live region (arb-tku8) the
 * moment `active` becomes true, and never on unmount or while `active` stays
 * false.
 *
 * Deliberately keyed on the true->true edge staying silent: without the ref
 * guard, two unrelated surfaces that both happen to render "Loading…" while
 * mounted would each re-announce on every re-render their query causes
 * (density toggle, an unrelated refetch), turning a screen reader's polite
 * queue into noise. The effect fires again once `active` has been false in
 * between, which is what a fresh pending/success cycle looks like.
 *
 * `role="alert"` errors are NOT routed through this -- they already interrupt
 * on their own (25+ existing call sites), and an assertive alert queued
 * behind a polite announcement would only delay it.
 */
export function useAnnounceOnChange(active: boolean, message: string): void {
  const announce = useLiveStatusStore((state) => state.announce);
  const wasActive = useRef(false);

  useEffect(() => {
    if (active && !wasActive.current) {
      announce(message);
    }
    wasActive.current = active;
  }, [active, message, announce]);
}

/**
 * Renders the loading / error / loaded triad for one query.
 *
 * Every admin surface needs the same three branches and the same AC6-503
 * treatment, and five hand-rolled copies is five chances for one of them to
 * render a raw stack trace or, worse, a key prompt on a 503 -- which is the
 * exact state the review environment is always in. Centralising it means the
 * 503 affordance is proven once per surface by a test that exercises the real
 * component, not a per-surface reimplementation.
 *
 * The pending branch also announces "Loading…" through the shared live
 * region (arb-tku8) -- previously a screen reader operator heard every error
 * (role="alert") but nothing else, so a pending state that resolves into a
 * silent success looked identical to nothing having happened at all.
 */
export function QueryState<T>({ isPending, error, data, children }: QueryStateProps<T>) {
  const pending = isPending || data === undefined;
  useAnnounceOnChange(error === null || error === undefined ? pending : false, 'Loading…');

  if (error !== null && error !== undefined) {
    return (
      <p className={styles.error} role="alert">
        {errorMessage(error)}
      </p>
    );
  }

  if (pending) {
    return <p className={styles.muted}>Loading…</p>;
  }

  return <>{children(data)}</>;
}
