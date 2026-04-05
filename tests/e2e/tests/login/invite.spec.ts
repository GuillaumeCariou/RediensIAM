/**
 * invite.spec.ts — Invite completion (set-password) flow.
 *
 * URL: /set-password?token=TOKEN&project_id=PID
 * The page calls completeInvite(token, password) on submit.
 */
import { test, expect } from '@playwright/test';

const VALID_TOKEN = 'invite-token-abc123';
const PROJECT_ID  = 'test-project-id';
const BASE_URL    = `/set-password?token=${VALID_TOKEN}&project_id=${PROJECT_ID}`;

test.beforeEach(async ({ page }) => {
  // Silence the theme fetch — irrelevant for these tests
  await page.route('**/auth/login/theme**', async (route) => {
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({}) });
  });
  await page.route(`**/org/projects/${PROJECT_ID}/theme`, async (route) => {
    await route.fulfill({ contentType: 'application/json', body: '{}' });
  });
  // Catch-all theme fetches
  await page.route(/\/theme/, async (route) => {
    await route.fulfill({ contentType: 'application/json', body: '{}' });
  });
});

// ── Missing token ─────────────────────────────────────────────────────────────

test('shows invalid-link message when no token in URL', async ({ page }) => {
  await page.goto('/set-password');
  await expect(page.getByText(/invalid link/i)).toBeVisible();
});

// ── Form rendering ────────────────────────────────────────────────────────────

test('renders set-password form', async ({ page }) => {
  await page.goto(BASE_URL);
  await expect(page.locator('#password')).toBeVisible();
  await expect(page.locator('#confirm')).toBeVisible();
  await expect(page.getByRole('button', { name: /set password/i })).toBeVisible();
});

// ── Client-side validation ────────────────────────────────────────────────────

test('shows error when passwords do not match', async ({ page }) => {
  await page.goto(BASE_URL);
  await page.locator('#password').fill('StrongP@ss1!');
  await page.locator('#confirm').fill('DifferentP@ss1!');
  await page.getByRole('button', { name: /set password/i }).click();
  await expect(page.getByText(/passwords do not match/i)).toBeVisible();
});

// ── Breach check ──────────────────────────────────────────────────────────────

test('shows breach error for known-bad password', async ({ page }) => {
  await page.route('**/auth/invite/complete', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'password_breached', count: 55000 }),
    });
  });

  await page.goto(BASE_URL);
  await page.locator('#password').fill('password123');
  await page.locator('#confirm').fill('password123');
  await page.getByRole('button', { name: /set password/i }).click();

  await expect(page.getByText(/55,000 data breaches/i)).toBeVisible();
});

// ── Expired token ─────────────────────────────────────────────────────────────

test('shows expired-link error when token is expired', async ({ page }) => {
  await page.route('**/auth/invite/complete', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'token_expired' }),
    });
  });

  await page.goto(BASE_URL);
  await page.locator('#password').fill('StrongP@ss1!');
  await page.locator('#confirm').fill('StrongP@ss1!');
  await page.getByRole('button', { name: /set password/i }).click();

  await expect(page.getByText(/expired/i)).toBeVisible();
});

// ── Password policy ───────────────────────────────────────────────────────────

test('shows policy detail when password fails project policy', async ({ page }) => {
  await page.route('**/auth/invite/complete', async (route) => {
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ error: 'password_policy', detail: 'Must contain at least one symbol.' }),
    });
  });

  await page.goto(BASE_URL);
  await page.locator('#password').fill('NoSymbol123');
  await page.locator('#confirm').fill('NoSymbol123');
  await page.getByRole('button', { name: /set password/i }).click();

  await expect(page.getByText(/must contain at least one symbol/i)).toBeVisible();
});

// ── Success ───────────────────────────────────────────────────────────────────

test('shows success state and sign-in link after password set', async ({ page }) => {
  await page.route('**/auth/invite/complete', async (route) => {
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({}) });
  });

  await page.goto(BASE_URL);
  await page.locator('#password').fill('StrongP@ss1!');
  await page.locator('#confirm').fill('StrongP@ss1!');
  await page.getByRole('button', { name: /set password/i }).click();

  await expect(page.getByText(/password set/i)).toBeVisible();
  await expect(page.getByText(/account is ready/i)).toBeVisible();
  await expect(page.getByRole('link', { name: /sign in/i })).toBeVisible();
});

// ── Loading state ─────────────────────────────────────────────────────────────

test('disables submit while saving', async ({ page }) => {
  await page.route('**/auth/invite/complete', async (route) => {
    await new Promise(r => setTimeout(r, 300));
    await route.fulfill({ contentType: 'application/json', body: '{}' });
  });

  await page.goto(BASE_URL);
  await page.locator('#password').fill('StrongP@ss1!');
  await page.locator('#confirm').fill('StrongP@ss1!');
  await page.getByRole('button', { name: /set password/i }).click();

  await expect(page.getByRole('button', { name: /setting password/i })).toBeDisabled();
});
