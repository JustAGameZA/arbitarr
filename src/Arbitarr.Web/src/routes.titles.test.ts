import { describe, expect, it } from 'vitest';
import { createRoutesFromElements } from 'react-router-dom';

import { ROUTES } from './routes.titles';
import { APP_ROUTE_ELEMENTS } from './routes';
import { NAV_ENTRIES } from './components/shell/SidebarNav';

/**
 * Cross-pins ROUTES (routes.titles.ts) to NAV_ENTRIES (SidebarNav.tsx), which
 * is the only mechanism that makes a missing title row *fail* rather than pass.
 *
 * The two `it.each(ROUTES)` suites -- pageTitle.test.tsx and documentTitle.test.tsx
 * -- derive their cases from the table under test, so deleting a row deletes its
 * own check: each suite simply shrinks by one case and reports 0 failed.
 *
 * Nothing on the other side catches it either, and for a reason worth stating,
 * since routing.test.tsx looks like it should. That suite sweeps NAV_ENTRIES, not
 * ROUTES, so it is the NAV_ENTRIES-side check rather than an instance of the
 * failure mode above -- and what it asserts per surface is the rendered <h1> and
 * the absence of the 404 heading, neither of which a missing ROUTES row disturbs.
 * SidebarNav.test.tsx likewise reads only NAV_ENTRIES. So the deleted row leaves
 * every existing suite green, and the regression is real: the surface still routes
 * and still renders, but `resolveDocumentTitle` misses and the browser tab reads
 * "Page not found".
 *
 * The assertions below are deliberately NOT parameterised over either table --
 * a case list derived from the table under test is exactly the shape that fails
 * open. They compare the two path sets directly, in both directions, so a row
 * present in one table and absent from the other fails whichever way round the
 * omission happened.
 */
describe('ROUTES and NAV_ENTRIES cover the same paths', () => {
  const routePaths = ROUTES.map(([path]) => path);
  const navPaths = NAV_ENTRIES.map((entry) => entry.to);

  it('gives every nav entry a title row', () => {
    // The direction that catches a deleted routes.titles.ts row: the sidebar
    // still links to the surface, so it is reachable and rendering, while its
    // document title has silently fallen through to NOT_FOUND_TITLE.
    expect(routePaths).toEqual(expect.arrayContaining(navPaths));
  });

  it('gives every title row a nav entry', () => {
    // The converse holds today with no exceptions: every ROUTES path is in the
    // sidebar. `/login` and `/setup` are not counter-examples -- they are absent
    // from BOTH tables on purpose (see routes.tsx's note on why they sit outside
    // the guarded shell), and so is the catch-all `*`. If a surface is ever added
    // that is routable and titled but intentionally not in the sidebar, subtract
    // it here BY NAME rather than weakening this to a one-way containment check,
    // so the next unintended omission still fails.
    expect(navPaths).toEqual(expect.arrayContaining(routePaths));
  });

  it('holds the two tables to the same length, so neither gains a duplicate row', () => {
    // Containment both ways is satisfiable by a duplicate: ['/','/'] contains
    // ['/'] and vice versa. Ruling duplicates out is what closes that, and with
    // both tables duplicate-free the two containments above already force the
    // sets equal -- so equal lengths follow rather than needing their own pin.
    //
    // Deliberately no literal count here. SidebarNav.test.tsx owns "exactly
    // eight" (it asserts the rendered list, where a literal is load-bearing
    // because the DOM has no table to compare against). Repeating the 8 here
    // would add a fourth site to edit for a ninth surface, and a stale copy
    // fails for the wrong reason -- it reports a count that is merely out of
    // date, not the drift between the two tables that this suite exists to find.
    expect(routePaths).toHaveLength(navPaths.length);
    expect(new Set(routePaths).size).toBe(routePaths.length);
    expect(new Set(navPaths).size).toBe(navPaths.length);
  });
});

/**
 * The third pin (arb-2bms): routes.tsx's JSX against ROUTES.
 *
 * The suite above holds ROUTES and NAV_ENTRIES together, which leaves routes.tsx
 * as the one table nothing read back. Mutation proved that gap real: adding
 * `<Route path="reports" .../>` to the shell and nowhere else left all five
 * suites at 0 failed, while the surface routed, rendered, and showed a "Page not
 * found" browser tab -- because `resolveDocumentTitle` missed. The two
 * `it.each(ROUTES)` suites never see a path that is not in ROUTES, routing.test.tsx
 * sweeps NAV_ENTRIES, and SidebarNav.test.tsx reads NAV_ENTRIES too, so a route
 * that exists in neither table is invisible to every one of them.
 *
 * The paths below are read out of the ACTUAL route elements with
 * `createRoutesFromElements` -- the same conversion React Router performs
 * internally -- rather than from a hoisted data table that routes.tsx maps over.
 * A data table would be a second copy of the route list, and a pin against a copy
 * cannot catch a `<Route>` added straight to the JSX. Converting the elements
 * means what is asserted is what the app renders.
 */
describe('the shell route table and ROUTES cover the same paths (arb-2bms)', () => {
  // Only the shell's children are in scope: `login` and `setup` are siblings of
  // the shell route, not children (see routes.tsx on why that placement is
  // load-bearing), so they fall outside this pin by structure rather than by
  // being listed as exceptions.
  const shellChildren =
    createRoutesFromElements(APP_ROUTE_ELEMENTS).find((route) => route.children)?.children ?? [];

  // `path` is undefined for the index route, which is how React Router represents
  // it; it is carried here as the sentinel 'index' so the exception list below can
  // name it. Every other entry contributes its literal path.
  const shellPaths = shellChildren.map((route) => ('index' in route && route.index ? 'index' : route.path));

  /**
   * The shell children that intentionally have no ROUTES row, BY NAME.
   *
   * Named rather than filtered by predicate so that a new omission still fails:
   * a rule like "skip anything with a `*`" would silently absorb the next
   * unintended one. `index` is the Dashboard, which IS in ROUTES but under the
   * path `/` -- it is mapped rather than excepted, below. `*` is the catch-all,
   * which is deliberately absent from ROUTES (it is what NOT_FOUND_TITLE covers).
   */
  const EXPECTED_EXCEPTIONS = ['*'];

  // ROUTES spells the index route `/`; the JSX spells it `index`. Normalising one
  // to the other is what lets the two be compared as sets at all.
  const pinnedShellPaths = shellPaths
    .filter((path) => !EXPECTED_EXCEPTIONS.includes(path as string))
    .map((path) => (path === 'index' ? '/' : `/${path}`));

  const routePaths = ROUTES.map(([path]) => path);

  it('gives every shell route a title row', () => {
    // The direction that catches the bug this suite was written for: a <Route>
    // added to the shell with no ROUTES row. The surface routes and renders, so
    // nothing else fails -- only its tab title is wrong.
    expect(routePaths).toEqual(expect.arrayContaining(pinnedShellPaths));
  });

  it('gives every title row a shell route', () => {
    // The converse: a ROUTES row whose <Route> was deleted. `resolveDocumentTitle`
    // would still answer for the path while nothing routed there.
    expect(pinnedShellPaths).toEqual(expect.arrayContaining(routePaths));
  });

  it('holds the exception list to exactly the shell paths that have no title row', () => {
    // Pins the allow-list itself. Without this, removing `*` from
    // EXPECTED_EXCEPTIONS would just add `/*` to the compared set and fail the
    // containment above for a confusing reason -- and, worse, ADDING an entry
    // here would silently excuse a real omission. Deriving the expected set from
    // the two tables rather than restating it keeps the list honest in both
    // directions.
    const shellPathsWithoutTitleRow = shellPaths.filter(
      (path) => !routePaths.includes(path === 'index' ? '/' : `/${path}`),
    );

    expect(EXPECTED_EXCEPTIONS).toEqual(shellPathsWithoutTitleRow);
  });

  it('holds the two tables to the same length, so neither gains a duplicate row', () => {
    // Same reasoning as the NAV_ENTRIES suite above: containment both ways is
    // satisfiable by a duplicate, and ruling duplicates out is what forces the
    // sets equal. No literal count here either, for the same reason.
    expect(pinnedShellPaths).toHaveLength(routePaths.length);
    expect(new Set(pinnedShellPaths).size).toBe(pinnedShellPaths.length);
  });
});
