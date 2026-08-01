import { defineConfig, devices } from '@playwright/test';
import 'dotenv/config';

const BASE_URL = process.env.TEST_BASE_URL ?? 'http://localhost';

export default defineConfig({
  testDir: './tests',
  fullyParallel: true,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: [['html', { open: 'never' }], ['list']],
  globalSetup: './global-setup.ts',

  use: {
    baseURL: BASE_URL,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    video: 'on-first-retry',
  },

  projects: [
    // ── Admin SPA — runs with a pre-authenticated session ───────────────────
    {
      name: 'admin',
      testMatch: 'tests/admin/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'] },
    },

    // ── Account SPA — runs with a pre-authenticated session ─────────────────
    {
      name: 'account',
      testMatch: 'tests/account/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'] },
    },

    // ── Login SPA — unauthenticated, hits real backend ───────────────────────
    {
      name: 'login',
      testMatch: 'tests/login/**/*.spec.ts',
      use: {
        ...devices['Desktop Chrome'],
        storageState: { cookies: [], origins: [] },
      },
    },

    /**
     * Org-scoped admin pages. This project was missing: `tests/org/` existed on disk with nine
     * SMTP tests — including the one asserting the super-admin endpoint is scoped — and no
     * `testMatch` selected them, so `--list` collected 150 tests from 14 files while 15 were
     * present. They had never run. A spec the runner does not collect is worth exactly what a
     * spec that does not exist is worth, and looks like the opposite.
     */
    {
      name: 'org',
      testMatch: 'tests/org/**/*.spec.ts',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
