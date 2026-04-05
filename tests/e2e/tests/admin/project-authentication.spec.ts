/**
 * project-authentication.spec.ts — Authentication tab of a project.
 *
 * Covers: SAML IdP CRUD, OAuth2 provider config, password policy, MFA settings,
 * login theme, email verification toggles.
 *
 * Route: /system/organisations/:oid/projects/:pid/authentication
 */
import { test, expect } from '../../fixtures/auth';
import { mockGet, mockPost, mockPatch, mockDelete } from '../../fixtures/mock-api';

const ORG_ID  = 'org-001';
const PROJ_ID = 'proj-001';
const BASE_URL = `/admin/system/organisations/${ORG_ID}/projects/${PROJ_ID}/authentication`;

const PROJECT_INFO = {
  id: PROJ_ID,
  name: 'Test Project',
  active: true,
  require_role_to_login: false,
  allow_self_registration: true,
  email_verification_enabled: true,
  sms_verification_enabled: false,
  mfa_required: false,
  breach_check_enabled: true,
  password_min_length: 8,
  password_require_uppercase: false,
  password_require_number: false,
  password_require_symbol: false,
  allowed_domains: [],
  theme: { providers: [], hydra_local_login: true },
};

const ROLES = [
  { id: 'role-001', name: 'Member', rank: 1 },
  { id: 'role-002', name: 'Admin', rank: 2 },
];

const SAML_PROVIDERS = [
  { id: 'saml-001', entity_id: 'https://idp.example.com', metadata_url: 'https://idp.example.com/metadata', email_attribute_name: 'email', name_attribute_name: 'displayName', jit_provisioning: true, active: true },
];

// ── Page load ─────────────────────────────────────────────────────────────────

test('renders authentication tabs', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: SAML_PROVIDERS });

  await page.goto(BASE_URL);

  // Expect tabs for different auth config sections
  await expect(page.getByRole('tab', { name: /general|settings/i })).toBeVisible();
  await expect(page.getByRole('tab', { name: /social|oauth|providers/i })).toBeVisible();
  await expect(page.getByRole('tab', { name: /saml/i })).toBeVisible();
  await expect(page.getByRole('tab', { name: /theme|branding/i })).toBeVisible();
});

// ── General settings ──────────────────────────────────────────────────────────

test('toggles self-registration and saves', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  let savedBody: unknown;
  await page.route(/\/project\/info|\/admin\/projects\/proj-001$/, async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    savedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify({ ...PROJECT_INFO, allow_self_registration: false }) });
  });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /general|settings/i }).click();

  // Toggle self-registration off
  const selfRegSwitch = page.getByRole('switch', { name: /self.registration/i });
  if (await selfRegSwitch.isChecked()) {
    await selfRegSwitch.click();
  }

  await page.getByRole('button', { name: /save/i }).click();

  expect((savedBody as Record<string, unknown>).allow_self_registration).toBe(false);
});

test('password policy fields saved', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  let savedBody: unknown;
  await page.route(/\/project\/info|\/admin\/projects\/proj-001$/, async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    savedBody = await route.request().postDataJSON();
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(PROJECT_INFO) });
  });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /general|settings/i }).click();

  const minLengthInput = page.getByLabel(/minimum.*length|min.*length/i);
  await minLengthInput.fill('12');

  await page.getByRole('button', { name: /save/i }).click();

  expect((savedBody as Record<string, unknown>).password_min_length).toBe(12);
});

// ── SAML providers ────────────────────────────────────────────────────────────

test('SAML tab lists existing providers', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: SAML_PROVIDERS });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /saml/i }).click();

  await expect(page.getByText('https://idp.example.com')).toBeVisible();
});

test('add SAML provider calls POST', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  let postedBody: unknown;
  await page.route(/\/saml-providers/, async (route) => {
    if (route.request().method() !== 'POST') { await route.fallback(); return; }
    postedBody = await route.request().postDataJSON();
    await route.fulfill({
      contentType: 'application/json',
      body: JSON.stringify({ id: 'saml-002', entity_id: 'https://newIdp.com', metadata_url: 'https://newIdp.com/metadata', email_attribute_name: 'email', jit_provisioning: false, active: true }),
    });
  });
  await mockGet(page, /\/saml-providers/, { providers: [{ id: 'saml-002', entity_id: 'https://newIdp.com', metadata_url: 'https://newIdp.com/metadata', email_attribute_name: 'email', jit_provisioning: false, active: true }] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /saml/i }).click();

  await page.getByRole('button', { name: /add.*saml|new.*saml/i }).click();

  const dialog = page.getByRole('dialog');
  await dialog.getByLabel(/entity.id/i).fill('https://newIdp.com');
  await dialog.getByLabel(/metadata.url/i).fill('https://newIdp.com/metadata');
  await dialog.getByRole('button', { name: /add|save|create/i }).click();

  await expect(page.getByRole('dialog')).not.toBeVisible();
  expect((postedBody as Record<string, unknown>).entity_id).toBe('https://newIdp.com');
});

test('delete SAML provider calls DELETE', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: SAML_PROVIDERS });

  let deleteCalled = false;
  await page.route(/\/saml-providers\/saml-001/, async (route) => {
    if (route.request().method() !== 'DELETE') { await route.fallback(); return; }
    deleteCalled = true;
    await route.fulfill({ status: 204 });
  });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /saml/i }).click();

  await page.getByRole('row').filter({ hasText: 'https://idp.example.com' }).getByRole('button').click();
  await page.getByText(/delete/i).click();

  const confirmBtn = page.getByRole('button', { name: /confirm|delete|yes/i }).last();
  if (await confirmBtn.isVisible({ timeout: 500 }).catch(() => false)) {
    await confirmBtn.click();
  }

  expect(deleteCalled).toBe(true);
});

// ── Social OAuth2 providers ───────────────────────────────────────────────────

test('social tab shows built-in provider toggles', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /social|oauth|providers/i }).click();

  await expect(page.getByText(/google/i)).toBeVisible();
  await expect(page.getByText(/github/i)).toBeVisible();
  await expect(page.getByText(/gitlab/i)).toBeVisible();
  await expect(page.getByText(/facebook/i)).toBeVisible();
});

test('enabling Google provider shows client_id and secret fields', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /social|oauth|providers/i }).click();

  // Enable Google
  const googleSwitch = page.locator('[data-provider="google"] [role="switch"], ')
    .or(page.getByRole('region').filter({ hasText: /google/i }).getByRole('switch'));
  if (await googleSwitch.count() > 0) {
    await googleSwitch.first().click();
  }

  // After enabling, client_id and secret fields should be visible
  await expect(page.getByLabel(/client.?id/i).first()).toBeVisible();
});

// ── A1: Social secret "saved" state ──────────────────────────────────────────

test('saved secret shows placeholder instead of value', async ({ adminPage: page }) => {
  const projectWithGoogle = {
    ...PROJECT_INFO,
    theme: { providers: [{ name: 'google', enabled: true, client_id: 'google-client-id', has_secret: true }], hydra_local_login: true },
  };
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, projectWithGoogle);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /social|oauth|providers/i }).click();

  // Secret field should show a "saved" placeholder, not expose the value
  const secretInput = page.getByLabel(/client.*secret|secret/i).first();
  const placeholder = await secretInput.getAttribute('placeholder');
  expect(placeholder).toMatch(/saved|enter new to replace/i);
  await expect(secretInput).toHaveValue('');
});

test('PATCH body omits secret when secret field is not changed', async ({ adminPage: page }) => {
  const projectWithGoogle = {
    ...PROJECT_INFO,
    theme: { providers: [{ name: 'google', enabled: true, client_id: 'google-client-id', has_secret: true }], hydra_local_login: true },
  };
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, projectWithGoogle);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  let patchBody: Record<string, unknown> = {};
  await page.route(/\/project\/info|\/admin\/projects\/proj-001$/, async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    patchBody = await route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(projectWithGoogle) });
  });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /social|oauth|providers/i }).click();

  // Only update client_id, leave secret untouched
  await page.getByLabel(/client.?id/i).first().fill('new-client-id');
  await page.getByRole('button', { name: /save/i }).click();

  // The PATCH body should not include the secret field
  const providers = (patchBody.providers ?? patchBody.theme) as unknown[];
  expect(JSON.stringify(providers)).not.toMatch(/"secret"/);
});

// ── C6: IP allowlist ──────────────────────────────────────────────────────────

test('IP allowlist field is visible in general settings', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /general|settings/i }).click();

  await expect(page.getByLabel(/ip.*allowlist|allowed.*ip|ip.*whitelist/i)).toBeVisible();
});

test('PATCH body includes ip_allowlist on save', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  let patchBody: Record<string, unknown> = {};
  await page.route(/\/project\/info|\/admin\/projects\/proj-001$/, async (route) => {
    if (route.request().method() !== 'PATCH') { await route.fallback(); return; }
    patchBody = await route.request().postDataJSON() as Record<string, unknown>;
    await route.fulfill({ contentType: 'application/json', body: JSON.stringify(PROJECT_INFO) });
  });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /general|settings/i }).click();

  await page.getByLabel(/ip.*allowlist|allowed.*ip|ip.*whitelist/i).fill('192.168.1.0/24\n10.0.0.0/8');
  await page.getByRole('button', { name: /save/i }).click();

  const field = patchBody.ip_allowlist ?? patchBody.allowed_ips ?? patchBody.allowed_domains;
  expect(field).toBeTruthy();
});

// ── Login theme ───────────────────────────────────────────────────────────────

test('theme tab shows color and font controls', async ({ adminPage: page }) => {
  await mockGet(page, /\/project\/info|\/admin\/projects\/proj-001$/, PROJECT_INFO);
  await mockGet(page, /\/project\/roles|\/admin\/projects\/proj-001\/roles/, { roles: ROLES });
  await mockGet(page, /\/saml-providers/, { providers: [] });

  await page.goto(BASE_URL);
  await page.getByRole('tab', { name: /theme|branding/i }).click();

  await expect(page.getByLabel(/primary color/i)).toBeVisible();
  await expect(page.getByLabel(/logo/i)).toBeVisible();
});
