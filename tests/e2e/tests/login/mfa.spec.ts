/**
 * mfa.spec.ts — MFA Challenge page tests (Login SPA).
 *
 * The MFA page reads mfa_type from sessionStorage (set by the login flow).
 * We seed it via page.addInitScript to simulate each MFA mode.
 *
 * WebAuthn is tested by mocking navigator.credentials.get — no real
 * hardware required.
 */
import { test, expect } from '@playwright/test';

// ── TOTP mode ─────────────────────────────────────────────────────────────────

test.describe('TOTP mode', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_type', 'totp');
    });
  });

  test('renders TOTP form with 6-digit input', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/two-factor auth/i)).toBeVisible();
    await expect(page.getByPlaceholder('000000')).toBeVisible();
    await expect(page.getByRole('button', { name: /verify/i })).toBeVisible();
  });

  test('verify button is disabled until 6 digits entered', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByRole('button', { name: /verify/i })).toBeDisabled();
    await page.getByPlaceholder('000000').fill('12345');
    await expect(page.getByRole('button', { name: /verify/i })).toBeDisabled();
    await page.getByPlaceholder('000000').fill('123456');
    await expect(page.getByRole('button', { name: /verify/i })).toBeEnabled();
  });

  test('shows error on invalid TOTP code', async ({ page }) => {
    await page.route('**/auth/mfa/totp/verify', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'invalid_code' }) });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByPlaceholder('000000').fill('000000');
    await page.getByRole('button', { name: /verify/i }).click();

    await expect(page.getByText(/invalid or expired code/i)).toBeVisible();
  });

  test('follows redirect_to on valid TOTP', async ({ page }) => {
    await page.route('**/auth/mfa/totp/verify', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ redirect_to: 'http://localhost/admin/?code=abc' }) });
    });

    let navigatedTo = '';
    page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByPlaceholder('000000').fill('123456');
    await page.getByRole('button', { name: /verify/i }).click();

    await page.waitForTimeout(500);
    expect(navigatedTo).toContain('/admin/');
  });

  test('shows backup code option', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/backup code/i)).toBeVisible();
  });

  test('switches to backup code mode', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/use a backup code instead/i).click();

    await expect(page.getByText(/use a backup code/i)).toBeVisible();
    await expect(page.getByPlaceholder('XXXXXXXX')).toBeVisible();
  });
});

// ── Backup code mode ──────────────────────────────────────────────────────────

test.describe('Backup code mode', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_type', 'totp'); // initial type, will switch
    });
  });

  test('accepts 8-character uppercase backup code', async ({ page }) => {
    await page.route('**/auth/mfa/backup-codes/verify', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ redirect_to: 'http://localhost/admin/' }) });
    });

    let navigatedTo = '';
    page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/use a backup code instead/i).click();

    const input = page.getByPlaceholder('XXXXXXXX');
    await input.fill('ABCD1234');
    await page.getByRole('button', { name: /verify/i }).click();

    await page.waitForTimeout(500);
    expect(navigatedTo).toContain('/admin/');
  });

  test('normalises lowercase input to uppercase', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/use a backup code instead/i).click();

    const input = page.getByPlaceholder('XXXXXXXX');
    await input.fill('abcd1234');
    await expect(input).toHaveValue('ABCD1234');
  });

  test('shows error on invalid backup code', async ({ page }) => {
    await page.route('**/auth/mfa/backup-codes/verify', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'invalid_code' }) });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/use a backup code instead/i).click();
    await page.getByPlaceholder('XXXXXXXX').fill('BADCODE1');
    await page.getByRole('button', { name: /verify/i }).click();

    await expect(page.getByText(/invalid backup code/i)).toBeVisible();
  });

  test('verify button disabled until 8 chars', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/use a backup code instead/i).click();

    const btn = page.getByRole('button', { name: /verify/i });
    await expect(btn).toBeDisabled();
    await page.getByPlaceholder('XXXXXXXX').fill('ABCD123');
    await expect(btn).toBeDisabled();
    await page.getByPlaceholder('XXXXXXXX').fill('ABCD1234');
    await expect(btn).toBeEnabled();
  });
});

// ── SMS mode ──────────────────────────────────────────────────────────────────

test.describe('SMS mode', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_type', 'sms');
      sessionStorage.setItem('mfa_phone_hint', '+1***5678');
    });
  });

  test('renders SMS form with phone hint', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/sms verification/i)).toBeVisible();
    await expect(page.getByText(/\+1\*\*\*5678/)).toBeVisible();
    await expect(page.getByPlaceholder('000000')).toBeVisible();
  });

  test('shows resend button for SMS mode', async ({ page }) => {
    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/resend code/i)).toBeVisible();
  });

  test('resend button calls send SMS endpoint', async ({ page }) => {
    let sendCalled = false;
    await page.route('**/auth/mfa/phone/send', async (route) => {
      sendCalled = true;
      await route.fulfill({ contentType: 'application/json', body: '{}' });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByText(/resend code/i).click();

    expect(sendCalled).toBe(true);
    await expect(page.getByText(/code resent/i)).toBeVisible();
  });

  test('verifies SMS OTP and redirects', async ({ page }) => {
    await page.route('**/auth/mfa/phone/verify', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ redirect_to: 'http://localhost/admin/' }) });
    });

    let navigatedTo = '';
    page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await page.getByPlaceholder('000000').fill('654321');
    await page.getByRole('button', { name: /verify/i }).click();

    await page.waitForTimeout(500);
    expect(navigatedTo).toContain('/admin/');
  });
});

// ── B3: MFA mandatory setup redirect ─────────────────────────────────────────

test.describe('MFA mandatory setup', () => {
  test('login response requires_mfa_setup navigates to /mfa-setup', async ({ page }) => {
    await page.route('**/auth/login', async (route) => {
      if (route.request().method() !== 'POST') { await route.fallback(); return; }
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ requires_mfa_setup: true, user_id: 'user-001' }),
      });
    });

    let navigatedTo = '';
    page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

    await page.goto('/login?login_challenge=challenge-mfa-001');
    await page.locator('#identifier').fill('user@example.com');
    await page.locator('#password').fill('Password1!');
    await page.getByRole('button', { name: /sign in|log in/i }).click();

    await page.waitForTimeout(500);
    expect(navigatedTo).toContain('/mfa-setup');
  });

  test('mfa-setup page shows QR code and secret', async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_setup_user_id', 'user-001');
    });

    await page.route('**/auth/mfa/totp/setup', async (route) => {
      if (route.request().method() !== 'POST') { await route.fallback(); return; }
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ secret: 'JBSWY3DPEHPK3PXP', qr_url: 'otpauth://totp/test?secret=JBSWY3DPEHPK3PXP' }),
      });
    });

    await page.goto('/mfa-setup?login_challenge=challenge-mfa-001');

    await expect(page.getByText(/JBSWY3DPEHPK3PXP/)).toBeVisible();
    await expect(page.locator('img[alt*="qr"], canvas, svg')).toBeVisible();
  });

  test('confirming setup code follows redirect_to', async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_setup_user_id', 'user-001');
    });

    await page.route('**/auth/mfa/totp/setup', async (route) => {
      if (route.request().method() !== 'POST') { await route.fallback(); return; }
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ secret: 'JBSWY3DPEHPK3PXP', qr_url: 'otpauth://totp/test?secret=JBSWY3DPEHPK3PXP' }),
      });
    });

    await page.route('**/auth/mfa/totp/confirm', async (route) => {
      if (route.request().method() !== 'POST') { await route.fallback(); return; }
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ redirect_to: 'http://localhost/admin/?code=abc' }),
      });
    });

    let navigatedTo = '';
    page.on('request', req => { if (req.isNavigationRequest()) navigatedTo = req.url(); });

    await page.goto('/mfa-setup?login_challenge=challenge-mfa-001');
    await page.getByPlaceholder('000000').fill('123456');
    await page.getByRole('button', { name: /confirm|verify|done/i }).click();

    await page.waitForTimeout(500);
    expect(navigatedTo).toContain('/admin/');
  });
});

// ── WebAuthn mode ─────────────────────────────────────────────────────────────

test.describe('WebAuthn mode', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      sessionStorage.setItem('mfa_type', 'webauthn');
    });
  });

  test('renders passkey UI', async ({ page }) => {
    // Mock the options fetch and stub credentials.get to prevent a real prompt
    await page.route('**/auth/mfa/webauthn/options', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({ error: 'no_credentials' }),
      });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/passkey sign-in/i)).toBeVisible();
  });

  test('shows error when options fetch fails', async ({ page }) => {
    await page.route('**/auth/mfa/webauthn/options', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'not_configured' }) });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/failed to get passkey options/i)).toBeVisible();
  });

  test('shows error when credentials.get is cancelled (NotAllowedError)', async ({ page }) => {
    // Mock the WebAuthn browser API to throw NotAllowedError
    await page.addInitScript(() => {
      const origGet = navigator.credentials.get.bind(navigator.credentials);
      navigator.credentials.get = async () => {
        const err = new DOMException('User cancelled', 'NotAllowedError');
        throw err;
      };
      // Restore after we've set it
      void origGet;
    });

    await page.route('**/auth/mfa/webauthn/options', async (route) => {
      await route.fulfill({
        contentType: 'application/json',
        body: JSON.stringify({
          challenge: 'dGVzdGNoYWxsZW5nZQ', // base64url "testchallenge"
          allowCredentials: [],
          timeout: 60000,
        }),
      });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/cancelled or timed out/i)).toBeVisible();
  });

  test('shows backup code fallback link in webauthn mode', async ({ page }) => {
    await page.route('**/auth/mfa/webauthn/options', async (route) => {
      await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ error: 'no_credentials' }) });
    });

    await page.goto('/mfa?login_challenge=challenge-mfa-001');
    await expect(page.getByText(/backup code instead/i)).toBeVisible();
  });
});
