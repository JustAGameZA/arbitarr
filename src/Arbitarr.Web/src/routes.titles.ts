/**
 * The single path -> label mapping for the app's six surfaces.
 *
 * Two consumers read this table and must never drift apart:
 *  - pageTitle.test.tsx, which asserts the in-page <h1> (AC2b: exactly one
 *    page title per view, living in the content pane).
 *  - AppShell's document-title effect (documentTitle.test.tsx), which sets
 *    the browser tab title per route.
 *
 * Adding a surface means adding a row here -- nowhere else.
 */
export const ROUTES: ReadonlyArray<[string, string]> = [
  ['/', 'Dashboard'],
  ['/search', 'Search'],
  ['/rules', 'Rules'],
  ['/suppressions', 'Suppressions'],
  ['/settings', 'Settings'],
  ['/system', 'System'],
];

/** The app name suffixed onto every non-index title. */
export const APP_NAME = 'Arbitarr';

/** Title used for the index route (plain app name, no page name prefix). */
export const INDEX_TITLE = APP_NAME;

/** Title used for any path that does not match a row in ROUTES. */
export const NOT_FOUND_TITLE = `Page not found — ${APP_NAME}`;

/**
 * Resolves the document title for a given pathname.
 *
 * The index route ("/") gets the plain app name; every other known route
 * gets "<Name> — Arbitarr"; anything unmatched falls back to the 404 title.
 */
export function resolveDocumentTitle(pathname: string): string {
  if (pathname === '/') {
    return INDEX_TITLE;
  }

  const match = ROUTES.find(([path]) => path === pathname);
  if (!match) {
    return NOT_FOUND_TITLE;
  }

  const [, label] = match;
  return `${label} — ${APP_NAME}`;
}
