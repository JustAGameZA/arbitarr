import { create } from 'zustand';

/**
 * The single polite live-region announcement, held in memory for the lifetime
 * of the tab and nowhere else (arb-tku8).
 *
 * Every admin surface currently reports its own pending and success states
 * visually -- QueryState's `Loading…` paragraph, and the per-surface `Saved.`
 * text next to the control it describes -- but neither is wired to anything a
 * screen reader announces on its own; only `role="alert"` errors are (25+
 * call sites). This store is the one thing every surface writes into instead
 * of each growing its own `role="status"` region: Sources.tsx already has one
 * (arb-tku8's brief), and multiplying that pattern per surface is exactly
 * what a shared shell-level region avoids.
 *
 * No `persist` middleware, matching `adminKeyStore` and `tableDensityStore`:
 * an announcement is a transient event for whoever is in the tab right now,
 * not state to restore on reload, and the CI guard that greps for
 * localStorage/sessionStorage would reject it anyway.
 */
interface LiveStatusState {
  message: string;
  /**
   * Monotonically increasing on every `announce`, including a repeat of the
   * same text (Account and Notifications' "Saved." are two of seven `.success`
   * call sites that can each fire it). `message` alone does not change on a
   * repeat, and AppShell selects the primitive `state.message`, so a second
   * identical announcement produced no re-render and a screen reader heard
   * only the first "Saved." -- `seq` gives the consumer a value that always
   * changes, keyed into the DOM so React is forced to touch the text node.
   */
  seq: number;
  /**
   * The message each named scope last announced, so a second announcer in the
   * same scope carrying the same message is coalesced away (arb-xzvk).
   *
   * Dashboard mounts three queries, and each one's QueryState announces
   * "Loading…" on its own pending edge, so one navigation put the identical
   * announcement into the polite queue three times. That is NOT the situation
   * `seq` exists for: `seq` covers two announcements that are genuinely two
   * EVENTS (Account saved, then Notifications saved), which must both be
   * heard. Dashboard's three are one event reported thrice. The difference is
   * not derivable from the message — only the caller knows it — which is why
   * coalescing is opt-in via a scope key rather than a blanket "drop repeats"
   * rule that would silently undo `seq` for every existing call site.
   *
   * Keyed by scope rather than one last-message field: two different scopes
   * announcing the same text are two events and must not silence each other.
   * A scope's entry is overwritten once that scope announces something
   * different, so a later re-entry into the earlier message (a refetch putting
   * the group back into pending after it resolved) announces again — an
   * announcement records an event, and a group that becomes pending a second
   * time has had a second event.
   */
  scopeMessages: Record<string, string>;
  /**
   * Announces `message`. With a `scope`, at most one announcement is produced
   * per consecutive run of that message within that scope; without one, every
   * call announces (the `seq` nonce guarantees it), which is the behaviour all
   * pre-arb-xzvk call sites were written against and which they keep.
   */
  announce: (message: string, scope?: string) => void;
}

export const useLiveStatusStore = create<LiveStatusState>((set) => ({
  message: '',
  seq: 0,
  scopeMessages: {},
  announce: (message, scope) =>
    set((state) => {
      if (scope === undefined) {
        return { message, seq: state.seq + 1 };
      }
      if (state.scopeMessages[scope] === message) {
        // Already announced by a sibling in this scope and not yet superseded.
        // Returning `state` itself, not a fresh object: zustand notifies on
        // every `set` regardless of value equality, and AppShell keys the
        // region on `seq`, so any new object here would mutate the DOM for an
        // announcement that was deliberately suppressed.
        return state;
      }
      return {
        message,
        seq: state.seq + 1,
        scopeMessages: { ...state.scopeMessages, [scope]: message },
      };
    }),
}));
