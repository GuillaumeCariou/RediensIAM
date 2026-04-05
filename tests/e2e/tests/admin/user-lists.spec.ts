/**
 * user-lists.spec.ts — Admin SPA › System › User Lists (global view)
 *                    — and Org context user list view.
 * All API calls are mocked.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPost } from '../../fixtures/mock-api';

// ── System-global view (/system/userlists) ────────────────────────────────────

const SYSTEM_LISTS = [
  { id: 'list-001', name: 'Acme Employees', org_id: 'org-001', org_name: 'Acme Corp', immovable: false, created_at: '2025-01-01T00:00:00Z' },
  { id: 'list-002', name: 'System Admins', org_id: null, org_name: null, immovable: true, created_at: '2025-01-01T00:00:00Z' },
];

test('global view shows Organisation column (not Users column)', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await expect(page.getByRole('columnheader', { name: /organisation/i })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: /users/i })).not.toBeVisible();
});

test('global view shows search bar', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await expect(page.getByPlaceholder(/search by name or org/i)).toBeVisible();
});

test('global view hides create button', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await expect(page.getByRole('button', { name: /new user list/i })).not.toBeVisible();
});

test('global view lists all user lists with org name', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await expect(page.getByText('Acme Employees')).toBeVisible();
  await expect(page.getByText('Acme Corp')).toBeVisible();
  await expect(page.getByText('System Admins')).toBeVisible();
  await expect(page.getByText('System (root)')).toBeVisible();
});

test('global view shows Immovable badge for immovable lists', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await expect(page.getByText('Immovable')).toBeVisible();
  await expect(page.getByText('Movable')).toBeVisible();
});

test('global view search filters by name', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await page.getByPlaceholder(/search by name or org/i).fill('System');

  await expect(page.getByText('System Admins')).toBeVisible();
  await expect(page.getByText('Acme Employees')).not.toBeVisible();
});

test('global view search filters by org name', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });

  await page.goto('/admin/system/userlists');

  await page.getByPlaceholder(/search by name or org/i).fill('Acme');

  await expect(page.getByText('Acme Employees')).toBeVisible();
  await expect(page.getByText('System Admins')).not.toBeVisible();
});

test('global view navigates to list detail on row click', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: SYSTEM_LISTS });
  await mockGet(page, '/admin/userlists/list-001', SYSTEM_LISTS[0]);
  await mockGet(page, /\/admin\/userlists\/list-001\/users/, { users: [] });

  await page.goto('/admin/system/userlists');
  await page.getByText('Acme Employees').click();

  await expect(page).toHaveURL(/\/system\/userlists\/list-001/);
});

// ── Org-context view (/system/organisations/:id/userlists) ───────────────────

const ORG_LISTS = [
  { id: 'list-001', name: 'Acme Employees', org_id: 'org-001', org_name: 'Acme Corp', immovable: false, user_count: 10, created_at: '2025-01-01T00:00:00Z' },
  { id: 'list-003', name: 'Managers', org_id: 'org-001', org_name: 'Acme Corp', immovable: false, user_count: 3, created_at: '2025-02-01T00:00:00Z' },
];

test('org-context view shows Users column (not Organisation column)', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  await page.goto('/admin/system/organisations/org-001/userlists');

  await expect(page.getByRole('columnheader', { name: /users/i })).toBeVisible();
  await expect(page.getByRole('columnheader', { name: /organisation/i })).not.toBeVisible();
});

test('org-context view shows create button', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  await page.goto('/admin/system/organisations/org-001/userlists');

  await expect(page.getByRole('button', { name: /new user list/i })).toBeVisible();
});

test('org-context view hides search bar', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  await page.goto('/admin/system/organisations/org-001/userlists');

  await expect(page.getByPlaceholder(/search/i)).not.toBeVisible();
});

test('org-context view shows user counts', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  await page.goto('/admin/system/organisations/org-001/userlists');

  await expect(page.getByText('10')).toBeVisible();
  await expect(page.getByText('3')).toBeVisible();
});

test('org-context create dialog posts correct org_id', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  let postedBody: unknown;
  await page.route('**/org/userlists', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    postedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ id: 'list-new', name: 'Beta Team', org_id: 'org-001', immovable: false, user_count: 0, created_at: new Date().toISOString() }) });
  });
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });

  await page.goto('/admin/system/organisations/org-001/userlists');
  await page.getByRole('button', { name: /new user list/i }).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByLabel(/name/i).fill('Beta Team');
  await page.getByRole('button', { name: /^create$/i }).click();

  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect((postedBody as Record<string, unknown>).name).toBe('Beta Team');
  expect((postedBody as Record<string, unknown>).org_id).toBe('org-001');
});

test('org-context navigates to list detail with correct org path', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/userlists\?org_id=org-001/, { user_lists: ORG_LISTS });
  await mockGet(page, '/admin/userlists/list-001', ORG_LISTS[0]);
  await mockGet(page, /\/org\/userlists\/list-001\/users/, { users: [] });
  await mockGet(page, /\/org\/userlists\/list-001$/, ORG_LISTS[0]);

  await page.goto('/admin/system/organisations/org-001/userlists');
  await page.getByText('Acme Employees').click();

  await expect(page).toHaveURL(/\/system\/organisations\/org-001\/userlists\/list-001/);
});

// ── Empty state ───────────────────────────────────────────────────────────────

test('shows empty state when no lists exist', async ({ adminPage: page }) => {
  await mockGet(page, '/admin/userlists', { user_lists: [] });

  await page.goto('/admin/system/userlists');

  await expect(page.getByText(/no user lists found/i)).toBeVisible();
});
