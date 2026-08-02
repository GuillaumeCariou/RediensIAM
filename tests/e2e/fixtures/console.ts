import { test as base, expect, type Page } from '@playwright/test';
import { credentials } from '../global-setup';
import { APP_URL, CONSOLE_URL } from '../playwright.config';

export { expect };

/**
 * Signs the browser into the admin console, through the real OAuth2 round trip.
 *
 * There is no storageState shortcut here and there cannot be one. The console runs on
 * `rediensiam-web`, which keeps the access token in a private field and writes nothing to
 * localStorage or sessionStorage — deliberately, so that a token does not outlive the tab. The
 * previous suite captured sessionStorage in a global setup and replayed it into every context;
 * that worked against `oidc-client-ts` and became a no-op the day the SDK replaced it, which is
 * how ten specs came to authenticate nothing at all.
 *
 * Driving the form is therefore the only way in — and it means the login flow itself is exercised
 * by every console test rather than by one. After the first sign-in Hydra holds an SSO session in
 * the context's cookies, so later navigations round-trip without a form.
 */
export async function signIn(page: Page): Promise<void> {
  const { email, password } = credentials();

  await page.goto(`${CONSOLE_URL}/console/`);
  // Not waitForURL on "console or login": the console URL is already true the instant goto
  // returns, so that predicate resolves before the redirect to Hydra has even been issued and the
  // form is never filled. What settles the question is the network going quiet.
  await page.waitForLoadState('networkidle');

  if (page.url().startsWith(`${APP_URL}/login`)) {
    await page.getByRole('textbox', { name: /^email/i }).fill(email);
    await page.getByRole('textbox', { name: /^password/i }).fill(password);
    await page.getByRole('button', { name: /continue/i }).click();
  }

  await page.waitForURL(url => url.href.startsWith(`${CONSOLE_URL}/console`), { timeout: 30_000 });
  // The shell is what tells us the token round-tripped: it only renders once the SDK holds one.
  await expect(shell(page)).toBeVisible({ timeout: 20_000 });
}

/** The console shell. Present only once the SDK holds a token, so it is what "signed in" means. */
export function shell(page: Page) {
  return page.locator('.iam-sidebar');
}

/**
 * Navigates to a console route and waits for the shell.
 *
 * A full page load throws the token away — it lives in a private field, not in storage — so every
 * `goto` re-runs the whole OAuth2 round trip against Hydra's SSO session before anything renders.
 * Asserting on page content straight after a goto races that, and the failure looks like a missing
 * element rather than an unfinished sign-in. Within the SPA, follow links instead; this is for
 * the cases that genuinely need a fresh load.
 */
export async function gotoConsole(page: Page, path: string): Promise<void> {
  await page.goto(`${CONSOLE_URL}${path}`);
  await expect(shell(page)).toBeVisible({ timeout: 30_000 });
}

export const test = base.extend<{ console: Page }>({
  console: async ({ page }, use) => {
    await signIn(page);
    await use(page);
  },
});
