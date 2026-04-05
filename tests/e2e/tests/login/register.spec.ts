/**
 * register.spec.ts — Registration flow tests.
 *
 * The registration page is reached via /register?login_challenge=...
 * Steps: fill form → (optionally) verify OTP → redirect_to
 */
import { test, expect } from '@playwright/test';

const CHALLENGE_ID = 'test-challenge-register-001';

const CHALLENGE_PAYLOAD = {
  project_id: 'test-project-id',
  allow_self_registration: true,
  email_verification_enabled: true,
  sms_verification_enabled: false,
  is_admin_login: false,
  theme: {},
};

test.beforeEach(async ({ page }) => {
  await page.route(`**/auth/login?login_challenge=${CHALLENGE_ID}`, async (route) => {
    if (route.request().method() !== 'GET') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(CHALLENGE_PAYLOAD) });
  });
});

// ── Form rendering ────────────────────────────────────────────────────────────

test('renders registration form fields', async ({ page }) => {
  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  await expect(page.locator('input[type="email"]')).toBeVisible();
  await expect(page.locator('input[type="password"]').first()).toBeVisible();
  await expect(page.getByRole('button', { name: /create account|register/i })).toBeVisible();
});

// ── Validation ────────────────────────────────────────────────────────────────

test('shows error when passwords do not match', async ({ page }) => {
  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);

  // Fill email
  await page.locator('input[type="email"]').fill('new@example.com');
  // Fill mismatched passwords
  const pwdFields = page.locator('input[type="password"]');
  await pwdFields.nth(0).fill('MyPassword1!');
  await pwdFields.nth(1).fill('DifferentPassword1!');
  await page.getByRole('button', { name: /create account|register/i }).click();

  await expect(page.getByText(/passwords do not match/i)).toBeVisible();
});

// ── Breached password ─────────────────────────────────────────────────────────

test('shows breach error when password is in breach database', async ({ page }) => {
  await page.route('**/auth/register', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'password_breached', count: 12345 }),
    });
  });

  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  const pwdFields = page.locator('input[type="password"]');
  await page.locator('input[type="email"]').fill('new@example.com');
  await pwdFields.nth(0).fill('password123');
  await pwdFields.nth(1).fill('password123');
  await page.getByRole('button', { name: /create account|register/i }).click();

  await expect(page.getByText(/12,345 data breaches/i)).toBeVisible();
});

// ── Email verification step ───────────────────────────────────────────────────

test('shows OTP verification step after successful registration', async ({ page }) => {
  await page.route('**/auth/register', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ requires_verification: true, session_id: 'sess-abc-123' }),
    });
  });

  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  const pwdFields = page.locator('input[type="password"]');
  await page.locator('input[type="email"]').fill('new@example.com');
  await pwdFields.nth(0).fill('StrongP@ss1!');
  await pwdFields.nth(1).fill('StrongP@ss1!');
  await page.getByRole('button', { name: /create account|register/i }).click();

  // Should show OTP input
  await expect(page.getByText(/verification|code/i)).toBeVisible();
  await expect(page.locator('input[inputmode="numeric"], input[type="text"]')).toBeVisible();
});

test('shows error on wrong OTP code', async ({ page }) => {
  await page.route('**/auth/register', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ requires_verification: true, session_id: 'sess-abc-123' }),
    });
  });

  await page.route('**/auth/register/verify', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'invalid_code' }),
    });
  });

  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  const pwdFields = page.locator('input[type="password"]');
  await page.locator('input[type="email"]').fill('new@example.com');
  await pwdFields.nth(0).fill('StrongP@ss1!');
  await pwdFields.nth(1).fill('StrongP@ss1!');
  await page.getByRole('button', { name: /create account|register/i }).click();

  // Enter wrong OTP
  const codeInput = page.locator('input[inputmode="numeric"], input[type="text"]').first();
  await codeInput.fill('000000');
  await page.getByRole('button', { name: /verify|confirm|submit/i }).click();

  await expect(page.getByText(/invalid or expired/i)).toBeVisible();
});

// ── A3: Rate limit (429) ──────────────────────────────────────────────────────

test('shows rate-limit message on 429 from register', async ({ page }) => {
  await page.route('**/auth/register', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ status: 429, contentType: 'application/json', body: JSON.stringify({ error: 'rate_limited' }) });
  });

  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  const pwdFields = page.locator('input[type="password"]');
  await page.locator('input[type="email"]').fill('new@example.com');
  await pwdFields.nth(0).fill('StrongP@ss1!');
  await pwdFields.nth(1).fill('StrongP@ss1!');
  await page.getByRole('button', { name: /create account|register/i }).click();

  await expect(page.getByText(/too many attempts.*try again later/i)).toBeVisible();
});

// ── Successful registration without verification ───────────────────────────────

test('follows redirect_to on registration without verification required', async ({ page }) => {
  await page.route('**/auth/register', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ redirect_to: 'http://localhost/admin/?code=abc' }),
    });
  });

  let navigatedTo = '';
  page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

  await page.goto(`/register?login_challenge=${CHALLENGE_ID}`);
  const pwdFields = page.locator('input[type="password"]');
  await page.locator('input[type="email"]').fill('new@example.com');
  await pwdFields.nth(0).fill('StrongP@ss1!');
  await pwdFields.nth(1).fill('StrongP@ss1!');
  await page.getByRole('button', { name: /create account|register/i }).click();

  await page.waitForTimeout(500);
  expect(navigatedTo).toContain('/admin/');
});
