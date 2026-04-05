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
  ],
});
