import { defineConfig, devices } from '@playwright/test';

/**
 * S1-10 real-browser tests (ADR-0019, DL-018). The portal's production build is served by scripts/serve-e2e.mjs under
 * a strict CSP; every test fails on a CSP violation or page error (see e2e/fixtures.ts).
 *
 * Projects:
 * - chromium: keyboard, focus, axe (with colour contrast), forced colours, CSP, phone width. Runs in CI.
 * - visual: screenshot baselines (light/dark, desktop/phone). Baselines are per platform, so it runs only when
 *   IAM_VISUAL=1; Linux baselines for CI are produced in the pinned Playwright container (see docs).
 */
const port = Number(process.env['IAM_E2E_PORT'] ?? 4300);
const visual = process.env['IAM_VISUAL'] === '1';

export default defineConfig({
  testDir: './e2e',
  outputDir: './tmp/e2e-results',
  snapshotPathTemplate: '{testDir}/__screenshots__/{platform}/{arg}{ext}',
  fullyParallel: true,
  forbidOnly: !!process.env['CI'],
  retries: 0,
  workers: process.env['CI'] ? 2 : undefined,
  timeout: 30_000,
  reporter: process.env['CI']
    ? [['list'], ['html', { open: 'never', outputFolder: 'tmp/e2e-report' }]]
    : 'list',
  use: {
    baseURL: `http://127.0.0.1:${port}`,
    // The portal formats dates with Angular's LOCALE_ID (en-US until a locale decision is made).
    locale: 'en-US',
    timezoneId: 'Asia/Kolkata',
    trace: 'retain-on-failure',
    // Never weaken the page's CSP for tests.
    bypassCSP: false,
  },
  expect: {
    toHaveScreenshot: { maxDiffPixelRatio: 0.01, animations: 'disabled', caret: 'hide' },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
      testIgnore: /visual\.spec\.ts/,
    },
    ...(visual
      ? [{ name: 'visual', use: { ...devices['Desktop Chrome'] }, testMatch: /visual\.spec\.ts/ }]
      : []),
  ],
  webServer: {
    command: 'node scripts/serve-e2e.mjs',
    url: `http://127.0.0.1:${port}/`,
    reuseExistingServer: !process.env['CI'],
    env: { IAM_E2E_PORT: String(port) },
    timeout: 30_000,
  },
});
