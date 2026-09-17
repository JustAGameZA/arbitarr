import { test, expect, type Page } from '@playwright/test';

/**
 * Browser-level regression cover for the mobile shell stacking fix (arb-qp5),
 * which vitest cannot exercise: jsdom has no layout engine, so the drawer's
 * off-canvas transform and the top bar's stacking rule need a real bounding
 * box and a real click target.
 *
 * Fixed at 390x844 -- the width the fix (AppShell.module.css's
 * `.topbar{position:relative;z-index:25}` inside the 768px query) was measured
 * against -- with `reducedMotion: 'reduce'` so the drawer's 180ms transition
 * (already disabled under `prefers-reduced-motion` in that stylesheet) cannot
 * race the assertions below.
 *
 * The whole run is offline: every `**\/api/**` route is mocked, `GET
 * /api/auth/session` as an authenticated, setup-complete session (the shape
 * RequireSession needs to mount the shell instead of redirecting to /login --
 * see RequireSession.tsx and state/sessionQueries.ts) and everything else with
 * an empty 200, so a missing mock fails loudly rather than the fail-open path
 * masking it.
 *
 * Navigates to an unmatched path rather than "/": the shell (sidebar, top bar,
 * drawer) is the same regardless of which route renders inside it
 * (routes.tsx), but the index route is DashboardPage, which reads richer
 * shapes out of the empty `{}` mock than this spec's blanket 200 provides and
 * throws during render -- with no error boundary in routes.tsx, that
 * unmounts the whole tree, shell included. `<Route path="*">` (NotFoundPage)
 * needs nothing from the mock, so the shell this spec is actually testing
 * mounts cleanly.
 */
test.use({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });

async function mockOfflineSession(page: Page): Promise<void> {
  // Catch-all for every API call, registered FIRST. Playwright matches routes
  // most-recently-registered first, so the session mock registered after this
  // one takes precedence for that one path while every other call still gets
  // an empty 200 -- registering them the other way round lets this broader
  // pattern swallow the session route too.
  await page.route('**/api/**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: '{}' }),
  );
  await page.route('**/api/auth/session', (route) =>
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ authenticated: true, username: 'operator', setupRequired: false }),
    }),
  );
}

test.describe('mobile shell stacking at 390x844 (arb-qp5)', () => {
  test.beforeEach(async ({ page }) => {
    await mockOfflineSession(page);
    await page.goto('/arb-qp5-unmatched-route');
  });

  test('drawer closed: the hamburger is actionable and opens the sidebar', async ({ page }) => {
    const toggle = page.getByRole('button', { name: 'Open navigation' });
    await toggle.click();

    await expect(page.getByRole('button', { name: 'Close navigation' })).toBeVisible();
    await expect(toggle).not.toBeVisible();
  });

  // This is THE assertion that fails when the `.topbar` stacking rule
  // (AppShell.module.css's `.topbar{position:relative;z-index:25}` inside the
  // 768px query) is removed: without it, the drawer's close button sits under
  // the top bar and the click below never reaches it. The other two tests in
  // this file are supporting cover, not the regression's primary signal.
  test('drawer open: the close button is actionable and closes the sidebar', async ({ page }) => {
    await page.getByRole('button', { name: 'Open navigation' }).click();

    const closeButton = page.getByRole('button', { name: 'Close navigation' });
    await closeButton.click();

    await expect(page.getByRole('button', { name: 'Open navigation' })).toBeVisible();
    await expect(closeButton).not.toBeVisible();
  });

  test('drawer closed: the aside sits off-screen', async ({ page }) => {
    const aside = page.locator('aside');
    const box = await aside.boundingBox();

    expect(box).not.toBeNull();
    // Off-canvas via translateX(-100%): the whole box sits to the left of the
    // viewport's left edge.
    expect(box!.x + box!.width).toBeLessThanOrEqual(0);
  });
});
