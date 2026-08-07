import { test as base, expect, type Page } from '@playwright/test';
import { credentials } from '../global-setup';
import { CONSOLE_URL } from '../playwright.config';

export { expect } from '@playwright/test';

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
  // Wait for whichever of the two things can appear, not for the network to fall quiet.
  // waitForURL on "console or login" is useless — the console URL is already true the instant goto
  // returns, before the redirect to Hydra has been issued. And networkidle never arrives: the
  // console keeps requests in flight, so a sixty-second timeout expired on a page that had been
  // ready for fifty-nine of them.
  await expect(shell(page).or(emailField(page))).toBeVisible({ timeout: 30_000 });

  if (await emailField(page).isVisible()) {
    await page.getByRole('textbox', { name: /^email/i }).fill(email);
    await page.getByRole('textbox', { name: /^password/i }).fill(password);
    await page.getByRole('button', { name: /continue/i }).click();
  }

  await page.waitForURL(url => url.href.startsWith(`${CONSOLE_URL}/console`), { timeout: 30_000 });
  // The shell is what tells us the token round-tripped: it only renders once the SDK holds one.
  await expect(shell(page)).toBeVisible({ timeout: 20_000 });
}

/** The sign-in form's identity field — the other thing a console URL can resolve to. */
export function emailField(page: Page) {
  return page.getByRole('textbox', { name: /^email/i });
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

  // A fresh load may come back through the sign-in form: the token is gone with the page, and
  // whether Hydra recognises the browser depends on an SSO session whose lifetime is a deployment
  // decision. Answering the form here keeps this helper about *the route* rather than about that
  // decision — a page that cannot be reached at all still fails, which is the point.
  await expect(shell(page).or(emailField(page))).toBeVisible({ timeout: 30_000 });
  if (await emailField(page).isVisible()) {
    const { email, password } = credentials();
    await emailField(page).fill(email);
    await page.getByRole('textbox', { name: /^password/i }).fill(password);
    await page.getByRole('button', { name: /continue/i }).click();
  }

  await expect(shell(page)).toBeVisible({ timeout: 30_000 });
}

/**
 * Signs the browser in as someone other than the bootstrap administrator.
 *
 * The boundary tests need identities that are genuinely refused things, and the only way to hold
 * one is to complete the real sign-in with it — a storageState captured for the super-admin proves
 * nothing about what an organisation's own administrator may reach. Contexts using this must opt
 * out of the project's stored state, or Hydra recognises the browser and skips the form.
 */
export async function signInAs(page: Page, email: string, password: string): Promise<void> {
  await page.goto(`${CONSOLE_URL}/console/`);
  await expect(shell(page).or(emailField(page))).toBeVisible({ timeout: 30_000 });

  if (await emailField(page).isVisible()) {
    await emailField(page).fill(email);
    await page.getByRole('textbox', { name: /^password/i }).fill(password);
    await page.getByRole('button', { name: /continue/i }).click();
  }

  await expect(shell(page)).toBeVisible({ timeout: 30_000 });
}

/**
 * The id of a seeded organisation, read from the URL its own tree node navigates to.
 *
 * Specs need real ids to build `/system/organisations/{id}/…` URLs, and the seed cannot supply
 * them: it creates objects by name through the API, and the ids are whatever Postgres generated.
 * Asking the console is also the only way that stays true if the fixture is rebuilt.
 */
export async function orgIdFor(page: Page, name: string): Promise<string> {
  await gotoConsole(page, '/console/system/organisations');
  await shell(page).getByRole('tree', { name: 'Console navigation' })
    .getByRole('link', { name, exact: true }).click();
  await page.waitForURL(/\/console\/system\/organisations\/[0-9a-f-]{36}/i, { timeout: 20_000 });
  return /organisations\/([0-9a-f-]{36})/i.exec(page.url())![1];
}

export const test = base.extend<{ console: Page }>({
  console: async ({ page }, use) => {
    await signIn(page);
    await use(page);
  },
});
