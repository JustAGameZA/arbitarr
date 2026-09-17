import { defineConfig, devices } from '@playwright/test';

/**
 * A minimal, CI-only Playwright project guarding the mobile shell stacking fix
 * at 390px (AppShell.module.css's `.topbar{position:relative;z-index:25}` rule
 * inside the 768px query, arb-qp5). jsdom has no layout engine, so this is the
 * only way to assert the real bounding boxes and click targets vitest cannot.
 *
 * Chromium only, one spec, deliberately not part of the required `Build & test`
 * gate yet -- see the `playwright` job in .github/workflows/build-test.yml.
 */
export default defineConfig({
  testDir: './e2e',
  // This repo does not retry flaky tests (CLAUDE.md/CONTRIBUTING.md): a spec
  // that needs a retry to pass is not done, so this is 0 everywhere, not just
  // in CI.
  retries: 0,
  // Fails the run if a `test.only` was left in by accident, matching this
  // repo's no-skip/no-only rule for vitest.
  forbidOnly: !!process.env.CI,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  webServer: {
    // The default vite build outDir points at the Host's wwwroot
    // (vite.config.ts); this spec builds to a throwaway `dist/` instead so it
    // never touches that directory or the Host's static files.
    command:
      'npx vite build --outDir dist && npx vite preview --outDir dist --host 127.0.0.1 --port 4173 --strictPort',
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: !process.env.CI,
  },
});
