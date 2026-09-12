import { describe, expect, it } from 'vitest';

import { ROUTES } from './routes.titles';
import { NAV_ENTRIES } from './components/shell/SidebarNav';

/**
 * Cross-pins ROUTES (routes.titles.ts) to NAV_ENTRIES (SidebarNav.tsx), which
 * is the only mechanism that makes a missing title row *fail* rather than pass.
 *
 * The three `it.each(ROUTES)` suites -- routing.test.tsx, pageTitle.test.tsx and
 * documentTitle.test.tsx -- derive their cases from the table under test, so
 * deleting a row deletes its own check: the suite simply shrinks and reports 0
 * failed. SidebarNav.test.tsx reads NAV_ENTRIES, a different table, so it stays
 * green too. The regression that escapes both is real: the surface still routes
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
    // ['/'] and vice versa. Pinning the lengths closes that, and pins the count
    // the two comments in routes.titles.ts and SidebarNav.tsx both state.
    expect(routePaths).toHaveLength(8);
    expect(navPaths).toHaveLength(8);
    expect(new Set(routePaths).size).toBe(routePaths.length);
    expect(new Set(navPaths).size).toBe(navPaths.length);
  });
});
