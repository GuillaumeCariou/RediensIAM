# RediensIAM — Feature Completion TODO

> Generated 2026-03-29. Backend fully implemented as of 2026-04-02. Frontend work pending.
>
> **Confirmed already implemented** (removed from list after verification):
> - Password policy per project ✅ (min length, uppercase, lowercase, digit, special — backend enforced in AuthController + ProjectController, frontend in Authentication.tsx Registration tab)
> - Social login provider config per project ✅ (Google, GitHub, GitLab, Facebook, custom OIDC — full providers tab in Authentication.tsx, SocialLoginService reads from login_theme JSONB)
> - Account lockout ✅ (FailedLoginCount + LockedUntil with configurable MaxLoginAttempts + LockoutMinutes, resets on password reset)
> - Org self-registration ✅ (by design — orgs are admin-created only, not a gap)

---

## Frontend Architecture Reference

> Read before touching any frontend file.

### Two separate SPAs

| | Admin SPA | Login SPA |
|---|---|---|
| **Root** | `frontend/admin/` | `frontend/login/` |
| **Base path** | `/admin` | `/` |
| **Auth** | OIDC via oidc-client-ts (Hydra AdminClientId, PKCE) | Stateless — Hydra login_challenge flow |
| **UI library** | Shadcn/ui (Radix + Tailwind) | Custom CSS only |
| **API** | `src/api.ts` — named async functions, Bearer token via `apiFetch()` | `src/api.ts` — raw `fetch()`, `credentials: 'include'`, `VITE_API_BASE_URL` |
| **State** | useState + useEffect, AuthContext, no global store | useState + sessionStorage for multi-step flows |
| **Notifications** | Inline `<Alert>` components, `setTimeout` auto-hide for success | Inline `.alert.alert-error` divs |
| **Forms** | Controlled inputs, manual validation, no form library | Controlled inputs, HTML validation attributes |

### Admin SPA — three access contexts

The same feature is often accessible at multiple privilege levels. The same page component renders in all contexts — `useOrgContext()` provides `orgBase`, `projectBase`, `isSystemCtx` to build correct API paths.

```
/system/organisations/:oid/projects/:pid/authentication  → super_admin browsing a project
/project/authentication                                   → project_manager of own project
```

**Never duplicate a page for each context.** Use `useOrgContext()` to make the API calls context-aware.

### Key reusable component

`UserListMembersPanel.tsx` — renders in ProjectUsers, UserListDetail, OrgDetail, and SystemOrgDetail. Any user-row feature (badges, actions) added here appears everywhere automatically.

### Login SPA flow

Every page reads `?login_challenge=` from the URL. Multi-step state (current user ID, challenge, etc.) lives in `sessionStorage`. Pages redirect to `res.redirect_to` on success — this is the Hydra consent redirect, never change it.

---

## A — Security Vulnerabilities

---

### A1. Social Login Client Secrets Stored in Plaintext

**Backend:** ✅ Done — secrets encrypted with AES-256-GCM, stored as `client_secret_enc`, API never returns raw secret (returns `null` for `client_secret` field).

**Frontend — `frontend/admin/src/pages/project/Authentication.tsx` (Providers tab)**

Each provider form (Google, GitHub, GitLab, Facebook, custom OIDC) has a `client_secret` input. The backend now returns `client_secret: null` when a secret is already saved.

Changes needed:
1. In the provider state type, add `secretSaved: boolean` — set to `true` when the loaded `client_secret === null` and the provider is otherwise configured (has a `client_id`).
2. For the `client_secret` input on each provider:
   - When `secretSaved` is `true` and the field is empty: set `placeholder="••••••••• (saved — enter new to replace)"` and render a small `<span className="text-xs text-muted-foreground">Secret is saved</span>` below it.
   - Track a `secretChanged` boolean per provider — set to `true` when the user types in the secret field.
   - When saving: only include `client_secret` in the payload if `secretChanged` is `true`; otherwise omit the field entirely (send `undefined`, not `null`).
3. Apply this pattern consistently to all five provider forms — Google, GitHub, GitLab, Facebook, and the custom OIDC provider.

No new API functions needed — existing `updateProject()` handles the save.

---

### A2. No Security Headers Middleware

**Backend:** ✅ Done — headers added in `Program.cs` middleware pipeline.

**Frontend:** No changes needed.

---

### A3. Registration Endpoint Not Rate Limited

**Backend:** ✅ Done — `POST /auth/register` and `POST /auth/password-reset/request` go through `LoginRateLimiter`.

**Frontend:** No changes needed. The existing 429 error path in `Register.tsx` and `PasswordReset.tsx` already shows the generic error message — confirm it reads as "Too many attempts, try again later" (the `res.error` field will be `"too_many_attempts"`).

---

### A4. OTP Comparison Not Constant-Time

**Backend:** ✅ Done — `CryptographicOperations.FixedTimeEquals` in `OtpCacheService`.

**Frontend:** No changes needed.

---

### A5. Email Enumeration via Timing Difference

**Backend:** ✅ Done — `RequestPasswordReset` always awaits the same operations and returns identical response regardless of whether the user exists.

**Frontend:** No changes needed.

---

## B — Core Missing Features

---

### B1. User Invitation Flow

**Backend:** ✅ Done — `POST /auth/invite/complete`, `POST /org/userlists/{id}/users/{uid}/resend-invite`, `invite_pending` field on user responses, `IEmailService.SendInviteAsync`.

**Frontend — Phase 1: Login SPA — new `SetPassword.tsx` page**

File to create: `frontend/login/src/pages/SetPassword.tsx`

Route to add in `frontend/login/src/App.tsx`:
```tsx
<Route path="/set-password" element={<SetPassword />} />
```

Page flow:
1. On mount, read `?token=` and `?project_id=` from URL. If either is missing, show an error card ("Invalid or expired invite link").
2. Fetch `/auth/login/theme?project_id=<project_id>` to get theme config and password policy — inject CSS vars exactly as `Login.tsx` does (lines 69–79 of Login.tsx).
3. Render a form with two fields: "New password" and "Confirm password" (both `type="password"`, with visibility toggle button — match PasswordReset.tsx style).
4. Below the fields, display the password policy requirements from the theme response (min length, uppercase required, etc.) — render the same requirement checklist that `Register.tsx` uses.
5. On submit:
   - Validate passwords match client-side.
   - Call `POST /auth/invite/complete` with body `{ token, password }`.
   - On `res.error === "password_breached"`: show error "This password has appeared in a data breach. Please choose a different password."
   - On `res.error === "token_expired"` or `"token_not_found"`: show error "This invite link has expired. Ask your administrator to resend the invite."
   - On `res.error === "password_policy"`: show policy violation details from `res.detail`.
   - On success (`res.message === "invite_complete"`): show success card "Password set! You can now log in." with a "Go to login" link back to the project login URL (use `res.login_url` if returned, otherwise `/login`).
6. Loading state: disable button + show "Setting password…" text.

API function to add in `frontend/login/src/api.ts`:
```ts
export async function completeInvite(token: string, password: string) {
  const res = await fetch(`${BASE}/auth/invite/complete`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ token, password }),
  });
  return res.json();
}
```

---

**Frontend — Phase 2: Admin SPA — `UserListMembersPanel.tsx`**

File: `frontend/admin/src/components/UserListMembersPanel.tsx`

The user row already has a 3-dot `DropdownMenu`. Add to the dropdown items:

1. **Invite pending badge** — in the user row, after the username/email text, add:
   ```tsx
   {user.invite_pending && (
     <Badge variant="outline" className="ml-2 text-amber-600 border-amber-400">
       Invite pending
     </Badge>
   )}
   ```

2. **Resend invite** — add to the 3-dot dropdown menu (visible only when `user.invite_pending === true`):
   ```tsx
   <DropdownMenuItem onClick={() => handleResendInvite(user.id)}>
     Resend invite
   </DropdownMenuItem>
   ```
   Handler:
   - Call `await resendInvite(listId, user.id)` (API function below).
   - On success: show inline success alert "Invite resent to {user.email}." (use the existing `setSuccess` / setTimeout pattern).
   - On `res.error === "user_already_active"`: show error alert "This user has already accepted their invitation."

API function to add in `frontend/admin/src/api.ts`:
```ts
export async function resendInvite(listId: string, userId: string) {
  return apiFetch(`/org/userlists/${listId}/users/${userId}/resend-invite`, { method: 'POST' });
}
```

For super_admin context (system routes), the same `UserListMembersPanel` is used — `listId` is already passed as a prop so no extra handling needed.

---

### B2. Admin Account Unlock

**Backend:** ✅ Done — `POST /admin/users/{id}/unlock` and `POST /org/userlists/{id}/users/{uid}/unlock`.

**Frontend — `frontend/admin/src/components/UserListMembersPanel.tsx`**

1. **Locked badge** — in the user row, after the existing invite_pending badge:
   ```tsx
   {user.locked_until && new Date(user.locked_until) > new Date() && (
     <Badge variant="destructive" className="ml-2">
       Locked
     </Badge>
   )}
   ```
   Add a `Tooltip` on hover showing "Locked until {fmtDate(user.locked_until)}".

2. **Unlock button** — add to the 3-dot dropdown (visible only when user is currently locked):
   ```tsx
   <DropdownMenuItem onClick={() => handleUnlock(user.id)} className="text-amber-600">
     Unlock account
   </DropdownMenuItem>
   ```
   Handler:
   - Call `await unlockUser(listId, user.id)` (API function below).
   - On success: refresh the user list and show "Account unlocked." success alert.

API function to add in `frontend/admin/src/api.ts`:
```ts
// listId = null means system-level unlock (/admin/users/:id/unlock)
export async function unlockUser(listId: string | null, userId: string) {
  const path = listId
    ? `/org/userlists/${listId}/users/${userId}/unlock`
    : `/admin/users/${userId}/unlock`;
  return apiFetch(path, { method: 'POST' });
}
```

In `UserListMembersPanel`, pass `listId` (already a prop) to this function. The panel already distinguishes system vs org context via the existing `isSystem` prop — use that to pass `null` vs the actual listId.

---

### B3. Mandatory MFA Enforcement Per Project

**Backend:** ✅ Done — `Project.RequireMfa` flag; login returns `{ requires_mfa_setup: true }` when user has no MFA; MFA setup completes the Hydra login.

**Frontend — Part 1: Admin SPA — `Authentication.tsx` (Registration tab)**

In the Registration tab, after the "Allow self-registration" Switch, add a new Switch row:

```tsx
<div className="flex items-center justify-between">
  <div>
    <p className="font-medium text-sm">Require MFA</p>
    <p className="text-xs text-muted-foreground">
      Users without a second factor cannot complete login until they enroll one.
    </p>
  </div>
  <Switch
    checked={form.require_mfa ?? false}
    onCheckedChange={(v) => setForm(f => ({ ...f, require_mfa: v }))}
  />
</div>
```

Include `require_mfa` in the `updateProject()` payload when saving. The field is already returned by `GET /project/info`.

---

**Frontend — Part 2: Login SPA — `Login.tsx` modification**

In `Login.tsx`, in the `handleSubmit` function, after handling `mfa_required` (existing check), add:

```tsx
if (res.requires_mfa_setup) {
  sessionStorage.setItem('mfa_setup_challenge', challenge);
  sessionStorage.setItem('mfa_setup_user', res.user_id);
  navigate('/mfa-setup');
  return;
}
```

Route to add in `frontend/login/src/App.tsx`:
```tsx
<Route path="/mfa-setup" element={<MfaSetup />} />
```

---

**Frontend — Part 3: Login SPA — new `MfaSetup.tsx` page**

File to create: `frontend/login/src/pages/MfaSetup.tsx`

Page flow:
1. On mount, read `mfa_setup_challenge` and `mfa_setup_user` from `sessionStorage`. If missing, redirect to `/login`.
2. Fetch theme via `?login_challenge=<challenge>` stored challenge — inject CSS vars same as Login.tsx.
3. Show a step-by-step MFA enrollment UI. Present only TOTP (simplest, no dependency on SMS config). If project has WebAuthn enabled show it as an option too.
4. **TOTP step:**
   - Call `POST /account/mfa/totp/setup` (requires the session cookie set during login) to get `otpauth_url` and `secret`.
   - Show QR code: render a `<img src={\`https://api.qrserver.com/v1/create-qr-code/?data=\${encodeURIComponent(otpauth_url)}\`} />` (or use a JS QR library if one is already in package.json — check first).
   - Show the base32 secret as a copyable text field for manual entry.
   - Input for 6-digit verification code.
   - On submit: call `POST /account/mfa/totp/confirm` with the code.
   - On success: the backend completes the Hydra login — follow `res.redirect_to`.
   - Show backup codes from the confirm response in a copy-friendly list with a "I've saved these" checkbox before allowing continue.
5. If TOTP confirm fails (`invalid_code`): show error "Incorrect code. Try again." — do not clear the QR.
6. Loading states: disable all buttons during API calls.

Note: This page calls `/account/mfa/totp/setup` and `/account/mfa/totp/confirm` which require the session cookie established during the login step. The session is already present because the login endpoint set it before returning `requires_mfa_setup: true`.

API functions to add in `frontend/login/src/api.ts`:
```ts
export async function setupTotp() {
  const res = await fetch(`${BASE}/account/mfa/totp/setup`, {
    method: 'POST', credentials: 'include',
  });
  return res.json();
}

export async function confirmTotp(code: string) {
  const res = await fetch(`${BASE}/account/mfa/totp/confirm`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',
    body: JSON.stringify({ code }),
  });
  return res.json();
}
```

---

### B4. System Service Accounts (CRUD + PAT + Roles)

**Backend:** ✅ Done — full CRUD at `/admin/service-accounts`, PAT endpoints, role endpoints.

**Frontend:** The pages `SystemServiceAccounts.tsx` and `ServiceAccountDetail.tsx` already exist and appear substantially implemented from the codebase scan (create dialog, PAT generation with one-time display, role assignment). Verify the following gaps and fill them:

**`frontend/admin/src/pages/system/SystemServiceAccounts.tsx`:**
- Confirm `POST /admin/service-accounts` is called on creation with `{ name, description }`.
- Confirm row click navigates to `/system/service-accounts/:id`.
- Confirm delete calls `DELETE /admin/service-accounts/:id`.
- Confirm the `Active`/inactive status badge reflects `sa.Active`.
- If `Description` column is missing from the table, add it.

**`frontend/admin/src/pages/ServiceAccountDetail.tsx`:**
- Confirm PAT generation calls `POST /admin/service-accounts/:id/pat` and shows the raw token exactly once in a dialog (copy button + "This token will not be shown again" warning).
- Confirm PAT revoke calls `DELETE /admin/service-accounts/:id/pat/:patId`.
- Confirm role assignment calls `POST /admin/service-accounts/:id/roles` with `{ role: "super_admin" }`.
- Confirm role removal calls `DELETE /admin/service-accounts/:id/roles/:roleId`.
- Add a "Disable / Enable" toggle that calls `PATCH /admin/service-accounts/:id` with `{ active: false/true }` if the endpoint exists; otherwise call `DELETE` only for full deletion.

**`frontend/admin/src/api.ts`** — verify these functions exist, add any missing:
```ts
// System service accounts
getSystemServiceAccounts()                          // GET /admin/service-accounts
createSystemServiceAccount(name, description)       // POST /admin/service-accounts
getSystemServiceAccount(id)                         // GET /admin/service-accounts/:id
deleteSystemServiceAccount(id)                      // DELETE /admin/service-accounts/:id
// PATs
createSystemSAPat(saId, name?)                      // POST /admin/service-accounts/:id/pat
listSystemSAPats(saId)                              // GET /admin/service-accounts/:id/pat
revokeSystemSAPat(saId, patId)                      // DELETE /admin/service-accounts/:id/pat/:patId
// Roles
listSystemSARoles(saId)                             // GET /admin/service-accounts/:id/roles
assignSystemSARole(saId, role)                      // POST /admin/service-accounts/:id/roles
revokeSystemSARole(saId, roleId)                    // DELETE /admin/service-accounts/:id/roles/:roleId
```

---

### B5. Webhooks

**Backend:** ✅ Done — full CRUD at `/org/webhooks`, test endpoint, delivery log.

**Frontend — new `frontend/admin/src/pages/org/OrgWebhooks.tsx`**

This is an org-level page. It must also render correctly when accessed from the system context (`/system/organisations/:id/webhooks`) — use `useOrgContext()` to get the correct `orgId` and `apiBase`.

**Route additions in `frontend/admin/src/App.tsx`:**
```tsx
// Under org routes:
<Route path="webhooks" element={<OrgWebhooks />} />

// Under system org detail routes:
<Route path="webhooks" element={<OrgWebhooks />} />
```

**Sidebar addition in `frontend/admin/src/components/Sidebar.tsx`:**
- Org section: add "Webhooks" nav item linking to `${orgBase}/webhooks` with a `Webhook` or `Zap` icon from lucide-react.
- System org detail section: add the same link (already follows the same pattern for audit-log, email, etc.).

**Page structure:**

*Main table view:*
- Card with title "Webhooks" and `[+ Add Webhook]` button top-right.
- Table columns: URL, Events (comma-joined or badge list), Active (Switch), Last delivery status, Created.
- Row actions (3-dot menu): Test, View deliveries, Delete.
- Empty state: "No webhooks configured. Add one to receive event notifications."

*Create webhook dialog:*
- URL input (required, validates `https://` prefix client-side — show "URL must use HTTPS" if not).
- Events: checkbox group for all supported events (`user.created`, `user.updated`, `user.deleted`, `user.locked`, `user.login.success`, `user.login.failure`, `role.assigned`, `role.revoked`, `session.revoked`, `project.updated`). Group them visually: "User events", "Role events", "Session events", "Project events".
- On create: call `POST /org/webhooks` with `{ url, events }`. Response includes `secret` (shown once). Display the secret in a modal: "Webhook secret — copy this now, it won't be shown again." with copy button. Dismiss reveals the webhook in the table.

*Deliveries dialog (per webhook):*
- Triggered from "View deliveries" menu item.
- Shows last 25 deliveries from `GET /org/webhooks/:id/deliveries`.
- Columns: Event, Status code (green ≥200 <300, red otherwise), Attempt count, Delivered at.
- Row expandable to show the full payload JSON in a `<pre>` block.

*Test webhook:*
- "Test" menu item calls `POST /org/webhooks/:id/test`.
- Show toast-style inline alert: "Test payload sent." or "Test failed: {error}".

*Active toggle:*
- Switch in the table row calls `PATCH /org/webhooks/:id` with `{ active: !current }`.

API functions to add in `frontend/admin/src/api.ts`:
```ts
listWebhooks(orgId)                                 // GET /org/webhooks
createWebhook(orgId, url, events)                   // POST /org/webhooks
getWebhook(orgId, id)                               // GET /org/webhooks/:id
updateWebhook(orgId, id, patch)                     // PATCH /org/webhooks/:id
deleteWebhook(orgId, id)                            // DELETE /org/webhooks/:id
testWebhook(orgId, id)                              // POST /org/webhooks/:id/test
listWebhookDeliveries(orgId, id)                    // GET /org/webhooks/:id/deliveries
```

Note: `orgId` is extracted from `useOrgContext()` — pass it into API calls, not hardcoded.

---

## C — Integration & Developer Experience

---

### C1. OpenAPI / Swagger Specification

**Backend:** ✅ Done — Swashbuckle configured, exposed on admin port only at `/swagger`.

**Frontend:** No changes needed.

---

### C2. Account Linking (Social Provider Linking Post-Login)

**Backend:** ✅ Done — `GET /account/social-accounts`, `DELETE /account/social-accounts/:id`, `GET /auth/oauth2/link/start`.

**Frontend — `frontend/admin/src/pages/account/AccountPage.tsx` (Security tab)**

The Security tab currently has password change. Add a "Linked Accounts" section below it.

Section structure:
1. **Heading:** "Linked Accounts" with `<Separator />` above it.
2. **Load on tab open:** call `GET /account/social-accounts` → list `{ id, provider, email, linked_at }`.
3. **For each linked account:** show a row with provider icon (reuse the social provider SVG icons from the login SPA or inline SVGs), provider name, the linked email as muted text, linked date, and an "Unlink" button.
4. **Unlink handler:**
   - Calls `DELETE /account/social-accounts/:id`.
   - On `res.error === "cannot_remove_last_auth_method"`: show error alert "Cannot unlink — this is your only login method. Set a password first."
   - On success: refresh the list.
5. **Connect new provider** — below the list, show "Connect a provider" with icon buttons for each provider that is NOT yet linked. Clicking opens the OAuth2 link flow:
   ```tsx
   window.location.href = `/auth/oauth2/link/start?provider=${provider}`;
   ```
   After the flow completes, the user is redirected back to `/account` — the list auto-refreshes on mount.
6. The list of available providers comes from the project config already loaded in the page (it's in `accountInfo.project_id` — fetch `/project/info` with that ID to get the enabled providers list, or simply hardcode the four standard ones: google, github, gitlab, facebook).

API function to add in `frontend/admin/src/api.ts`:
```ts
getSocialAccounts()                                 // GET /account/social-accounts
unlinkSocialAccount(id)                             // DELETE /account/social-accounts/:id
```

---

### C3. Session Visibility for Admins

**Backend:** ✅ Done — `GET /admin/users/:id/sessions`, `GET /org/userlists/:lid/users/:uid/sessions`, `DELETE` variants for revoke.

**Frontend — `frontend/admin/src/components/UserListMembersPanel.tsx`**

Add a "Sessions" option to the 3-dot dropdown menu for each user row:

```tsx
<DropdownMenuItem onClick={() => openSessionsDialog(user)}>
  View sessions
</DropdownMenuItem>
```

Sessions dialog (controlled by `sessionsUser` state + Dialog component):
- Title: "Active sessions — {user.email}"
- On open: call `getUserSessions(listId, user.id)` (API function below).
- Table columns: App name (`client_name`), Granted at, Expires at, "Revoke" button.
- "Revoke all" button at the bottom of the dialog → calls `revokeAllUserSessions(listId, user.id)` → refreshes the session list → show "All sessions revoked."
- Per-session revoke: calls `revokeUserSession(listId, user.id, clientId)`.
- Empty state: "No active sessions."

API functions to add in `frontend/admin/src/api.ts`:
```ts
// listId = null → system-level (/admin/users/:uid/sessions)
getUserSessions(listId: string | null, userId: string)
revokeUserSession(listId: string | null, userId: string, clientId: string)
revokeAllUserSessions(listId: string | null, userId: string)
```

---

### C4. Prometheus Metrics Endpoint

**Backend:** ✅ Done — `/metrics` on admin port, `UseHttpMetrics()`.

**Frontend:** No changes needed.

---

### C5. Breach Password Check (HaveIBeenPwned)

**Backend:** ✅ Done — `BreachCheckService`, `Project.CheckBreachedPasswords`, integrated into register/password-change/invite-complete.

**Frontend — Part 1: Admin SPA — `Authentication.tsx` (Registration tab)**

After the password policy section, add a Switch row:

```tsx
<div className="flex items-center justify-between">
  <div>
    <p className="font-medium text-sm">Reject breached passwords</p>
    <p className="text-xs text-muted-foreground">
      Passwords found in known data breaches are rejected at registration and password change.
      Uses the HaveIBeenPwned k-anonymity API — no password is ever transmitted.
    </p>
  </div>
  <Switch
    checked={form.check_breached_passwords ?? false}
    onCheckedChange={(v) => setForm(f => ({ ...f, check_breached_passwords: v }))}
  />
</div>
```

Include `check_breached_passwords` in the `updateProject()` save payload. The field is returned by `GET /project/info`.

---

**Frontend — Part 2: Login SPA — `Register.tsx`**

In the registration submit handler, add a case for `password_breached` in the error check:

```tsx
if (res.error === 'password_breached') {
  setError(`This password has been found in ${res.count?.toLocaleString() ?? 'multiple'} data breaches. Please choose a different password.`);
  return;
}
```

---

**Frontend — Part 3: Login SPA — `PasswordReset.tsx`**

In the password confirm handler (the step where the new password is submitted), add the same check:

```tsx
if (res.error === 'password_breached') {
  setError(`This password has been found in ${res.count?.toLocaleString() ?? 'multiple'} data breaches. Please choose a different password.`);
  return;
}
```

---

**Frontend — Part 4: Login SPA — `SetPassword.tsx` (invite completion)**

Already specified in B1 above — the `password_breached` error case is included there.

---

### C6. IP Allowlist Per Project

**Backend:** ✅ Done — `Project.IpAllowlist`, enforced in `POST /auth/login`.

**Frontend — `frontend/admin/src/pages/project/Authentication.tsx`**

Add a new **"Security"** tab to the Tabs component (after "Verification", before a future SAML/enterprise tab).

Tab content — "IP Allowlist" section:

```tsx
<Card>
  <CardHeader>
    <CardTitle>IP Allowlist</CardTitle>
    <CardDescription>
      Restrict logins to specific IP ranges. Leave empty to allow all IPs.
      Enter one CIDR range per line (e.g. 10.0.0.0/8, 192.168.1.0/24).
    </CardDescription>
  </CardHeader>
  <CardContent className="space-y-3">
    <Textarea
      value={ipAllowlist}
      onChange={(e) => setIpAllowlist(e.target.value)}
      placeholder={"10.0.0.0/8\n192.168.1.0/24"}
      rows={5}
      className="font-mono text-sm"
    />
    {ipAllowlistError && (
      <Alert variant="destructive"><AlertDescription>{ipAllowlistError}</AlertDescription></Alert>
    )}
    <p className="text-xs text-muted-foreground text-amber-600">
      ⚠ If you misconfigure this, you will lock yourself out. Verify your IP before saving.
    </p>
  </CardContent>
</Card>
```

State: `ipAllowlist` (string — newline-joined from the array), `ipAllowlistError`.

Client-side validation on save: split by newline, trim each, skip empty lines, validate each entry matches `/^(\d{1,3}\.){3}\d{1,3}(\/\d{1,2})?$|^[0-9a-fA-F:]+\/\d{1,3}$/` — show error if any entry is invalid.

Save: convert to string array, include `ip_allowlist` in the `updateProject()` payload. Load: join the array with `\n` into the textarea value.

---

### C7. Custom OAuth2 Scopes Per Project

**Backend:** ✅ Done — `Project.AllowedScopes`, `PUT /project/scopes`, Hydra client updated on scope change.

**Frontend — `frontend/admin/src/pages/project/Authentication.tsx` (Providers tab)**

Add a "Custom Scopes" section at the bottom of the Providers tab, after all provider configurations.

```tsx
<Card>
  <CardHeader>
    <CardTitle>OAuth2 Scopes</CardTitle>
    <CardDescription>
      Define custom scopes available to this project's OAuth2 clients.
      The built-in scopes <code>openid</code> and <code>offline</code> are always included.
    </CardDescription>
  </CardHeader>
  <CardContent className="space-y-3">
    {/* Non-removable built-in pills */}
    <div className="flex flex-wrap gap-2">
      <Badge variant="secondary">openid</Badge>
      <Badge variant="secondary">offline</Badge>
      {customScopes.map(scope => (
        <Badge key={scope} variant="outline" className="gap-1">
          {scope}
          <button onClick={() => removeScope(scope)} className="ml-1 hover:text-destructive">×</button>
        </Badge>
      ))}
    </div>
    {/* Add scope input */}
    <div className="flex gap-2">
      <Input
        value={newScope}
        onChange={(e) => setNewScope(e.target.value.toLowerCase().replace(/[^a-z0-9:_-]/g, ''))}
        placeholder="read:orders"
        className="font-mono"
        onKeyDown={(e) => e.key === 'Enter' && addScope()}
      />
      <Button variant="outline" onClick={addScope}>Add</Button>
    </div>
    {scopeError && <p className="text-xs text-destructive">{scopeError}</p>}
  </CardContent>
</Card>
```

State: `customScopes: string[]` (loaded from project's `allowed_scopes`, minus `openid`/`offline`).

Validation: scope must match `/^[a-z][a-z0-9:_-]*$/` and not duplicate an existing one.

Save: call `PUT /project/scopes` with `{ scopes: ['openid', 'offline', ...customScopes] }` when the tab's save button is clicked. Alternatively, include in the main `updateProject()` call as `allowed_scopes`.

API function to add in `frontend/admin/src/api.ts`:
```ts
updateProjectScopes(projectId: string, scopes: string[])  // PUT /project/scopes
```

---

## D — Operational Excellence

---

### D1. Audit Log Retention Policy

**Backend:** ✅ Done — `Organisation.AuditRetentionDays`, `AuditLogRetentionService` background service.

**Frontend — new `frontend/admin/src/pages/org/OrgSettings.tsx`**

This is a new org-level settings page (no equivalent currently exists for org admins).

Route to add in `frontend/admin/src/App.tsx`:
```tsx
// Under org routes:
<Route path="settings" element={<OrgSettings />} />

// Under system org detail routes (super_admin browsing an org):
<Route path="settings" element={<OrgSettings />} />
```

Sidebar addition in `frontend/admin/src/components/Sidebar.tsx`:
- Org section: add "Settings" nav item linking to `${orgBase}/settings` (use a `Settings` icon from lucide-react).
- System org section: add the same link.

Page structure — `OrgSettings.tsx`:

```tsx
<Card>
  <CardHeader>
    <CardTitle>Audit Log Retention</CardTitle>
    <CardDescription>
      Audit logs older than the retention period are automatically deleted.
      Set to "Forever" to disable automatic deletion.
    </CardDescription>
  </CardHeader>
  <CardContent>
    <Select value={String(retentionDays ?? '')} onValueChange={...}>
      <SelectTrigger className="w-48">
        <SelectValue placeholder="Select period" />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value="30">30 days</SelectItem>
        <SelectItem value="60">60 days</SelectItem>
        <SelectItem value="90">90 days</SelectItem>
        <SelectItem value="180">180 days</SelectItem>
        <SelectItem value="365">1 year</SelectItem>
        <SelectItem value="">Forever</SelectItem>
      </SelectContent>
    </Select>
  </CardContent>
  <CardFooter>
    <Button onClick={save} disabled={saving}>{saving ? 'Saving…' : 'Save'}</Button>
    {saved && <span className="ml-3 text-sm text-green-600">Saved!</span>}
  </CardFooter>
</Card>
```

Load: `GET /org/info` (already exists, returns `audit_retention_days`).
Save: `PATCH /org/info` with `{ audit_retention_days: retentionDays }` (or `null` for Forever).

API function to verify/add in `frontend/admin/src/api.ts`:
```ts
getOrgInfo()                                        // GET /org/info — verify returns audit_retention_days
updateOrgInfo(patch)                                // PATCH /org/info — verify accepts audit_retention_days
```

For super_admin context, `useOrgContext()` provides the org-scoped API paths.

---

### D2. Data Export

**Backend:** ✅ Done — `GET /org/userlists/:id/export?format=csv`, `GET /admin/organizations/:id/export/audit-log?format=csv`.

**Frontend — `frontend/admin/src/pages/org/OrgAuditLog.tsx` and `frontend/admin/src/pages/system/AuditLog.tsx`**

Add an export button in the top-right of the audit log Card header, next to any existing controls:

```tsx
<Button variant="outline" size="sm" onClick={handleExport} disabled={exporting}>
  <Download className="h-4 w-4 mr-2" />
  {exporting ? 'Exporting…' : 'Export CSV'}
</Button>
```

Handler — trigger a browser download (not a fetch-to-state, since the backend streams CSV):
```ts
async function handleExport() {
  setExporting(true);
  const token = getAccessToken(); // from auth.ts
  const url = orgId
    ? `/admin/organizations/${orgId}/export/audit-log?format=csv`
    : `/admin/organizations/export/audit-log?format=csv`; // adjust to actual endpoint
  const res = await fetch(url, { headers: { Authorization: `Bearer ${token}` } });
  const blob = await res.blob();
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `audit-log-${new Date().toISOString().slice(0,10)}.csv`;
  a.click();
  setExporting(false);
}
```

Add date-range inputs (optional, low priority): two date inputs (`from`, `to`) that append `&from=<ISO>&to=<ISO>` to the export URL when set.

---

**Frontend — `frontend/admin/src/pages/UserListDetail.tsx`**

Add the same Export CSV button to the UserListDetail card header:

```tsx
<Button variant="outline" size="sm" onClick={handleExport} disabled={exporting}>
  <Download className="h-4 w-4 mr-2" />
  Export CSV
</Button>
```

Handler calls `GET /org/userlists/:id/export?format=csv` and triggers download the same way as above.

API function to add in `frontend/admin/src/api.ts` (returns a Blob, not JSON):
```ts
export async function exportUserList(listId: string, format = 'csv'): Promise<Blob> {
  const res = await apiFetch(`/org/userlists/${listId}/export?format=${format}`, {}, true /* raw */);
  return res.blob();
}
```
(Requires a minor `apiFetch` wrapper extension to support returning raw `Response` — or use `fetch` directly with the Bearer token.)

---

## E — Enterprise Features

---

### E1. SAML 2.0 Enterprise Federation

**Backend:** ✅ Done — `SamlIdpConfig` entity, `/auth/saml/start`, `/auth/saml/acs`, `/admin/projects/:id/saml/metadata`, `/admin/projects/:id/saml-providers` CRUD.

**Frontend — `frontend/admin/src/pages/project/Authentication.tsx` (Providers tab)**

Add a "SAML 2.0" section at the bottom of the Providers tab, below custom OIDC and custom scopes.

Section structure:

```tsx
<Card>
  <CardHeader className="flex flex-row items-center justify-between">
    <div>
      <CardTitle>SAML 2.0 Identity Providers</CardTitle>
      <CardDescription>
        Allow users to log in via a corporate IdP (Okta, Azure AD, ADFS).
      </CardDescription>
    </div>
    <Button size="sm" onClick={() => setAddSamlOpen(true)}>+ Add IdP</Button>
  </CardHeader>
  <CardContent>
    {/* List of configured IdPs */}
    {samlProviders.map(idp => (
      <div key={idp.id} className="flex items-center justify-between py-2 border-b last:border-0">
        <div>
          <p className="font-medium text-sm">{idp.entity_id}</p>
          <p className="text-xs text-muted-foreground">{idp.metadata_url ?? 'Manual config'}</p>
        </div>
        <div className="flex items-center gap-2">
          <Badge variant={idp.active ? 'default' : 'secondary'}>
            {idp.active ? 'Active' : 'Inactive'}
          </Badge>
          <Button variant="ghost" size="icon" onClick={() => deleteSamlProvider(idp.id)}>
            <Trash2 className="h-4 w-4" />
          </Button>
        </div>
      </div>
    ))}
    {samlProviders.length === 0 && (
      <p className="text-sm text-muted-foreground">No SAML providers configured.</p>
    )}
  </CardContent>
</Card>
```

Add SAML IdP dialog (`addSamlOpen` state):
- Fields: Entity ID (required text input), Metadata URL (optional, `https://` only), SSO URL (optional, shown when no metadata URL), Certificate PEM (optional, textarea), Email attribute name (default `email`), Display name attribute (optional), JIT provisioning (Switch, default on), Default role (Select from project roles).
- On save: call `POST /admin/projects/:id/saml-providers` with the form values.
- On success: refresh the providers list and show the SP metadata URL in a copy-friendly callout: "Give this URL to your IdP:" + copyable `GET /admin/projects/:id/saml/metadata` URL.

SP Metadata URL display — also show it as a persistent copyable field above the provider list, so the admin always has it:
```tsx
<div className="flex items-center gap-2 p-3 bg-muted rounded text-sm font-mono">
  <span className="truncate">{spMetadataUrl}</span>
  <Button variant="ghost" size="icon" onClick={() => navigator.clipboard.writeText(spMetadataUrl)}>
    <Copy className="h-4 w-4" />
  </Button>
</div>
```
Where `spMetadataUrl` = `${window.location.origin}/admin/projects/${projectId}/saml/metadata`.

Login SPA — `Login.tsx`:
- If the project theme contains SAML providers (check `theme.saml_providers?.length > 0`), render a "Sign in with [entity_id]" button for each active one.
- Click handler: `window.location.href = \`/auth/saml/start?project_id=${projectId}&idp_id=${idp.id}&login_challenge=${challenge}\``.
- Style consistent with the existing social provider buttons.

API functions to add in `frontend/admin/src/api.ts`:
```ts
listSamlProviders(projectId)                        // GET /admin/projects/:id/saml-providers
createSamlProvider(projectId, config)               // POST /admin/projects/:id/saml-providers
deleteSamlProvider(projectId, idpId)                // DELETE /admin/projects/:id/saml-providers/:idpId
```

---

### E2. Suspicious Login / New Device Detection

**Backend:** ✅ Done — device fingerprinting, `User.NewDeviceAlertsEnabled`, `PATCH /account/me` accepts `new_device_alerts_enabled`.

**Frontend — `frontend/admin/src/pages/account/AccountPage.tsx` (Profile tab)**

In the Profile tab, at the bottom (below display name), add a notification preference row:

```tsx
<div className="flex items-center justify-between pt-4 border-t">
  <div>
    <p className="font-medium text-sm">New device login alerts</p>
    <p className="text-xs text-muted-foreground">
      Receive an email when you log in from a device or location not seen in the last 90 days.
    </p>
  </div>
  <Switch
    checked={newDeviceAlertsEnabled}
    onCheckedChange={handleToggleNewDeviceAlerts}
  />
</div>
```

Handler:
```tsx
async function handleToggleNewDeviceAlerts(value: boolean) {
  setNewDeviceAlertsEnabled(value);
  await updateMe({ new_device_alerts_enabled: value });
}
```

Load the current value from `GET /account/me` which already returns `new_device_alerts_enabled`. The field is already part of the `accountInfo` loaded on page mount.

No new API function needed — `updateMe()` already calls `PATCH /account/me` and is used by the display name field.

---

## Summary Table

| ID  | Item                                 | Priority | Backend | Frontend |
|-----|--------------------------------------|----------|---------|----------|
| A1  | Encrypt social login client_secrets  | 🔴 CRIT  | ✅      | ✎ Authentication.tsx — providers secret placeholder |
| A2  | Security headers middleware           | 🔴 HIGH  | ✅      | —        |
| A3  | Rate limit registration endpoints     | 🔴 HIGH  | ✅      | —        |
| A4  | Constant-time OTP comparison          | 🟡 MED   | ✅      | —        |
| A5  | Email enumeration fix                 | 🟡 MED   | ✅      | —        |
| B1  | User invitation flow                  | 🔴 HIGH  | ✅      | ✎ Login SPA: SetPassword.tsx (new) · Admin SPA: UserListMembersPanel badge + resend |
| B2  | Admin account unlock                  | 🔴 HIGH  | ✅      | ✎ UserListMembersPanel: locked badge + unlock button |
| B3  | Mandatory MFA per project             | 🔴 HIGH  | ✅      | ✎ Authentication.tsx toggle · Login SPA: Login.tsx + MfaSetup.tsx (new) |
| B4  | System service accounts CRUD + PATs   | 🔴 HIGH  | ✅      | ✎ Verify/complete SystemServiceAccounts.tsx + ServiceAccountDetail.tsx |
| B5  | Webhooks                              | 🔴 HIGH  | ✅      | ✎ OrgWebhooks.tsx (new) + routing + sidebar |
| C1  | OpenAPI / Swagger                     | 🟡 MED   | ✅      | —        |
| C2  | Account linking (social providers)    | 🟡 MED   | ✅      | ✎ AccountPage.tsx Security tab: Linked Accounts section |
| C3  | Session visibility for admins         | 🟡 MED   | ✅      | ✎ UserListMembersPanel: Sessions dialog |
| C4  | Prometheus metrics endpoint           | 🟡 MED   | ✅      | —        |
| C5  | Breach password check (HIBP)          | 🟡 MED   | ✅      | ✎ Authentication.tsx toggle · Login SPA: Register.tsx + PasswordReset.tsx error handling |
| C6  | IP allowlist per project              | 🟡 MED   | ✅      | ✎ Authentication.tsx: new Security tab with CIDR textarea |
| C7  | Custom OAuth2 scopes per project      | 🟡 MED   | ✅      | ✎ Authentication.tsx Providers tab: custom scopes tag input |
| D1  | Audit log retention policy            | 🟡 MED   | ✅      | ✎ OrgSettings.tsx (new) + routing + sidebar |
| D2  | Data export (CSV/JSON)                | 🟢 LOW   | ✅      | ✎ OrgAuditLog.tsx + AuditLog.tsx + UserListDetail.tsx: export buttons |
| E1  | SAML 2.0 enterprise federation        | 🟢 LOW   | ✅      | ✎ Authentication.tsx Providers tab: SAML section · Login SPA: Login.tsx SAML buttons |
| E2  | Suspicious login / new device alert   | 🟢 LOW   | ✅      | ✎ AccountPage.tsx Profile tab: toggle |

---

## Frontend Execution Order

### Phase 1 — Unblock end-users (Login SPA, no admin dependency)
1. **B1** — `SetPassword.tsx` (new page) + route + `api.ts` addition
2. **B3** — `Login.tsx` modification + `MfaSetup.tsx` (new page) + route + `api.ts` additions
3. **C5** — `Register.tsx` + `PasswordReset.tsx` breach error handling (3-line additions each)

### Phase 2 — Admin SPA user management (single component, all contexts)
4. **B1** — `UserListMembersPanel.tsx`: invite pending badge + resend invite menu item
5. **B2** — `UserListMembersPanel.tsx`: locked badge + unlock menu item
6. **C3** — `UserListMembersPanel.tsx`: sessions menu item + sessions dialog
7. `api.ts`: `resendInvite`, `unlockUser`, `getUserSessions`, `revokeUserSession`, `revokeAllUserSessions`

### Phase 3 — Project auth settings (Authentication.tsx)
8. **A1** — providers secret placeholder + change detection
9. **B3** — Require MFA toggle (Registration tab)
10. **C5** — Breach check toggle (Registration tab)
11. **C6** — IP allowlist (new Security tab)
12. **C7** — Custom scopes section (Providers tab)
13. **E1** — SAML section (Providers tab) + Login.tsx SAML buttons
14. `api.ts`: `listSamlProviders`, `createSamlProvider`, `deleteSamlProvider`, `updateProjectScopes`

### Phase 4 — Account page (AccountPage.tsx)
15. **C2** — Linked accounts section (Security tab)
16. **E2** — New device alerts toggle (Profile tab)
17. `api.ts`: `getSocialAccounts`, `unlinkSocialAccount`

### Phase 5 — New admin pages
18. **B5** — `OrgWebhooks.tsx` + `App.tsx` route + sidebar link + `api.ts` webhook functions
19. **D1** — `OrgSettings.tsx` + `App.tsx` route + sidebar link
20. **D2** — Export buttons on `OrgAuditLog.tsx`, `AuditLog.tsx`, `UserListDetail.tsx`

### Phase 6 — Verify existing pages
21. **B4** — Audit `SystemServiceAccounts.tsx` + `ServiceAccountDetail.tsx` against spec above
