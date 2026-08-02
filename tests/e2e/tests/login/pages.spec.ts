import { test, expect } from '@playwright/test';

/**
 * The login SPA's standalone pages — the ones a user reaches from a mail, a link or a redirect
 * rather than from the sign-in form.
 *
 * Several of them exist only because a redirect target must be a page: `hydra.urls.logout` pointed
 * at `/auth/logout`, a controller answering JSON, so a sign-out landed the browser on a raw
 * `{"logout_challenge":"…"}` body that nothing ever accepted — the session outlived the sign-out
 * that appeared to have happened. `urls.error` was not set at all, so an OAuth2 failure rendered
 * Hydra's own "configuration key urls.error is not set" page.
 */

test('the login page renders for an anonymous visitor', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByRole('textbox', { name: /^email/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /continue/i })).toBeVisible();
});

test('registration is reachable and asks for what it needs', async ({ page }) => {
  await page.goto('/register');
  await expect(page.getByRole('textbox').first()).toBeVisible();
});

test('the password-reset page asks for an address', async ({ page }) => {
  await page.goto('/password-reset');
  await expect(page.getByRole('textbox').first()).toBeVisible();
});

test('the OAuth2 error page is ours, not Hydra\'s default', async ({ page }) => {
  await page.goto('/auth/oauth2/error?error=invalid_request&error_description=test');

  // Hydra's own page says so in as many words when urls.error is unset. Ours must not be it.
  await expect(page.getByText(/configuration key urls\.error/i)).toHaveCount(0);
  await expect(page.locator('body')).not.toBeEmpty();
});

test('the logout page says so when there is no challenge to complete', async ({ page }) => {
  // Opened without a challenge — a link someone kept, or a second click. It must explain itself
  // rather than sit on a spinner or claim a sign-out that never happened.
  await page.goto('/logout');
  await expect(page.getByRole('alert')).toBeVisible();
});

test('a route the SPA does not know falls back to the login page', async ({ page }) => {
  await page.goto('/no-such-page');
  await expect(page).toHaveURL(/\/login/);
});
