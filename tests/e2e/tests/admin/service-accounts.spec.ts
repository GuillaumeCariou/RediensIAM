/**
 * service-accounts.spec.ts — Admin SPA › Service Accounts (system-wide).
 * All API calls are mocked.
 *
 * Covers: list, create, delete, PAT generation & revocation, API key management.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPost, mockDelete } from '../../fixtures/mock-api';

const SERVICE_ACCOUNTS = [
  { id: 'sa-001', name: 'CI Runner', description: 'Deployment automation', active: true, last_used_at: '2026-03-01T10:00:00Z', created_at: '2025-01-01T00:00:00Z', pats: [], roles: [] },
  { id: 'sa-002', name: 'Monitor Bot', description: null, active: false, last_used_at: null, created_at: '2025-06-01T00:00:00Z', pats: [], roles: [] },
];

const SA_DETAIL = {
  id: 'sa-001',
  name: 'CI Runner',
  description: 'Deployment automation',
  active: true,
  last_used_at: '2026-03-01T10:00:00Z',
  created_at: '2025-01-01T00:00:00Z',
  pats: [
    { id: 'pat-001', name: 'Deploy token', expires_at: null, last_used_at: '2026-03-01T10:00:00Z', created_at: '2025-01-01T00:00:00Z' },
    { id: 'pat-002', name: 'Staging token', expires_at: '2027-01-01T00:00:00Z', last_used_at: null, created_at: '2025-06-01T00:00:00Z' },
  ],
  roles: [
    { id: 'role-001', role: 'org_admin', org_id: 'org-001', project_id: null, granted_at: '2025-01-01T00:00:00Z' },
  ],
};

const API_KEY_INFO = { client_id: null, has_key: false, kid: null };

// ── List ──────────────────────────────────────────────────────────────────────

test('lists service accounts', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts', { service_accounts: SERVICE_ACCOUNTS });

  await page.goto('/admin/system/service-accounts');

  await expect(page.getByText('CI Runner')).toBeVisible();
  await expect(page.getByText('Monitor Bot')).toBeVisible();
});

test('shows active/inactive status', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts', { service_accounts: SERVICE_ACCOUNTS });

  await page.goto('/admin/system/service-accounts');

  await expect(page.getByText(/active/i).first()).toBeVisible();
});

// ── Navigate to detail ────────────────────────────────────────────────────────

test('clicking SA navigates to detail page', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts', { service_accounts: SERVICE_ACCOUNTS });
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  await page.goto('/admin/system/service-accounts');
  await page.getByText('CI Runner').click();

  await expect(page).toHaveURL(/service-accounts\/sa-001/);
});

// ── PAT generation ────────────────────────────────────────────────────────────

test('detail page lists existing PATs', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  await page.goto('/admin/system/service-accounts/sa-001');

  await expect(page.getByText('Deploy token')).toBeVisible();
  await expect(page.getByText('Staging token')).toBeVisible();
});

test('generate PAT shows token value once', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  const newToken = 'pat_abc123xyz789secretvalue';
  await page.route('**/service-accounts/sa-001/pat', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ id: 'pat-003', name: 'New Token', token: newToken, expires_at: null, created_at: new Date().toISOString() }),
    });
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  await page.getByRole('button', { name: /generate.*token|new.*pat|add.*token/i }).click();

  // Fill name in dialog
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/name/i).fill('New Token');
  await dialog.getByRole('button', { name: /generate|create/i }).click();

  // Token must be displayed
  await expect(page.getByText(newToken)).toBeVisible();
});

test('revoke PAT calls DELETE endpoint', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  let revokeCalled = false;
  await page.route('**/service-accounts/sa-001/pat/pat-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    revokeCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  // Find Deploy token row and click revoke
  const row = page.getByRole('row').filter({ hasText: 'Deploy token' });
  await row.getByRole('button').click();
  await page.getByText(/revoke/i).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|revoke|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(revokeCalled).toBe(true);
});

// ── API key (JWK) ─────────────────────────────────────────────────────────────

test('generate API key button triggers key generation and shows download', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  let addKeyCalled = false;
  await page.route('**/service-accounts/sa-001/api-keys', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    addKeyCalled = true;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ client_id: 'sa-001-client', kid: `sa-001-${Date.now()}` }) });
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  await page.getByRole('button', { name: /generate.*key|create.*key|api key/i }).click();

  // Wait for async key generation (crypto.subtle.generateKey takes a moment)
  await page.waitForTimeout(3_000);
  // Download should be triggered — we can't test file download directly
  // but we verify the API was called
  expect(addKeyCalled).toBe(true);
});

test('shows existing API key info when key is configured', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', {
    client_id: 'sa-001-client',
    has_key: true,
    kid: 'sa-001-1234567890',
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  await expect(page.getByText(/sa-001-client/)).toBeVisible();
});

test('remove API key calls DELETE', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', {
    client_id: 'sa-001-client', has_key: true, kid: 'sa-001-1234567890',
  });

  let removeCalled = false;
  await page.route('**/service-accounts/sa-001/api-keys', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    removeCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  await page.getByRole('button', { name: /remove.*key|delete.*key/i }).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|remove|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(removeCalled).toBe(true);
});

// ── Delete SA ─────────────────────────────────────────────────────────────────

test('delete SA calls DELETE and navigates back', async ({ adminPage: page }) => {
  await mockGet(page, '/service-accounts/sa-001', SA_DETAIL);
  await mockGet(page, '/service-accounts/sa-001/api-keys', API_KEY_INFO);

  let deleteCalled = false;
  await page.route('**/service-accounts/sa-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deleteCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/system/service-accounts/sa-001');

  await page.getByRole('button', { name: /delete service account/i }).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|delete|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(deleteCalled).toBe(true);
});
