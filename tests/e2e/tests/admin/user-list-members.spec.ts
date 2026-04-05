/**
 * user-list-members.spec.ts — User list member panel (Admin SPA).
 *
 * Covers:
 *   B1 — invite_pending badge, "Resend invite" dropdown item, success flash,
 *         user_already_active error alert.
 *   C3 — per-user sessions dialog from 3-dot menu; per-session revoke;
 *         revoke-all clears list.
 *
 * Route: /admin/system/userlists/:lid
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet } from '../../fixtures/mock-api';

const LIST_ID = 'ul-001';
const BASE_URL = `/admin/system/userlists/${LIST_ID}`;

const USER_LIST = { id: LIST_ID, name: 'Beta Testers', org_id: 'org-001', org_name: 'Acme' };

const MEMBERS = [
  { id: 'usr-001', email: 'alice@acme.com', display_name: 'Alice', invite_pending: false, added_at: '2026-01-01T00:00:00Z' },
  { id: 'usr-002', email: 'bob@acme.com',   display_name: 'Bob',   invite_pending: true,  added_at: '2026-02-01T00:00:00Z' },
];

const BOB_SESSIONS = [
  { session_id: 'sess-b-001', client_name: 'Web Browser', last_active_at: '2026-04-01T10:00:00Z', ip: '1.2.3.4' },
  { session_id: 'sess-b-002', client_name: 'Mobile App',  last_active_at: '2026-03-30T08:00:00Z', ip: '5.6.7.8' },
];

// ── B1: invite_pending badge ──────────────────────────────────────────────────

test('user with invite_pending shows amber "Invite pending" badge', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await expect(bobRow.getByText(/invite pending/i)).toBeVisible();
});

test('user without invite_pending does not show invite badge', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });

  await page.goto(BASE_URL);

  const aliceRow = page.getByRole('row').filter({ hasText: 'alice@acme.com' });
  await expect(aliceRow.getByText(/invite pending/i)).not.toBeVisible();
});

test('"Resend invite" appears in dropdown for pending user', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();

  await expect(page.getByText(/resend invite/i)).toBeVisible();
});

test('resend invite success shows flash', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });

  await page.route(`**/userlists/${LIST_ID}/members/usr-002/resend-invite`, async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ok: true }) });
  });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();
  await page.getByText(/resend invite/i).click();

  await expect(page.getByText(/invite (re)?sent|invitation sent/i)).toBeVisible();
});

test('resend invite user_already_active shows alert', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });

  await page.route(`**/userlists/${LIST_ID}/members/usr-002/resend-invite`, async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      status: 409,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'user_already_active' }),
    });
  });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();
  await page.getByText(/resend invite/i).click();

  await expect(page.getByText(/already active|user_already_active/i)).toBeVisible();
});

// ── C3: Per-user sessions in admin panel ──────────────────────────────────────

test('"View sessions" in 3-dot menu opens sessions dialog', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });
  await mockGet(page, `/admin/users/usr-002/sessions`, { sessions: BOB_SESSIONS });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();
  await page.getByText(/view sessions/i).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByText('Web Browser')).toBeVisible();
  await expect(page.getByText('Mobile App')).toBeVisible();
});

test('per-session revoke calls DELETE with session_id', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });
  await mockGet(page, `/admin/users/usr-002/sessions`, { sessions: BOB_SESSIONS });

  let revokedId = '';
  await page.route('**/admin/users/usr-002/sessions/**', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    revokedId = route.request().url().split('/').pop() ?? '';
    await route.fulfill({ status: 204 });
  });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();
  await page.getByText(/view sessions/i).click();

  const dialog = page.getByRole('dialog');
  await dialog.getByRole('row').filter({ hasText: 'Web Browser' }).getByRole('button').click();

  expect(revokedId).toBe('sess-b-001');
});

test('revoke-all sessions clears the list in dialog', async ({ adminPage: page }) => {
  await mockGet(page, `/userlists/${LIST_ID}`, USER_LIST);
  await mockGet(page, `/userlists/${LIST_ID}/members`, { members: MEMBERS });
  await mockGet(page, `/admin/users/usr-002/sessions`, { sessions: BOB_SESSIONS });

  let revokeAllCalled = false;
  await page.route('**/admin/users/usr-002/sessions', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    revokeAllCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto(BASE_URL);

  const bobRow = page.getByRole('row').filter({ hasText: 'bob@acme.com' });
  await bobRow.getByRole('button').click();
  await page.getByText(/view sessions/i).click();

  const dialog = page.getByRole('dialog');
  await dialog.getByRole('button', { name: /revoke all/i }).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|revoke|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(revokeAllCalled).toBe(true);
  // After revoke-all the dialog should show empty state
  await expect(dialog.getByText(/no active sessions|no sessions/i)).toBeVisible();
});
