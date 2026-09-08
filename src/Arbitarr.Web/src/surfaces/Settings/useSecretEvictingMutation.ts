/**
 * The one rule for a mutation whose request VARIABLES can carry a secret
 * (a plaintext API key, a webhook URL): `gcTime: 0` on the mutation
 * (`useMutation({ gcTime: 0, ... })` in the matching `queries.ts`) plus a
 * settle-time eviction from THIS hook. Neither half is sufficient alone.
 *
 * <h3>Why this exists (arb-689)</h3>
 * `ApiKeys.tsx`, `Sources.tsx` and `Notifications/queries.ts` each grew their
 * own copy of the same reasoning, each restated at length because getting it
 * wrong is silent: react-query keeps a settled mutation's `state.variables` —
 * and, for a create, its `state.data` — on the `MutationCache` for `gcTime`
 * (five minutes by default), reachable from the devtools or the console.
 * Clearing component state or the input field is not enough; the cache is a
 * SECOND copy of the secret the client is otherwise careful never to create.
 *
 * <h3>The eviction MUST happen from a per-call callback, never a hook-level one</h3>
 * `Mutation.execute` awaits the hook-level `onSuccess`/`onError`/`onSettled`
 * passed to `useMutation` in `queries.ts` BEFORE it dispatches the settle
 * action — and that dispatch is the only route to the PER-CALL callbacks
 * passed to `mutate(vars, { ... })`, which is where every surface in this
 * codebase reads the result to render (the created key, the server's
 * rejection). `MutationObserver.reset()` detaches the observer those arrive
 * on. Calling it from a hook-level callback in `queries.ts` therefore removes
 * the delivery route BEFORE the per-call callback that needed it runs, and a
 * server rejection is swallowed with nothing shown — silently defeating the
 * reject-never-clamp rule every mutation in these sections otherwise follows.
 * `Notifications/queries.ts`'s `useUpdateNotificationConfigMutation` works
 * around this today with a `setTimeout(..., 0)` deferral; that workaround is
 * unnecessary once eviction happens from `settle()` below, which is already
 * called from the per-call site.
 *
 * <h3>The "fourth divergence" this hook settles</h3>
 * `useRevokeApiKeyMutation` set `gcTime: 0` "for consistency" even though its
 * variables are a bare id — nothing sensitive ever sits in that entry.
 * `useDeleteSourceMutation` got no such treatment despite being the same
 * shape. Neither the id-only revoke nor the id-only source delete/remove
 * needs `gcTime: 0` or a call through this hook: there is no secret in their
 * variables or data for the cache to retain, and calling `settle()` on them
 * anyway would only be a second `errorMessage` capture with nothing to evict.
 * The rule, applied uniformly from here on: **only a mutation whose request
 * body can itself carry a secret gets `gcTime: 0` plus `settle()`.** An
 * id-only mutation (revoke, remove, delete, clear) reads its own
 * `.error`/`.isError` directly, exactly as `Notifications.tsx`'s `clear`
 * (`useClearWebhookMutation`) already does.
 *
 * <h3>Why no reset is needed once the panel that rendered the secret is gone</h3>
 * `ApiKeys.tsx`'s `onDismissReveal` used to carry a comment saying a second
 * `reset()` there "would be unfalsifiable" — true only because `settle()`
 * (called from the per-call `onSuccess`/`onError` at create time) has ALREADY
 * evicted the entry by the time the reveal panel closes. That reasoning lives
 * here now, once, instead of being re-derived per file: once `settle()` has
 * run for a given call, the MutationCache holds nothing for it to still
 * remove, so a caller closing a reveal panel or an editor never needs to
 * reset the mutation a second time.
 */
export interface SecretEvictingMutation {
  reset: () => void;
}

/**
 * Returns `settle`, a per-call `onSettled`/`onSuccess`+`onError` body for a
 * secret-bearing mutation: capture the server's rejection (or clear it on
 * success) into the caller's own state, THEN evict the settled entry from the
 * MutationCache. Call it from the options object passed to `mutate(vars, {
 * ... })` — never from `useMutation`'s own hook-level options in `queries.ts`.
 * See the module doc for why the ordering and the call site are both
 * load-bearing.
 *
 * `onCapture` receives the RAW error (or `null` on success) — never a
 * pre-formatted string. Every render site in these sections calls
 * `errorMessage()` itself at the point it renders a failure (`CreateKeyForm`,
 * `Sources.tsx`'s write-error banner, and so on), so pre-formatting here
 * would double-format on a caller that stores the raw value, or dead-end on
 * one that stores the string: `errorMessage('already exists')` fails every
 * `instanceof` check and falls through to its generic `'Request failed.'`,
 * silently discarding the server's actual words. Passing the raw error
 * through keeps this hook interchangeable with the `setCreateFailure` /
 * `setWriteError` / `setRejection` state every caller already had, typed as
 * `unknown` exactly as before.
 */
export function useSecretEvictingMutation() {
  const settle = (
    mutation: SecretEvictingMutation,
    error: unknown,
    onCapture: (error: unknown) => void,
  ) => {
    onCapture(error ?? null);
    mutation.reset();
  };

  return { settle };
}
