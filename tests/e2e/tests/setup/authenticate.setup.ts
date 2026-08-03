import { test as setup, expect } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { credentials } from '../../global-setup';
import { APP_URL, CONSOLE_URL } from '../../playwright.config';

export const SESSION_FILE = path.join(
  path.dirname(fileURLToPath(import.meta.url)), '..', '..', '.auth', 'session.json',
);

/**
 * Signs in once, and saves the cookies.
 *
 * Playwright's own guidance is to reuse an authentication state rather than sign in per test, and
 * it says why: "Redoing the login process for every test can significantly slow down test
 * execution." Fifteen console tests each driving the form took nearly two minutes and, worse, made
 * Hydra reject a CSRF cookie belonging to a flow a previous test had abandoned mid-redirect.
 *
 * What is saved is cookies only, and that is enough — but only since the deployment started asking
 * Hydra to remember a login. `storageState` persists cookies and localStorage; the console's access
 * token lives in a private field and is in neither, so there is nothing to replay and each test
 * still completes a real OAuth2 round trip. The difference is that Hydra now recognises the browser
 * and skips the form, which is the part that was slow.
 */
setup('authenticate once and keep the SSO session', async ({ page, context }) => {
  const { email, password } = credentials();

  await page.goto(`${CONSOLE_URL}/console/`);
  const emailField = page.getByRole('textbox', { name: /^email/i });
  await expect(emailField).toBeVisible({ timeout: 30_000 });

  await emailField.fill(email);
  await page.getByRole('textbox', { name: /^password/i }).fill(password);
  await page.getByRole('button', { name: /continue/i }).click();

  await expect(page.locator('.iam-sidebar')).toBeVisible({ timeout: 30_000 });

  // The cookie that matters belongs to the issuer, not to the console: it is Hydra's session, set
  // on APP_URL, and it is what lets the next authorization request skip the form.
  const cookies = await context.cookies();
  expect(cookies.some(c => c.domain.includes(new URL(APP_URL).hostname)),
    'no cookie from the issuer — Hydra kept no session, so nothing here will be reused')
    .toBe(true);

  await context.storageState({ path: SESSION_FILE });
});
