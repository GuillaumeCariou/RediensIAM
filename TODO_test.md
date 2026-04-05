# E2E Test Plan — RediensIAM SPAs

## Status
- [x] Manual test checklist written
- [x] Playwright project scaffolded (`tests/e2e/`)
- [x] Global auth setup (OIDC flow → sessionStorage capture)
- [x] Auth + mock-API fixtures
- [x] Login SPA: login, register, password-reset specs
- [x] Admin SPA: system-users, org-lifecycle, user-lists specs
- [x] Login SPA: mfa.spec.ts (TOTP, SMS, WebAuthn virtual authenticator mock)
- [x] Login SPA: invite.spec.ts (set-password flow)
- [x] Account SPA: account.spec.ts (profile, password, MFA, sessions, social)
- [x] Admin SPA: webhooks.spec.ts (CRUD, delivery log, test, secret rotation)
- [x] Admin SPA: service-accounts.spec.ts (PATs, API keys, delete SA)
- [x] Admin SPA: project-authentication.spec.ts (SAML, OAuth2 providers, password policy, theme)
- [x] Org SPA: org-email.spec.ts (SMTP configure, test, delete, super-admin context)
- [x] Gap tests (E2E): A1/A3/B1/B3/C2/C3/C6 added to existing specs; user-list-members.spec.ts + deployment-smoke.spec.ts created
- [x] Gap tests (.NET): ManagedApiTests.cs created; MfaLoginTests.cs extended (B3); WebhookTests.cs extended (B5 HMAC)
- [ ] CI: add `playwright test` step to deploy pipeline
- [ ] Seed: ensure dev stack has a seeded super_admin + org_admin + project_manager user

---

## Gaps — features in TODO.md / TODO2.md not yet covered

### .NET integration tests — all gaps now closed

| Feature | File | Status |
|---------|------|--------|
| **TODO2 — `/api/manage/*`** | `Tests/ManagedApi/ManagedApiTests.cs` (new) | ✅ added — all 7 endpoints, super_admin/org_admin/unauthenticated paths |
| **B1 resend-invite endpoint** | `Tests/Auth/InviteFlowTests.cs` | ✅ was already present |
| **B3 requires_mfa_setup response** | `Tests/Auth/MfaLoginTests.cs` | ✅ added — two tests: no-MFA user gets setup flag; TOTP user gets normal challenge |
| **B5 webhook secret rotation HMAC** | `Tests/Webhooks/WebhookTests.cs` | ✅ added — HMAC differs after rotation; SecretHash updated in DB |

### E2E (Playwright) tests — all gaps now closed

| Feature | File | Status |
|---------|------|--------|
| **A1 — Social secret "saved" state** | `admin/project-authentication.spec.ts` | ✅ added |
| **A3 — Rate limit 429 in register** | `login/register.spec.ts` | ✅ added |
| **A3 — Rate limit 429 in password reset** | `login/password-reset.spec.ts` | ✅ added |
| **B1 — Invite pending badge + resend** | `admin/user-list-members.spec.ts` (new) | ✅ added |
| **B3 — MFA setup redirect from login** | `login/mfa.spec.ts` | ✅ added |
| **C2 — Social link "Connect" + last-method error** | `account/account.spec.ts` | ✅ added |
| **C3 — Per-user sessions in admin panel** | `admin/user-list-members.spec.ts` (new) | ✅ added |
| **C6 — IP allowlist textarea** | `admin/project-authentication.spec.ts` | ✅ added |
| **TODO2 — ingress smoke test** | `admin/deployment-smoke.spec.ts` (new) | ✅ added |

---

## Infrastructure overview

```
tests/e2e/
  package.json              ← Playwright + dotenv
  playwright.config.ts      ← projects: admin (authenticated), login (no auth)
  global-setup.ts           ← OIDC login once → save .auth/admin-session.json
  .auth/                    ← gitignored; session snapshots live here
  fixtures/
    auth.ts                 ← adminPage / orgAdminPage fixtures (inject sessionStorage)
    mock-api.ts             ← helpers to wire page.route() mocks for admin API
  tests/
    login/                  ← tests against the real backend (Login SPA flows)
    admin/                  ← tests with mocked API (Admin SPA UI behaviour)
    account/                ← self-service account page
```

### Authentication strategy

The admin SPA uses oidc-client-ts with `sessionStorage` as its store.
Playwright's `storageState` only covers cookies + localStorage, so we:
1. Do the full OIDC flow in `global-setup.ts`, capture `sessionStorage` keys,
   and write them to `.auth/admin-session.json`.
2. In the `adminPage` fixture, inject those keys back via `page.addInitScript`
   before the React app boots — so every test starts already authenticated.

### What needs a real backend (Login SPA tests)

These specs hit the live dev stack (`http://localhost`):
- `login.spec.ts` — credential validation, lockout, error messages
- `register.spec.ts` — form validation, verification step (mocked OTP via route intercept)
- `password-reset.spec.ts` — three-step flow (mocked OTP)

### What is mocked (Admin SPA tests)

All `/admin/*`, `/org/*`, `/project/*` calls are intercepted with
`page.route()` in each spec. This makes tests fast and deterministic
without depending on DB state.

---

## Required env vars

```
# tests/e2e/.env (gitignored)
TEST_BASE_URL=http://localhost
TEST_SUPER_ADMIN_EMAIL=admin@example.com
TEST_SUPER_ADMIN_PASSWORD=AdminP@ss123!
```

Seed the dev stack with these credentials before running.

---

## Running tests

```bash
cd tests/e2e
npm install
npx playwright install chromium

# against live dev stack
npm test

# headed (watch mode)
npm run test:headed

# single spec
npx playwright test tests/login/login.spec.ts

# with trace viewer on failure
npx playwright test --trace on
```

---

## Manual-only checklist (cannot automate reliably)

| # | Test | Why manual |
|---|------|-----------|
| 1 | WebAuthn with real hardware key (YubiKey, TouchID) | No virtual authenticator substitute |
| 2 | Real SMS delivery (Twilio/etc.) | External service; stub in tests |
| 3 | Real email delivery to inbox (non-spam) | External SMTP; MailHog in dev |
| 4 | SAML login with external IdP | Requires live IdP; stub in integration tests |
| 5 | Social OAuth login (Google, GitHub…) | Requires live provider consent screen |
| 6 | Login theme preview visual regression | Subjective / design review |
| 7 | Device alert email content + formatting | Email client rendering |
| 8 | Webhook endpoint reachable from cluster | Needs ngrok or public endpoint |
| 9 | IP allowlist with real network change | Environment-dependent |
| 10 | Org suspension blocks users in browser | Manual smoke-test after deploy |
