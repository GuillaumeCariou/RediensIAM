/**
 * auth.ts — Playwright fixture that provides an admin SPA page
 * pre-loaded with the OIDC token from global-setup.
 *
 * Usage:
 *   import { test, expect } from '../fixtures/auth';
 *   test('...', async ({ adminPage }) => { ... });
 */
import { test as baseTest, expect, type Page } from '@playwright/test';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

export { expect };

const __dirname   = path.dirname(fileURLToPath(import.meta.url));
const SESSION_FILE = path.join(__dirname, '../.auth/admin-session.json');

function loadSession(): Record<string, string> {
  if (!fs.existsSync(SESSION_FILE)) {
    throw new Error(
      `Auth session file not found: ${SESSION_FILE}\n` +
      'Run the full test suite first (globalSetup will create it), or run:\n' +
      '  npx playwright test --project=admin (it will run global-setup automatically)'
    );
  }
  return JSON.parse(fs.readFileSync(SESSION_FILE, 'utf8'));
}

type AuthFixtures = { adminPage: Page };

export const test = baseTest.extend<AuthFixtures>({
  /**
   * adminPage — a Chromium page with the admin OIDC session pre-injected.
   *
   * The fixture injects all captured sessionStorage keys before the page
   * loads so that oidc-client-ts finds its stored token and skips the
   * redirect-to-login flow.
   */
  adminPage: async ({ browser }, use) => {
    const session = loadSession();

    const context = await browser.newContext();
    const page    = await context.newPage();

    // Inject sessionStorage before any page script runs
    await page.addInitScript((state: Record<string, string>) => {
      for (const [k, v] of Object.entries(state)) {
        sessionStorage.setItem(k, v);
      }
    }, session);

    await use(page);
    await context.close();
  },
});
