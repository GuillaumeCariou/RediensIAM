/**
 * webhooks.spec.ts — Admin/Org SPA › Webhooks management.
 * All API calls are mocked.
 *
 * The OrgWebhooks page is used at /org/webhooks (org admin)
 * and at /system/organisations/:id/webhooks (super admin).
 * We test the /org route here; same component, same mocks work.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet } from '../../fixtures/mock-api';

const WEBHOOKS = [
  {
    id: 'wh-001',
    url: 'https://example.com/hooks/user',
    events: ['user.created', 'user.deleted'],
    active: true,
    last_delivery_status: 200,
    created_at: '2026-01-01T00:00:00Z',
  },
  {
    id: 'wh-002',
    url: 'https://example.com/hooks/role',
    events: ['role.assigned'],
    active: false,
    last_delivery_status: null,
    created_at: '2026-02-01T00:00:00Z',
  },
];

const DELIVERIES = [
  { id: 'del-001', event: 'user.created', status_code: 200, attempt_count: 1, delivered_at: '2026-03-01T08:00:00Z', payload: '{"event":"user.created"}' },
  { id: 'del-002', event: 'user.deleted', status_code: 500, attempt_count: 3, delivered_at: null, payload: null },
];

// ── List ──────────────────────────────────────────────────────────────────────

test('lists webhooks with URL and events', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.goto('/admin/org/webhooks');

  await expect(page.getByText('https://example.com/hooks/user')).toBeVisible();
  await expect(page.getByText('https://example.com/hooks/role')).toBeVisible();
});

test('shows active/inactive badge', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.goto('/admin/org/webhooks');

  await expect(page.getByText(/active/i).first()).toBeVisible();
  await expect(page.getByText(/inactive|disabled/i)).toBeVisible();
});

// ── Create ────────────────────────────────────────────────────────────────────

test('create dialog requires HTTPS URL', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.goto('/admin/org/webhooks');
  await page.getByRole('button', { name: /add webhook|new webhook/i }).click();

  await expect(page.getByRole('dialog')).toBeVisible();

  // Enter HTTP URL
  await page.getByPlaceholder(/https:\/\//i).fill('http://not-secure.com/hook');
  await page.getByRole('button', { name: /^create$/i }).click();

  await expect(page.getByText(/must use https/i)).toBeVisible();
});

test('create dialog requires at least one event', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.goto('/admin/org/webhooks');
  await page.getByRole('button', { name: /add webhook|new webhook/i }).click();

  await page.getByPlaceholder(/https:\/\//i).fill('https://example.com/new');
  // Don't select any events
  await page.getByRole('button', { name: /^create$/i }).click();

  await expect(page.getByText(/select at least one event/i)).toBeVisible();
});

test('successful create shows secret reveal dialog', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.route('**/org/webhooks', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ id: 'wh-003', url: 'https://example.com/new', events: ['user.created'], active: true, secret: 'whsec_abc123def456', created_at: new Date().toISOString() }),
    });
  });
  // Reload list after create
  await mockGet(page, '/org/webhooks', [...WEBHOOKS, { id: 'wh-003', url: 'https://example.com/new', events: ['user.created'], active: true, last_delivery_status: null, created_at: new Date().toISOString() }]);

  await page.goto('/admin/org/webhooks');
  await page.getByRole('button', { name: /add webhook|new webhook/i }).click();

  await page.getByPlaceholder(/https:\/\//i).fill('https://example.com/new');
  // Select an event
  await page.getByText('user.created').click();
  await page.getByRole('button', { name: /^create$/i }).click();

  // Secret reveal dialog should appear
  await expect(page.getByText(/whsec_/)).toBeVisible();
});

// ── Delete ────────────────────────────────────────────────────────────────────

test('delete webhook calls DELETE endpoint', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  let deleteCalled = false;
  await page.route('**/org/webhooks/wh-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deleteCalled = true;
    await route.fulfill({ status: 204 });
  });
  await mockGet(page, '/org/webhooks', [WEBHOOKS[1]]);

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/delete/i).click();

  // Confirm if needed
  const confirmBtn = page.getByRole('button', { name: /confirm|delete|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(deleteCalled).toBe(true);
});

// ── Test webhook ──────────────────────────────────────────────────────────────

test('test button shows success feedback', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.route('**/org/webhooks/wh-001/test', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ status_code: 200 }) });
  });

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/test/i).click();

  await expect(page.getByText(/200|success/i)).toBeVisible();
});

test('test button shows failure feedback on 5xx', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.route('**/org/webhooks/wh-001/test', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ status_code: 503 }) });
  });

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/test/i).click();

  await expect(page.getByText(/503|failed|error/i)).toBeVisible();
});

// ── Delivery log ──────────────────────────────────────────────────────────────

test('delivery log shows event and status', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);
  await mockGet(page, '/org/webhooks/wh-001/deliveries', DELIVERIES);

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/deliveries|delivery log/i).click();

  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByText('user.created')).toBeVisible();
  await expect(page.getByText('200')).toBeVisible();
  await expect(page.getByText('500')).toBeVisible();
});

test('expandable delivery shows payload', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);
  await mockGet(page, '/org/webhooks/wh-001/deliveries', DELIVERIES);

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/deliveries|delivery log/i).click();

  // Click on the first delivery row to expand payload
  await page.getByRole('row').filter({ hasText: 'user.created' }).click();

  await expect(page.getByText('{"event":"user.created"}')).toBeVisible();
});

// ── Secret rotation ───────────────────────────────────────────────────────────

test('rotate secret shows new secret', async ({ adminPage: page }) => {
  await mockGet(page, '/org/webhooks', WEBHOOKS);

  await page.route('**/org/webhooks/wh-001/rotate-secret', async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ secret: 'whsec_new_rotated_secret' }) });
  });

  await page.goto('/admin/org/webhooks');

  const row = page.getByRole('row').filter({ hasText: 'https://example.com/hooks/user' });
  await row.getByRole('button').click();
  await page.getByText(/rotate secret/i).click();

  await expect(page.getByText(/whsec_new_rotated_secret/)).toBeVisible();
});
