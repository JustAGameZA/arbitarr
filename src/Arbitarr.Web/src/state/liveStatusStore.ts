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
  announce: (message: string) => void;
}

export const useLiveStatusStore = create<LiveStatusState>((set) => ({
  message: '',
  announce: (message) => set({ message }),
}));
