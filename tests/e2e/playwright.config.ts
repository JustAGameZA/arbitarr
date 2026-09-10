import { defineConfig, devices } from '@playwright/test';

/**
 * E2E golden path (arb-rga.7). Chromium only: this lane exists to prove the product's own
 * path end to end -- bootstrap, add a source, search, see rows -- not to check rendering
 * across engines. A second browser would double the slowest required check's wall clock
 * for evidence nothing in the acceptance criteria asks for.
 *
 * The app is started by tests/e2e/compose.yml, not by `webServer` here: the image under
 * test is the one `Deploy review environment` already built and asserted on, and starting
 * it a second way would let the E2E pass against something else.
 */
export default defineConfig({
  testDir: './tests',

  // No retries. Component 4 of the spec is explicit: no automatic retries in the PR lane,
  // because a retry converts a real intermittent failure into a green tick and the flake
  // is then only visible to whoever reads the logs.
  retries: 0,

  // Serial. These tests share one container's mutable state -- the admin key is bootstrapped
  // once and a source is created once -- so they are steps of one path, not independent
  // cases, and running them concurrently would race that state.
  workers: 1,
  fullyParallel: false,

  // Fails the run rather than passing, if a stray .only is ever committed.
  forbidOnly: !!process.env.CI,

  timeout: 30_000,
  expect: { timeout: 10_000 },

  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],

  use: {
    baseURL: process.env.ARBITARR_BASE_URL || 'http://127.0.0.1:8080',
    // Kept on the first failure only: the trace is the artifact the workflow uploads, and
    // it is what makes a CI-only failure diagnosable without a rerun.
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
