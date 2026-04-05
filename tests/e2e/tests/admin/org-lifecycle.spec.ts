/**
 * org-lifecycle.spec.ts — Admin SPA › System › Organisations CRUD + suspend/unsuspend.
 * All API calls are mocked.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPost, mockPatch, mockDelete } from '../../fixtures/mock-api';

const ORGS = [
  { id: 'org-001', name: 'Acme Corp', slug: 'acme', suspended: false, user_count: 42, project_count: 3, created_at: '2025-01-01T00:00:00Z' },
  { id: 'org-002', name: 'Suspended Inc', slug: 'suspended', suspended: true, user_count: 5, project_count: 1, created_at: '2025-06-01T00:00:00Z' },
];

// ── List ──────────────────────────────────────────────────────────────────────

test('lists organisations', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  await page.goto('/admin/system/organisations');

  await expect(page.getByText('Acme Corp')).toBeVisible();
  await expect(page.getByText('Suspended Inc')).toBeVisible();
});

test('shows Suspended badge for suspended orgs', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  await page.goto('/admin/system/organisations');

  await expect(page.getByText('Suspended')).toBeVisible();
});

// ── Create ────────────────────────────────────────────────────────────────────

test('create dialog opens and submits', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  const newOrg = { id: 'org-003', name: 'New Org', slug: 'new-org', suspended: false, user_count: 0, project_count: 0, created_at: new Date().toISOString() };
  let postedBody: unknown;
  await page.route('**/admin/organizations', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    postedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(newOrg) });
  });

  // Reload mock to include new org after create
  await mockGet(page, '/admin/organizations', { organizations: [...ORGS, newOrg] });

  await page.goto('/admin/system/organisations');
  await page.getByRole('button', { name: /new org|create/i }).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByLabel(/name/i).fill('New Org');
  await page.getByLabel(/slug/i).fill('new-org');
  await page.getByRole('button', { name: /create/i }).last().click();

  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect((postedBody as Record<string, unknown>).name).toBe('New Org');
  expect((postedBody as Record<string, unknown>).slug).toBe('new-org');
});

// ── Suspend / unsuspend ───────────────────────────────────────────────────────

test('suspend org calls suspend endpoint', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  let suspendCalled = false;
  await page.route('**/admin/organizations/org-001/suspend', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    suspendCalled = true;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ...ORGS[0], suspended: true }) });
  });

  await page.goto('/admin/system/organisations');
  // Find the row for Acme Corp and trigger suspend via dropdown / button
  const row = page.getByRole('row').filter({ hasText: 'Acme Corp' });
  await row.getByRole('button').click();
  await page.getByText(/suspend/i).click();

  // Confirm dialog if present
  const confirmBtn = page.getByRole('button', { name: /confirm|yes|suspend/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(suspendCalled).toBe(true);
});

test('unsuspend org calls unsuspend endpoint', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  let unsuspendCalled = false;
  await page.route('**/admin/organizations/org-002/unsuspend', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    unsuspendCalled = true;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ...ORGS[1], suspended: false }) });
  });

  await page.goto('/admin/system/organisations');
  const row = page.getByRole('row').filter({ hasText: 'Suspended Inc' });
  await row.getByRole('button').click();
  await page.getByText(/unsuspend/i).click();

  expect(unsuspendCalled).toBe(true);
});

// ── Delete ────────────────────────────────────────────────────────────────────

test('delete org calls DELETE endpoint', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });

  let deleteCalled = false;
  await page.route('**/admin/organizations/org-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deleteCalled = true;
    await route.fulfill({ status: 204 });
  });
  await mockGet(page, '/admin/organizations', { organizations: [ORGS[1]] });

  await page.goto('/admin/system/organisations');
  const row = page.getByRole('row').filter({ hasText: 'Acme Corp' });
  await row.getByRole('button').click();
  await page.getByText(/delete/i).click();

  // Confirm if there's a confirmation dialog
  const confirmBtn = page.getByRole('button', { name: /confirm|delete|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(deleteCalled).toBe(true);
});

// ── Navigate to org detail ────────────────────────────────────────────────────

test('clicking org row navigates to org detail', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/organizations', { organizations: ORGS });
  await mockGet(page, '/admin/organizations/org-001', ORGS[0]);

  await page.goto('/admin/system/organisations');
  await page.getByText('Acme Corp').click();

  await expect(page).toHaveURL(/\/system\/organisations\/org-001/);
});
