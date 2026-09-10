/**
 * The single path -> label mapping for the app's seven surfaces.
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
  ['/activity', 'Activity'],
  ['/settings', 'Settings'],
  ['/system', 'System'],
];

/** The app name suffixed onto every route's title. */
export const APP_NAME = 'Arbitarr';

/** Title used for any path that does not match a row in ROUTES. */
export const NOT_FOUND_TITLE = `Page not found — ${APP_NAME}`;

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
