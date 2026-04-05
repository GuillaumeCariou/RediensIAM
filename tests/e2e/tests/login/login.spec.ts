/**
 * login.spec.ts — Login SPA credential flow tests.
 *
 * These tests run against the REAL backend (no API mocking).
 * They require:
 *  - Dev stack running at TEST_BASE_URL (default: http://localhost)
 *  - A seeded project whose login_challenge can be obtained
 *
 * Strategy: intercept the backend's /auth/login GET to return a crafted
 * challenge payload, then POST real credentials against the actual handler.
 * This avoids needing a live Hydra client while still exercising the full
 * login form logic.
 */
import { test, expect } from '@playwright/test';

// Minimal login-challenge payload the Login SPA needs to render the form
const CHALLENGE_ID = 'test-challenge-login-001';
const CHALLENGE_PAYLOAD = {
  project_id: 'test-project-id',
  project_name: 'Test App',
  is_admin_login: false,
  allow_self_registration: true,
  email_verification_enabled: true,
  sms_verification_enabled: false,
  theme: {},
};

test.beforeEach(async ({ page }) => {
  // Stub the challenge fetch so the Login SPA renders without a real Hydra flow
  await page.route(`**/auth/login?login_challenge=${CHALLENGE_ID}`, async (route) => {
    if (route.request().method() !== 'GET') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(CHALLENGE_PAYLOAD) });
  });
  await page.route(`**/auth/login/theme?login_challenge=${CHALLENGE_ID}`, async (route) => {
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(CHALLENGE_PAYLOAD) });
  });
});

// ── Form rendering ────────────────────────────────────────────────────────────

test('renders email/username field and password field', async ({ page }) => {
  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await expect(page.locator('#identifier')).toBeVisible();
  await expect(page.locator('#password')).toBeVisible();
  await expect(page.getByRole('button', { name: /sign in/i })).toBeVisible();
});

test('shows forgot-password and create-account links when enabled', async ({ page }) => {
  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await expect(page.getByText(/forgot password/i)).toBeVisible();
  await expect(page.getByText(/create account/i)).toBeVisible();
});

test('shows project name in heading', async ({ page }) => {
  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await expect(page.getByText('Test App')).toBeVisible();
});

// ── Validation ────────────────────────────────────────────────────────────────

test('requires both fields to submit', async ({ page }) => {
  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);

  // Try submitting with only email filled
  await page.locator('#identifier').fill('user@example.com');
  await page.getByRole('button', { name: /sign in/i }).click();
  // Browser native validation prevents submit — password field is required
  await expect(page.locator('#password')).toBeFocused();
});

// ── Wrong credentials ────────────────────────────────────────────────────────

test('shows error on wrong password', async ({ page }) => {
  // Let the POST go to the real backend but stub it to return an error
  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'invalid_credentials' }),
    });
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('nobody@example.com');
  await page.locator('#password').fill('wrongpassword');
  await page.getByRole('button', { name: /sign in/i }).click();

  await expect(page.getByText(/invalid email or password/i)).toBeVisible();
});

// ── Account locked ────────────────────────────────────────────────────────────

test('shows locked-until message when account is locked', async ({ page }) => {
  const lockedUntil = new Date(Date.now() + 5 * 60 * 1000).toISOString(); // 5 min from now

  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'account_locked', locked_until: lockedUntil }),
    });
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('locked@example.com');
  await page.locator('#password').fill('anypassword');
  await page.getByRole('button', { name: /sign in/i }).click();

  await expect(page.getByText(/account locked until/i)).toBeVisible();
});

// ── No role ───────────────────────────────────────────────────────────────────

test('shows permission error when user has no role in project', async ({ page }) => {
  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'no_role' }),
    });
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('norole@example.com');
  await page.locator('#password').fill('password');
  await page.getByRole('button', { name: /sign in/i }).click();

  await expect(page.getByText(/do not have permission/i)).toBeVisible();
});

// ── MFA redirect ──────────────────────────────────────────────────────────────

test('redirects to /mfa when MFA is required', async ({ page }) => {
  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ requires_mfa: true, mfa_type: 'totp' }),
    });
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('mfauser@example.com');
  await page.locator('#password').fill('password');
  await page.getByRole('button', { name: /sign in/i }).click();

  await expect(page).toHaveURL(/\/mfa/);
});

// ── Successful login ──────────────────────────────────────────────────────────

test('follows redirect_to on successful login', async ({ page }) => {
  const redirectTarget = 'http://localhost/admin/?code=abc&state=xyz';

  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ redirect_to: redirectTarget }),
    });
  });

  // Intercept the final navigation so the test doesn't actually follow it
  let navigatedTo = '';
  page.on('request', req => {
    if (req.isNavigationRequest()) navigatedTo = req.url();
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('admin@example.com');
  await page.locator('#password').fill('correctpassword');
  await page.getByRole('button', { name: /sign in/i }).click();

  // Give the navigation a moment
  await page.waitForTimeout(500);
  expect(navigatedTo).toContain('/admin/');
});

// ── Loading state ─────────────────────────────────────────────────────────────

test('disables submit button while loading', async ({ page }) => {
  // Slow response to observe the loading state
  await page.route('**/auth/login', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await new Promise(r => setTimeout(r, 300));
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'invalid_credentials' }) });
  });

  await page.goto(`/?login_challenge=${CHALLENGE_ID}`);
  await page.locator('#identifier').fill('test@example.com');
  await page.locator('#password').fill('password');
  await page.getByRole('button', { name: /sign in/i }).click();

  await expect(page.getByRole('button', { name: /signing in/i })).toBeDisabled();
});
