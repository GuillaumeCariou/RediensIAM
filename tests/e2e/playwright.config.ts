import { defineConfig, devices } from '@playwright/test';
import 'dotenv/config';

/**
 * End-to-end tests against a running RediensIAM.
 *
 * These talk to the real stack — the app, Hydra, Keto, Postgres and Dragonfly — and mock nothing.
 * The previous suite routed the admin API through `page.route()` mocks, which made it a second,
 * slower copy of the vitest suites (95 tests in the console, 84 in the login SPA) while proving
 * nothing about the wiring between them. What only an end-to-end test can show is the chain: an
 * OAuth2 redirect that actually round-trips, a role that Keto actually grants, a row that actually
 * reaches Postgres.
 *
 * They therefore need a deployment. `./deploy/setup.sh --dev` produces one, and writes the
 * bootstrap administrator into deploy/rediensiam/values.secret.yaml — which is where
 * global-setup.ts reads it from unless TEST_SUPER_ADMIN_EMAIL / _PASSWORD say otherwise.
 *
 * The two origins are not interchangeable: the login pages and the API answer on APP_URL, the
 * console is served from CONSOLE_URL, and the whole point of several tests below is that a
 * redirect crosses between them correctly.
 */
export const APP_URL     = process.env.TEST_APP_URL     ?? 'http://iam.localhost';
// https, and not by preference: the admin ingress is TLS-only — cert-manager issues it a
// self-signed certificate and the router has no http entry, so http answers 404. `ignoreHTTPSErrors`
// below is what makes that certificate usable from a test browser.
export const CONSOLE_URL = process.env.TEST_CONSOLE_URL ?? 'https://admin.iam.localhost';

export default defineConfig({
  testDir: './tests',
  // Sequential by default: every spec drives one deployment, and several create organisations or
  // projects whose names appear in each other's lists. Parallelism here buys seconds and costs
  // reproducibility.
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  reporter: [['list'], ['html', { open: 'never' }]],
  globalSetup: './global-setup.ts',

  use: {
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    // A real deployment on http, so nothing here has a certificate worth checking.
    ignoreHTTPSErrors: true,
  },

  projects: [
    {
      // Signs in once and saves the cookies. Playwright's guidance, and the reason for it, is in
      // setup/authenticate.setup.ts.
      name: 'setup',
      testMatch: 'tests/setup/**/*.setup.ts',
      use: { ...devices['Desktop Chrome'], baseURL: CONSOLE_URL },
    },
    {
      // No stored state on purpose: these tests are about what an anonymous visitor sees, and
      // several of them assert that a wrong password is refused.
      name: 'login',
      testMatch: 'tests/login/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'], baseURL: APP_URL },
    },
    {
      name: 'console',
      testMatch: 'tests/console/**/*.spec.ts',
      dependencies: ['setup'],
      use: {
        ...devices['Desktop Chrome'],
        baseURL: CONSOLE_URL,
        // Hydra's session cookie. The access token is not in here and cannot be — it lives in a
        // private field — so each test still completes a real OAuth2 round trip. What it skips is
        // the form, which is the slow and collision-prone part.
        storageState: './.auth/session.json',
      },
    },
  ],
});
