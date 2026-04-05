/**
 * password-reset.spec.ts — Three-step password reset flow.
 *
 * Step 1: Enter email → request OTP
 * Step 2: Enter OTP → receive reset_token
 * Step 3: Enter new password → done
 *
 * All backend calls are mocked so no real email/SMS is needed.
 */
import { test, expect } from '@playwright/test';

const PROJECT_ID = 'test-project-id';

// ── Step 1: email form ────────────────────────────────────────────────────────

test('renders email input on the first step', async ({ page }) => {
  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await expect(page.locator('input[type="email"]')).toBeVisible();
  await expect(page.getByRole('button', { name: /send|reset/i })).toBeVisible();
});

test('shows error when reset is not available for project', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'not_configured' }),
    });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();

  await expect(page.getByText(/not available for this project/i)).toBeVisible();
});

test('advances to OTP step after requesting reset', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ session_id: 'reset-sess-001' }),
    });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();

  // Should show OTP input now
  await expect(page.locator('input')).toBeVisible();
  await expect(page.getByRole('button', { name: /verify|confirm/i })).toBeVisible();
});

// ── A3: Rate limit (429) ──────────────────────────────────────────────────────

test('shows rate-limit message on 429 from request endpoint', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ status: 429, contentType: 'application/json', body: JSON.stringify({ error: 'rate_limited' }) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();

  await expect(page.getByText(/too many attempts.*try again later/i)).toBeVisible();
});

// ── Step 2: OTP verification ──────────────────────────────────────────────────

test('shows error on invalid OTP', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ session_id: 'reset-sess-001' }) });
  });
  await page.route('**/auth/password-reset/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'invalid_code' }) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();

  await page.locator('input').first().fill('000000');
  await page.getByRole('button', { name: /verify|confirm/i }).click();

  await expect(page.getByText(/invalid or expired/i)).toBeVisible();
});

test('advances to new-password step after valid OTP', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ session_id: 'reset-sess-001' }) });
  });
  await page.route('**/auth/password-reset/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ reset_token: 'tok-abc-xyz' }) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();

  await page.locator('input').first().fill('123456');
  await page.getByRole('button', { name: /verify|confirm/i }).click();

  // Should now show password fields
  await expect(page.locator('input[type="password"]').first()).toBeVisible();
});

// ── Step 3: new password ──────────────────────────────────────────────────────

test('shows error when new passwords do not match', async ({ page }) => {
  // Drive to the password step
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ session_id: 'reset-sess-001' }) });
  });
  await page.route('**/auth/password-reset/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ reset_token: 'tok-abc-xyz' }) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();
  await page.locator('input').first().fill('123456');
  await page.getByRole('button', { name: /verify|confirm/i }).click();

  // Fill mismatched passwords
  const pwds = page.locator('input[type="password"]');
  await pwds.nth(0).fill('NewPassword1!');
  await pwds.nth(1).fill('DifferentPassword1!');
  await page.getByRole('button', { name: /set password|update|save/i }).click();

  await expect(page.getByText(/passwords do not match/i)).toBeVisible();
});

test('shows breach error on new password that is in breach database', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ session_id: 'reset-sess-001' }) });
  });
  await page.route('**/auth/password-reset/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ reset_token: 'tok-abc-xyz' }) });
  });
  await page.route('**/auth/password-reset/confirm', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'password_breached', count: 99999 }) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();
  await page.locator('input').first().fill('123456');
  await page.getByRole('button', { name: /verify|confirm/i }).click();

  const pwds = page.locator('input[type="password"]');
  await pwds.nth(0).fill('password123');
  await pwds.nth(1).fill('password123');
  await page.getByRole('button', { name: /set password|update|save/i }).click();

  await expect(page.getByText(/99,999 data breaches/i)).toBeVisible();
});

test('shows success state after password is reset', async ({ page }) => {
  await page.route('**/auth/password-reset/request', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ session_id: 'reset-sess-001' }) });
  });
  await page.route('**/auth/password-reset/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ reset_token: 'tok-abc-xyz' }) });
  });
  await page.route('**/auth/password-reset/confirm', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({}) });
  });

  await page.goto(`/password-reset?project_id=${PROJECT_ID}`);
  await page.locator('input[type="email"]').fill('user@example.com');
  await page.getByRole('button', { name: /send|reset/i }).click();
  await page.locator('input').first().fill('123456');
  await page.getByRole('button', { name: /verify|confirm/i }).click();

  const pwds = page.locator('input[type="password"]');
  await pwds.nth(0).fill('NewSecurePass1!');
  await pwds.nth(1).fill('NewSecurePass1!');
  await page.getByRole('button', { name: /set password|update|save/i }).click();

  await expect(page.getByText(/password updated/i)).toBeVisible();
  await expect(page.getByText(/sign in/i)).toBeVisible();
});
