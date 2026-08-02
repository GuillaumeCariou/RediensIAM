import { test, expect } from '@playwright/test';
import { credentials } from '../../global-setup';
import { signIn } from '../../fixtures/console';
import { APP_URL, CONSOLE_URL } from '../../playwright.config';

/**
 * The sign-in round trip, as a browser performs it: console → Hydra → login page → back.
 *
 * Nothing here is mocked, so a pass means the OAuth2 client is registered, the challenge binds to
 * a project, Keto answered on the role, Hydra minted a token and the console accepted it. That
 * chain is the one thing a unit test cannot reach.
 */

test('an unauthenticated visit to the console lands on the login page', async ({ page }) => {
  await page.goto(`${CONSOLE_URL}/console/`);

  await page.waitForURL(url => url.href.startsWith(`${APP_URL}/login`), { timeout: 30_000 });
  await expect(page).toHaveURL(/login_challenge=/);
  await expect(page.getByRole('textbox', { name: /^email/i })).toBeVisible();
});

test('the right password completes the round trip into the console', async ({ page }) => {
  const { email, password } = credentials();

  await page.goto(`${CONSOLE_URL}/console/`);
  await page.waitForURL(/\/login/, { timeout: 30_000 });
  await page.getByRole('textbox', { name: /^email/i }).fill(email);
  await page.getByRole('textbox', { name: /^password/i }).fill(password);
  await page.getByRole('button', { name: /continue/i }).click();

  await page.waitForURL(url => url.href.startsWith(`${CONSOLE_URL}/console`), { timeout: 30_000 });
  await expect(page.locator('.iam-sidebar')).toBeVisible();
});

test('the bootstrap administrator is not asked to enrol a factor', async ({ page }) => {
  // The first administrator of a deployment signs in on a password alone — nothing else can reach
  // the console to configure the SMTP or SMS that would make a factor deliverable. Every
  // administrator after the first is sent through enrolment instead.
  await signIn(page);
  await expect(page).not.toHaveURL(/\/mfa-setup/);
});

test('the console shows a standing reminder while the admin has no factor', async ({ page }) => {
  // It is the only thing left between that one account and a password on its own, so it lives in
  // the shell rather than on a page, and it carries no dismiss control.
  await signIn(page);

  const reminder = page.getByText(/no second factor/i);
  await expect(reminder).toBeVisible();

  // Still there on another page: it belongs to the shell, not to the dashboard. Followed as a
  // link, not as a fresh load — the token lives in memory, so a full reload re-runs the whole
  // OAuth2 round trip, and in dev that round trip asks for the password again: the console
  // (localhost:30501) and the issuer (iam.localhost) are different sites, so Hydra's SameSite=Strict
  // session cookie is not sent. Production puts both under one registrable domain and does not
  // have that problem.
  await page.getByRole('link', { name: /organisations/i }).first().click();
  await expect(reminder).toBeVisible();
});

/**
 * Last on purpose. The per-IP failure counter is deliberately never cleared by a success — a
 * shared budget that one valid account could reset would be a free brute-force lane for every
 * other account behind the same address — so it expires only by TTL. A deliberate failure
 * therefore spends part of the budget the tests above need, and spending it first is what makes
 * them fail with "invalid email or password" for reasons that have nothing to do with them.
 */
test('a wrong password is refused without saying which half was wrong', async ({ page }) => {
  await page.goto(`${CONSOLE_URL}/console/`);
  await page.waitForURL(/\/login/, { timeout: 30_000 });

  await page.getByRole('textbox', { name: /^email/i }).fill('nobody@e2e.test');
  await page.getByRole('textbox', { name: /^password/i }).fill('not-the-password');
  await page.getByRole('button', { name: /continue/i }).click();

  // "Invalid email or password" and nothing narrower: naming which one is an account oracle.
  const error = page.getByText(/invalid email or password/i);
  await expect(error).toBeVisible();
  await expect(page.getByText(/no such (user|account)|unknown email/i)).toHaveCount(0);
  await expect(page).toHaveURL(/\/login/);
});
