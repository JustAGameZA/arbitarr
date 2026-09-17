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
  /**
   * Replaces the TEXT of the error branch, never its treatment (arb-z505).
   *
   * Two surfaces answer a 404 with a sentence that names their own cause --
   * Search's "ad-hoc results are not recorded in the release lookup" and
   * Suppressions' "the audit log records the upstream guid only" -- and each
   * is correct only where it is written. Before this prop, those sentences
   * were the reason both surfaces hand-rolled the whole triad, which is
   * exactly the drift QueryState exists to prevent; routing them through here
   * keeps the pending/error/503 treatment proven once while the wording stays
   * local to the surface that knows the cause.
   *
   * Defaulted to `errorMessage`, so the 25+ existing call sites are untouched
   * and an omitted prop cannot quietly change what a surface says. The
   * `role="alert"` stays QueryState's own and is not the caller's to forget.
   */
  renderError?: (error: unknown) => ReactNode;
  /**
   * Groups this query's pending announcement with its siblings' (arb-xzvk).
   *
   * A surface that mounts several QueryStates for ONE navigation passes the
   * same key to each, and the group announces "Loading…" once instead of once
   * per query — Dashboard's Status, Recent searches and Effective configuration
   * made a screen-reader operator hear it three times. Passed straight through
   * to `useAnnounceOnChange`; omitted, every instance announces on its own
   * pending edge exactly as before, which is right for a surface with a single
   * query and is what the 25+ existing call sites get without changing.
   *
   * Scopes only the ANNOUNCEMENT. The visible "Loading…" paragraph stays
   * per-instance: each panel still shows its own query's state, because the
   * three resolve independently and a shared spinner would misreport two of
   * them.
   */
  announceScope?: string;
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
 *
 * `scope` (arb-xzvk) names a GROUP of announcers that speak for one event, so
 * the group announces once however many of its members fire. The ref guard
 * above is per-INSTANCE and cannot do this: Dashboard's three queries are three
 * separate hook instances, each correctly seeing its own false->true edge, and
 * each announcing "Loading…" for what the operator experiences as one
 * navigation. Coalescing therefore has to happen where the announcements meet,
 * in the store, not here.
 *
 * Optional, and omitting it is exactly the pre-arb-xzvk behaviour -- every
 * existing call site keeps compiling and keeps announcing unconditionally.
 * That default is deliberate, not incidental: the seven `.success` "Saved."
 * sites are genuinely independent events that must each be heard, and silently
 * grouping them would undo the `seq` nonce arb-tku8's codereview added.
 */
export function useAnnounceOnChange(active: boolean, message: string, scope?: string): void {
  const announce = useLiveStatusStore((state) => state.announce);
  const wasActive = useRef(false);

  useEffect(() => {
    if (active && !wasActive.current) {
      announce(message, scope);
    }
    wasActive.current = active;
  }, [active, message, scope, announce]);
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
export function QueryState<T>({
  isPending,
  error,
  data,
  children,
  renderError = errorMessage,
  announceScope,
}: QueryStateProps<T>) {
  const pending = isPending || data === undefined;
  useAnnounceOnChange(
    error === null || error === undefined ? pending : false,
    'Loading…',
    announceScope,
  );

  if (error !== null && error !== undefined) {
    return (
      <p className={styles.error} role="alert">
        {renderError(error)}
      </p>
    );
  }

  if (pending) {
    return <p className={styles.muted}>Loading…</p>;
  }

  return <>{children(data)}</>;
}
