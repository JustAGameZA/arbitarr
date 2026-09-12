/**
 * The single path -> label mapping for the app's eight surfaces.
 *
 * Four consumers read this table and must never drift apart:
 *  - pageTitle.test.tsx, which asserts the in-page <h1> (AC2b: exactly one
 *    page title per view, living in the content pane).
 *  - AppShell's document-title effect (documentTitle.test.tsx), which sets
 *    the browser tab title per route.
 *  - Login.tsx, which sets `document.title` to `LOGIN_TITLE` on mount.
 *  - Setup.tsx, which sets `document.title` to `SETUP_TITLE` on mount.
 *
 * "Must never drift apart" is an assertion, not a mechanism: the two test suites
 * listed above are both `it.each(ROUTES)`, deriving their cases from this very
 * table, so deleting a row deletes its own check and each shrinks by a case with
 * 0 failed. routing.test.tsx is not a third instance of that -- it sweeps
 * NAV_ENTRIES, so it is the check on the other table, and it stays green too. The
 * mechanism that actually catches a missing row is routes.titles.test.ts, which
 * cross-pins this table to NAV_ENTRIES -- a table it does NOT derive its cases
 * from. Do not replace that suite with another `it.each(ROUTES)` one (arb-139r).
 *
 * Adding a surface means adding a row here -- and the matching NAV_ENTRIES row,
 * which is the pair routes.titles.test.ts holds together.
 */
export const ROUTES: ReadonlyArray<[string, string]> = [
  ['/', 'Dashboard'],
  ['/search', 'Search'],
  ['/rules', 'Rules'],
  ['/suppressions', 'Suppressions'],
  ['/activity', 'Activity'],
  ['/library', 'Library'],
  ['/settings', 'Settings'],
  ['/system', 'System'],
];

/** The app name suffixed onto every route's title. */
export const APP_NAME = 'Arbitarr';

/** Title used for any path that does not match a row in ROUTES. */
export const NOT_FOUND_TITLE = `Page not found — ${APP_NAME}`;

/**
 * Title for the sign-in screen (arb-7m7).
 *
 * `/login` is deliberately absent from ROUTES above -- see routes.tsx's note on
 * why it and `/setup` sit outside the guarded shell -- so it cannot pick up a
 * title through `resolveDocumentTitle`. Kept here rather than as a literal in
 * Login.tsx so the title mechanism stays in one file, the same reason every
 * other route's title lives in this table rather than at its call site.
 */
export const LOGIN_TITLE = `Sign in — ${APP_NAME}`;

/**
 * Title for the first-run account-creation screen (arb-xwbl).
 *
 * `/setup` is deliberately absent from ROUTES above -- see routes.tsx's note
 * on why it and `/login` sit outside the guarded shell -- so it cannot pick
 * up a title through `resolveDocumentTitle`. Kept here rather than as a
 * literal in Setup.tsx so the title mechanism stays in one file, the same
 * reason every other route's title lives in this table rather than at its
 * call site.
 */
export const SETUP_TITLE = `Set up — ${APP_NAME}`;

/**
 * Resolves the document title for a given pathname.
 *
 * Every known route -- including the index route ("/"), which is the
 * Dashboard -- gets "<Name> — Arbitarr", the same convention across the
 * board; anything unmatched falls back to the 404 title.
 */
export function resolveDocumentTitle(pathname: string): string {
  const match = ROUTES.find(([path]) => path === pathname);
  if (!match) {
    return NOT_FOUND_TITLE;
  }

  const [, label] = match;
  return `${label} — ${APP_NAME}`;
}
