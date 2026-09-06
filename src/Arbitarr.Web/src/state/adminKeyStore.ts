import { create } from 'zustand';

/**
 * The admin API key, held in memory for the lifetime of the tab and nowhere else.
 *
 * There is deliberately NO `persist` middleware (Decision D2). The key is not
 * written to localStorage, not to sessionStorage, and never placed in a URL --
 * `AdminApiKeyFilter` documents that it takes the key from a header precisely so
 * it does not end up in access logs or browser history the way the
 * Torznab/Newznab client apikey does. Persisting it here would reintroduce that
 * exposure on the client side and survive tab close, where any script on the
 * origin could read it. A CI guard greps src/ for localStorage|sessionStorage.
 *
 * Adding `persist` to "improve UX" is a security regression, not a convenience.
 */
interface AdminKeyState {
  /** The operator's key for this tab. null = not entered yet, or cleared after a 401/403. */
  key: string | null;
  /**
   * True when the SERVER has no admin key configured at all (fresh install), as
   * signalled by a 503 from AdminApiKeyFilter.
   *
   * This is tracked separately from `key` because the two states call for
   * opposite affordances and conflating them strands the operator: on 503 the
   * right response is "configure a key on the server", while a key prompt would
   * be useless -- no key the user types can satisfy a gate that has none set.
   */
  serverKeyUnset: boolean;
  setKey: (key: string) => void;
  clearKey: () => void;
  setServerKeyUnset: (unset: boolean) => void;
}

export const useAdminKeyStore = create<AdminKeyState>((set) => ({
  key: null,
  serverKeyUnset: false,
  setKey: (key) => set({ key }),
  clearKey: () => set({ key: null }),
  setServerKeyUnset: (serverKeyUnset) => set({ serverKeyUnset }),
}));
