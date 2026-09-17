import { createContext } from 'react';

/**
 * Lets RouteError signal AppShell that the error panel is up, instead of
 * RouteError writing `document.title` itself.
 *
 * AppShell is the single owner of `document.title` (see its pathname-keyed
 * effect): a class-component `componentDidCatch` runs during the commit
 * phase, but AppShell's title effect is a passive effect that runs AFTER
 * commit for the whole tree, so a boundary nested inside AppShell (the common
 * case -- routes.tsx's "Placement 2") would have any direct title write it
 * made immediately overwritten. Routing the signal through AppShell's own
 * state instead removes the race by construction: there is exactly one
 * effect that ever assigns `document.title`.
 *
 * The default value is a no-op setter, not `undefined`, so a `RouteError`
 * rendered with no provider above it (a unit test, or the "Placement 1"
 * backstop around the whole `<Routes>` in AppRoutes -- see RouteError.tsx's
 * class comment) still works: it just has no title to signal.
 */
export const RouteErrorContext = createContext<(routeErrored: boolean) => void>(() => {});
