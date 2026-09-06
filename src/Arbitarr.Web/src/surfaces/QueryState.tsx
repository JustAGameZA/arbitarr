import type { ReactNode } from 'react';

import { AdminKeyNotConfiguredError, AdminKeyRejectedError, ApiError } from '../api/client';
import { serverReason } from '../api/types';
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
 * Renders the loading / error / loaded triad for one query.
 *
 * Every admin surface needs the same three branches and the same AC6-503
 * treatment, and five hand-rolled copies is five chances for one of them to
 * render a raw stack trace or, worse, a key prompt on a 503 -- which is the
 * exact state the review environment is always in. Centralising it means the
 * 503 affordance is proven once per surface by a test that exercises the real
 * component, not a per-surface reimplementation.
 */
export function QueryState<T>({ isPending, error, data, children }: QueryStateProps<T>) {
  if (error !== null && error !== undefined) {
    return (
      <p className={styles.error} role="alert">
        {errorMessage(error)}
      </p>
    );
  }

  if (isPending || data === undefined) {
    return <p className={styles.muted}>Loading…</p>;
  }

  return <>{children(data)}</>;
}
