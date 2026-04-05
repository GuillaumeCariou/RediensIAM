/**
 * org-email.spec.ts — Org Email (SMTP) configuration page.
 *
 * Route (org admin): /org/email
 * Route (super admin): /system/organisations/:id/email
 *
 * Tests cover: viewing config, editing, saving, testing, deleting.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet } from '../../fixtures/mock-api';

const SMTP_NOT_CONFIGURED = { configured: false };

const SMTP_CONFIGURED = {
  configured: true,
  host: 'smtp.example.com',
  port: 587,
  start_tls: true,
  username: 'relay@example.com',
  from_address: 'noreply@example.com',
  from_name: 'Acme IAM',
  updated_at: '2026-01-15T12:00:00Z',
};

// ── Not configured ────────────────────────────────────────────────────────────

test('shows "not configured" state with configure button', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_NOT_CONFIGURED);

  await page.goto('/admin/org/email');

  await expect(page.getByText(/not configured|no smtp/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /configure|set up smtp/i })).toBeVisible();
});

// ── Configured state ──────────────────────────────────────────────────────────

test('shows SMTP host, port and from address when configured', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_CONFIGURED);

  await page.goto('/admin/org/email');

  await expect(page.getByText('smtp.example.com')).toBeVisible();
  await expect(page.getByText('noreply@example.com')).toBeVisible();
});

// ── Edit form ─────────────────────────────────────────────────────────────────

test('edit button populates form with existing values', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_CONFIGURED);

  await page.goto('/admin/org/email');
  await page.getByRole('button', { name: /edit|configure/i }).click();

  await expect(page.getByLabel(/host/i)).toHaveValue('smtp.example.com');
  await expect(page.getByLabel(/port/i)).toHaveValue('587');
  await expect(page.getByLabel(/from.*address/i)).toHaveValue('noreply@example.com');
  await expect(page.getByLabel(/from.*name/i)).toHaveValue('Acme IAM');
});

test('save SMTP config calls PUT with form values', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_NOT_CONFIGURED);

  let savedBody: unknown;
  await page.route('**/org/smtp', async (route) => {
    if (route.request().method() !== 'PUT') { await route.fallback(); return; }
    savedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(SMTP_CONFIGURED) });
  });

  await page.goto('/admin/org/email');
  await page.getByRole('button', { name: /configure|set up smtp/i }).click();

  await page.getByLabel(/host/i).fill('smtp.example.com');
  await page.getByLabel(/port/i).fill('587');
  await page.getByLabel(/username/i).fill('relay@example.com');
  await page.getByLabel(/password/i).fill('secretpassword');
  await page.getByLabel(/from.*address/i).fill('noreply@example.com');
  await page.getByLabel(/from.*name/i).fill('Acme IAM');

  await page.getByRole('button', { name: /save/i }).click();

  expect((savedBody as Record<string, unknown>).host).toBe('smtp.example.com');
  expect((savedBody as Record<string, unknown>).from_address).toBe('noreply@example.com');
});

// ── SMTP test ─────────────────────────────────────────────────────────────────

test('test button shows success on HTTP 200 from backend', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_CONFIGURED);

  await page.route('**/org/smtp/test', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ok: true, message: 'Test email sent.' }) });
  });

  await page.goto('/admin/org/email');
  await page.getByRole('button', { name: /test/i }).click();

  await expect(page.getByText(/test email sent|success/i)).toBeVisible();
});

test('test button shows failure message on error response', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_CONFIGURED);

  await page.route('**/org/smtp/test', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ok: false, message: 'Connection refused.' }) });
  });

  await page.goto('/admin/org/email');
  await page.getByRole('button', { name: /test/i }).click();

  await expect(page.getByText(/connection refused|failed/i)).toBeVisible();
});

// ── Delete SMTP config ────────────────────────────────────────────────────────

test('delete button removes SMTP config', async ({ adminPage: page }) => {
  await mockGet(page, '/org/smtp', SMTP_CONFIGURED);

  let deleteCalled = false;
  await page.route('**/org/smtp', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deleteCalled = true;
    await route.fulfill({ status: 204 });
  });
  await mockGet(page, '/org/smtp', SMTP_NOT_CONFIGURED);

  await page.goto('/admin/org/email');
  await page.getByRole('button', { name: /delete|remove/i }).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|delete|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(deleteCalled).toBe(true);
});

// ── Super-admin context (/system/organisations/:id/email) ──────────────────────

test('super-admin context calls admin SMTP endpoint', async ({ adminPage: page }) => {
  // Super admin uses /admin/organizations/:id/smtp
  await page.route('**/admin/organizations/org-001/smtp', async (route) => {
    if (route.request().method() !== 'GET') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(SMTP_CONFIGURED) });
  });

  await page.goto('/admin/system/organisations/org-001/email');

  await expect(page.getByText('smtp.example.com')).toBeVisible();
});
