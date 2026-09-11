import { create } from 'zustand';

/** Row height of the surface tables. Expanded is the default everywhere. */
export type TableDensity = 'expanded' | 'compact';

/**
 * The viewer's table density, held in memory for the lifetime of the tab and
 * nowhere else.
 *
 * There is deliberately NO `persist` middleware, for the same reason
 * `adminKeyStore` has none: a CI guard greps src/ for
 * localStorage|sessionStorage, and persisting through it would mean reaching
 * for one of them (or a cookie, or the URL). Decision 6 of the shell epic
 * (arb-tz3) settles the question the other way round -- density is "persisted
 * per viewer in session state", which is this store, not the server.
 *
 * It is a VIEWER PREFERENCE and is never sent to the server: there is no
 * settings key for it, and it must not gain one. Density is a property of how
 * one person is looking at a table right now, not of the deployment -- two
 * operators on the same instance can disagree about it without either being
 * wrong, which is exactly what a server-side setting could not express.
 */
interface TableDensityState {
  density: TableDensity;
  setDensity: (density: TableDensity) => void;
  toggleDensity: () => void;
}

export const useTableDensityStore = create<TableDensityState>((set) => ({
  density: 'expanded',
  setDensity: (density) => set({ density }),
  toggleDensity: () =>
    set((state) => ({ density: state.density === 'compact' ? 'expanded' : 'compact' })),
}));
