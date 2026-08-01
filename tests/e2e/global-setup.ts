/**
 * global-setup.ts
 *
 * Runs once before the test suite. Performs the full OIDC login flow through
 * the admin SPA → Hydra → Login SPA → back to admin SPA, then captures the
 * resulting sessionStorage (where oidc-client-ts stores the token) and writes
 * it to .auth/admin-session.json for reuse by the adminPage fixture.
 */
import { chromium } from '@playwright/test';
import 'dotenv/config';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const AUTH_DIR  = path.join(__dirname, '.auth');
const SESSION_FILE = path.join(AUTH_DIR, 'admin-session.json');

const BASE_URL = process.env.TEST_BASE_URL ?? 'http://localhost';
const EMAIL    = process.env.TEST_SUPER_ADMIN_EMAIL;
const PASSWORD = process.env.TEST_SUPER_ADMIN_PASSWORD;

export default async function globalSetup() {
  if (!EMAIL || !PASSWORD) {
    throw new Error(
      'Missing TEST_SUPER_ADMIN_EMAIL / TEST_SUPER_ADMIN_PASSWORD env vars.\n' +
      'Create tests/e2e/.env with these values before running E2E tests.'
    );
  }

  fs.mkdirSync(AUTH_DIR, { recursive: true });

  const browser = await chromium.launch();
  const context = await browser.newContext({ baseURL: BASE_URL });
  const page    = await context.newPage();

  try {
    // Triggers the OIDC redirect chain that lands us on the Login SPA.
    await page.goto('/admin/');

    await page.waitForURL(/login_challenge/, { timeout: 15_000 });

    await page.locator('#identifier').fill(EMAIL);
    await page.locator('#password').fill(PASSWORD);
    await page.getByRole('button', { name: /sign in/i }).click();

    // Hydra only shows the consent screen the first time a client is granted, so the run
    // has to succeed whether or not it appears: race the direct landing against the
    // click-through, and swallow the click's failure when there is no consent page.
    await Promise.race([
      page.waitForURL(/\/admin\//, { timeout: 10_000 }),
      page.getByRole('button', { name: /allow|accept/i }).click().then(() =>
        page.waitForURL(/\/admin\//, { timeout: 10_000 })
      ).catch(() => {}),
    ]);

    // The token is written by the SPA's callback handler, not by the navigation, so there is
    // nothing to wait on but the network going quiet. A timeout here is not fatal — the
    // sessionStorage check below is the real assertion — hence the swallowed rejection.
    await page.waitForLoadState('networkidle', { timeout: 10_000 }).catch(() => {});

    // oidc-client-ts keys the stored user by issuer and client id, so capture every entry
    // rather than guessing the key.
    const sessionState = await page.evaluate((): Record<string, string> => {
      const out: Record<string, string> = {};
      for (let i = 0; i < sessionStorage.length; i++) {
        const k = sessionStorage.key(i)!;
        out[k] = sessionStorage.getItem(k)!;
      }
      return out;
    });

    if (Object.keys(sessionState).length === 0) {
      throw new Error(
        'sessionStorage is empty after login — OIDC flow may not have completed.\n' +
        'Check that the admin SPA is reachable at ' + BASE_URL + '/admin/'
      );
    }

    fs.writeFileSync(SESSION_FILE, JSON.stringify(sessionState, null, 2));
    console.log(`[global-setup] Auth state saved → ${SESSION_FILE}`);
  } finally {
    await browser.close();
  }
}
