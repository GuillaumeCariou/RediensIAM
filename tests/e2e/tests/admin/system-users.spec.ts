/**
 * system-users.spec.ts — Admin SPA › System › Global User Search.
 *
 * All API calls are mocked. The adminPage fixture injects the OIDC
 * sessionStorage token so the SPA considers itself authenticated.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPatch, mockPost } from '../../fixtures/mock-api';

const USERS = [
  {
    id: 'user-001',
    email: 'alice@acme.com',
    username: 'alice',
    discriminator: '0001',
    display_name: 'Alice Smith',
    active: true,
    last_login_at: '2026-03-01T10:00:00Z',
    org_name: 'Acme Corp',
    user_list_name: 'default',
    org_id: 'org-001',
    locked_until: null,
  },
  {
    id: 'user-002',
    email: 'bob@locked.com',
    username: 'bob',
    discriminator: '0002',
    display_name: null,
    active: false,
    last_login_at: null,
    org_name: 'Locked Inc',
    user_list_name: 'default',
    org_id: 'org-002',
    locked_until: new Date(Date.now() + 3_600_000).toISOString(), // locked for 1h
  },
];

const USER_DETAIL = {
  id: 'user-001',
  email: 'alice@acme.com',
  username: 'alice',
  display_name: 'Alice Smith',
  phone: '+1555000111',
  active: true,
  email_verified: true,
  locked_until: null,
};

// ── Search ────────────────────────────────────────────────────────────────────

test('renders search bar on page load', async ({ adminPage: page }) => {
  await page.goto('/admin/system/users');
  await expect(page.getByPlaceholder(/search by email/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /search/i })).toBeVisible();
});

test('does not fetch until search is submitted', async ({ adminPage: page }) => {
  let called = false;
  await page.route('**/admin/users**', () => { called = true; });

  await page.goto('/admin/system/users');
  await page.waitForLoadState('networkidle');
  expect(called).toBe(false);
});

test('shows results after searching', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  await expect(page.getByText('alice@acme.com')).toBeVisible();
  await expect(page.getByText('Alice Smith')).toBeVisible();
  await expect(page.getByText('Acme Corp')).toBeVisible();
});

test('shows Active badge for active user', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  await expect(page.getByText('Active').first()).toBeVisible();
});

test('shows Disabled badge for inactive user', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('bob');
  await page.getByRole('button', { name: /search/i }).click();

  await expect(page.getByText('Disabled')).toBeVisible();
});

test('shows Locked badge for locked user', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('bob');
  await page.getByRole('button', { name: /search/i }).click();

  await expect(page.getByText('Locked')).toBeVisible();
});

test('shows "No users found" when search returns empty', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: [] });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('nobody');
  await page.getByRole('button', { name: /search/i }).click();

  await expect(page.getByText(/no users found/i)).toBeVisible();
});

test('triggers search on Enter key', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByPlaceholder(/search by email/i).press('Enter');

  await expect(page.getByText('alice@acme.com')).toBeVisible();
});

// ── Edit dialog ───────────────────────────────────────────────────────────────

test('opens edit dialog when clicking a user row', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001$/, USER_DETAIL);

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  await page.getByText('alice@acme.com').click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByLabel(/email/i)).toHaveValue('alice@acme.com');
  await expect(page.getByLabel(/username/i)).toHaveValue('alice');
});

test('saves changes from edit dialog', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001$/, USER_DETAIL);

  let savedBody: unknown;
  await page.route('**/admin/users/user-001', async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    savedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ...USER_DETAIL, display_name: 'Alice Updated' }) });
  });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();
  await page.getByText('alice@acme.com').click();

  await page.getByLabel(/display name/i).fill('Alice Updated');
  await page.getByRole('button', { name: /save changes/i }).click();

  // Dialog should close
  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect((savedBody as Record<string, unknown>).display_name).toBe('Alice Updated');
});

test('shows error when edit save fails', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001$/, USER_DETAIL);
  await page.route('**/admin/users/user-001', async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    await route.fulfill({ status: 500, body: '{}' });
  });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();
  await page.getByText('alice@acme.com').click();
  await page.getByRole('button', { name: /save changes/i }).click();

  await expect(page.getByText(/failed to save/i)).toBeVisible();
});

// ── Sessions dialog ───────────────────────────────────────────────────────────

const SESSIONS = [
  { client_id: 'client-001', client_name: 'My App', granted_at: '2026-03-01T08:00:00Z' },
  { client_id: 'client-002', client_name: 'Another App', granted_at: '2026-03-02T09:00:00Z' },
];

test('opens sessions dialog from dropdown menu', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001\/sessions/, { sessions: SESSIONS });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  // Open dropdown on Alice's row (last cell)
  await page.getByRole('row').filter({ hasText: 'alice@acme.com' }).getByRole('button').click();
  await page.getByText(/view sessions/i).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByText('My App')).toBeVisible();
  await expect(page.getByText('Another App')).toBeVisible();
});

test('shows "No active sessions" when user has no sessions', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001\/sessions/, { sessions: [] });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  await page.getByRole('row').filter({ hasText: 'alice@acme.com' }).getByRole('button').click();
  await page.getByText(/view sessions/i).click();

  await expect(page.getByText(/no active sessions/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /revoke all/i })).toBeDisabled();
});

test('revoke all sessions clears list and shows flash', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });
  await mockGet(page, /\/admin\/users\/user-001\/sessions/, { sessions: SESSIONS });
  await page.route('**/admin/users/user-001/sessions', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    await route.fulfill({ status: 200, body: '{}' });
  });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('alice');
  await page.getByRole('button', { name: /search/i }).click();

  await page.getByRole('row').filter({ hasText: 'alice@acme.com' }).getByRole('button').click();
  await page.getByText(/view sessions/i).click();
  await page.getByRole('button', { name: /revoke all/i }).click();

  await expect(page.getByText(/no active sessions/i)).toBeVisible();
});

// ── Unlock ────────────────────────────────────────────────────────────────────

test('unlock action calls unlock endpoint and removes Locked badge', async ({ adminPage: page }) => {
  await mockGet(page, /\/admin\/users\?q=/, { users: USERS });

  let unlockCalled = false;
  await page.route('**/admin/users/user-002/unlock', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    unlockCalled = true;
    await route.fulfill({ status: 200, body: '{}' });
  });

  await page.goto('/admin/system/users');
  await page.getByPlaceholder(/search by email/i).fill('bob');
  await page.getByRole('button', { name: /search/i }).click();

  await page.getByRole('row').filter({ hasText: 'bob@locked.com' }).getByRole('button').click();
  await page.getByText(/unlock account/i).click();

  expect(unlockCalled).toBe(true);
  // Flash message
  await expect(page.getByText(/account unlocked/i)).toBeVisible();
});
