/**
 * account.spec.ts — Account self-service page tests (Admin SPA /account).
 *
 * Uses the adminPage fixture (authenticated via sessionStorage injection).
 * All API calls are mocked.
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPatch, mockPost, mockDelete } from '../../fixtures/mock-api';

const ME: Record<string, unknown> = {
  id: 'user-001',
  username: 'alice', discriminator: '0001',
  email: 'alice@acme.com', display_name: 'Alice Smith',
  email_verified: true, totp_enabled: false,
  last_login_at: '2026-03-01T10:00:00Z',
  roles: ['org_admin'], org_id: 'org-001', project_id: null,
  new_device_alerts_enabled: false,
};

const MFA_STATUS = { totp_enabled: false, backup_codes_remaining: 0, phone_verified: false };

// ── Page loads ────────────────────────────────────────────────────────────────

test('renders account page with tabs', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);

  await page.goto('/admin/account');

  await expect(page.getByRole('tab', { name: /profile/i })).toBeVisible();
  await expect(page.getByRole('tab', { name: /security/i })).toBeVisible();
  await expect(page.getByRole('tab', { name: /sessions/i })).toBeVisible();
});

// ── Profile tab ───────────────────────────────────────────────────────────────

test('profile tab shows username, email and verified badge', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);

  await page.goto('/admin/account');

  await expect(page.getByText('alice#0001')).toBeVisible();
  await expect(page.getByText('alice@acme.com')).toBeVisible();
  await expect(page.getByText(/verified/i)).toBeVisible();
});

test('saves updated display name', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);

  let patchBody: unknown;
  await page.route('**/account/me', async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    patchBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ...ME, display_name: 'Alice Updated' }) });
  });

  await page.goto('/admin/account');

  const displayNameInput = page.getByLabel(/display name/i);
  await displayNameInput.clear();
  await displayNameInput.fill('Alice Updated');
  await page.getByRole('button', { name: /save/i }).first().click();

  await expect(page.getByText(/saved/i)).toBeVisible();
  expect((patchBody as Record<string, unknown>).display_name).toBe('Alice Updated');
});

test('password change tab sends old and new password', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);

  let patchBody: unknown;
  await page.route('**/account/password', async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    patchBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: '{}' });
  });

  await page.goto('/admin/account');

  await page.getByLabel(/current password/i).fill('OldP@ss1!');
  await page.getByLabel(/new password/i).fill('NewP@ss1!');
  await page.getByRole('button', { name: /change password/i }).click();

  expect((patchBody as Record<string, unknown>).current_password).toBe('OldP@ss1!');
  expect((patchBody as Record<string, unknown>).new_password).toBe('NewP@ss1!');
});

// ── Security tab — TOTP setup ─────────────────────────────────────────────────

test('security tab shows TOTP status', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  await expect(page.getByText(/authenticator app/i)).toBeVisible();
});

test('setup TOTP shows QR code / secret', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockPost(page, '/account/mfa/totp/setup', {
    secret: 'JBSWY3DPEHPK3PXP',
    qr_url: 'otpauth://totp/test?secret=JBSWY3DPEHPK3PXP',
  });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();
  await page.getByRole('button', { name: /set up|enable.*authenticator/i }).click();

  await expect(page.getByText(/JBSWY3DPEHPK3PXP/)).toBeVisible();
});

test('backup codes section shows count', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', { ...ME, totp_enabled: true });
  await mockGet(page, '/account/mfa', { ...MFA_STATUS, totp_enabled: true, backup_codes_remaining: 5 });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  await expect(page.getByText(/5/)).toBeVisible();
});

// ── Sessions tab ──────────────────────────────────────────────────────────────

const SESSIONS = [
  { client_id: 'client-001', client_name: 'My App', granted_at: '2026-03-01T08:00:00Z' },
  { client_id: 'client-002', client_name: 'Another App', granted_at: '2026-03-02T09:00:00Z' },
];

test('sessions tab lists OAuth2 apps', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/sessions', { sessions: SESSIONS });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /sessions/i }).click();

  await expect(page.getByText('My App')).toBeVisible();
  await expect(page.getByText('Another App')).toBeVisible();
});

test('revoke single session calls DELETE with client_id', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/sessions', { sessions: SESSIONS });

  let deletedClientId = '';
  await page.route('**/account/sessions/**', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deletedClientId = route.request().url().split('/').pop() ?? '';
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /sessions/i }).click();

  // Click revoke on the first session
  await page.getByRole('row').filter({ hasText: 'My App' }).getByRole('button').click();

  expect(deletedClientId).toBe('client-001');
});

test('revoke all sessions calls DELETE /account/sessions', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/sessions', { sessions: SESSIONS });

  let revokeAllCalled = false;
  await page.route('**/account/sessions', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    revokeAllCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /sessions/i }).click();

  await page.getByRole('button', { name: /revoke all/i }).click();

  // Confirm if there's an alert dialog
  const confirmBtn = page.getByRole('button', { name: /confirm|revoke|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(revokeAllCalled).toBe(true);
});

// ── Social accounts ───────────────────────────────────────────────────────────

const SOCIAL_ACCOUNTS = [
  { id: 'social-001', provider: 'google', email: 'alice@gmail.com' },
];

test('shows linked social accounts', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/social-accounts', { accounts: SOCIAL_ACCOUNTS });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  await expect(page.getByText(/google/i)).toBeVisible();
  await expect(page.getByText('alice@gmail.com')).toBeVisible();
});

// ── C2: Social "Connect" flow ─────────────────────────────────────────────────

test('connect provider button navigates to oauth2 link start', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/social-accounts', { accounts: [] });

  let navigatedTo = '';
  page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  // Click "Connect" (or "Link") for any provider
  await page.getByRole('button', { name: /connect|link.*provider/i }).first().click();

  await page.waitForTimeout(500);
  expect(navigatedTo).toContain('/auth/oauth2/link/start');
});

test('shows error when unlinking last auth method', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/social-accounts', { accounts: SOCIAL_ACCOUNTS });

  await page.route('**/account/social-accounts/social-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    await route.fulfill({
      status: 422,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'cannot_remove_last_auth_method' }),
    });
  });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  await page.getByRole('button', { name: /unlink/i }).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|unlink|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  await expect(page.getByText(/cannot remove.*last.*auth|last.*sign.in method/i)).toBeVisible();
});

test('unlink social account calls DELETE', async ({ adminPage: page }) => {
  await mockGet(page, '/account/me', ME);
  await mockGet(page, '/account/mfa', MFA_STATUS);
  await mockGet(page, '/account/social-accounts', { accounts: SOCIAL_ACCOUNTS });

  let unlinkCalled = false;
  await page.route('**/account/social-accounts/social-001', async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    unlinkCalled = true;
    await route.fulfill({ status: 204 });
  });

  await page.goto('/admin/account');
  await page.getByRole('tab', { name: /security/i }).click();

  await page.getByRole('button', { name: /unlink/i }).click();

  // Confirm if needed
  const confirmBtn = page.getByRole('button', { name: /confirm|unlink|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(unlinkCalled).toBe(true);
});
