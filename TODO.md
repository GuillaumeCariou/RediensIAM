# RediensIAM — Feature Completion TODO

> Generated 2026-03-29. Based on a full codebase scan.
>
> **Confirmed already implemented** (removed from list after verification):
> - Password policy per project ✅ (min length, uppercase, lowercase, digit, special — backend enforced in AuthController + ProjectController, frontend in Authentication.tsx Registration tab)
> - Social login provider config per project ✅ (Google, GitHub, GitLab, Facebook, custom OIDC — full providers tab in Authentication.tsx, SocialLoginService reads from login_theme JSONB)
> - Account lockout ✅ (FailedLoginCount + LockedUntil with configurable MaxLoginAttempts + LockoutMinutes, resets on password reset)
> - Org self-registration ✅ (by design — orgs are admin-created only, not a gap)

---

## A — Security Vulnerabilities (fix before production)

These are not feature gaps — they are security issues that could lead to data breaches or account compromise.

---

### A1. Social Login Client Secrets Stored in Plaintext

**Severity:** HIGH — DB compromise leaks all tenant OAuth2 client secrets

**What:** `Project.LoginTheme` JSONB stores `providers[].client_secret` (Google, GitHub, GitLab, Facebook, custom OIDC) in plaintext. An attacker with read access to the `projects` table gets all OAuth2 client secrets, which can be used to impersonate tenants on those providers and steal users' linked accounts.

**Backend — `src/Controllers/ProjectController.cs` and `src/Controllers/OrgController.cs`:**
- Before saving `login_theme` to the DB, walk `providers[]` and encrypt every non-null `client_secret` using `TotpEncryption.Encrypt` (same AES-256-GCM key as SMTP passwords).
- Store as `"client_secret_enc": "<hex>"` and clear the `client_secret` field.
- When reading back from DB (GET /project/info, /org/projects/:id), decrypt for internal use (e.g. `SocialLoginService`) but **never** include `client_secret` or `client_secret_enc` in the API response. Return `"client_secret": null` so the frontend knows a secret is saved.
- Add a helper method: `EncryptProviderSecrets(JsonNode providers, byte[] key)` / `DecryptProviderSecrets(...)`.
- Same treatment in `SystemAdminController` for all project-level patch endpoints.
- `SocialLoginService.GetProviderConfigAsync` must decrypt before using the secret.

**Frontend — `Authentication.tsx` (Providers tab):**
- The `client_secret` inputs already use `type="password"`. When the API returns `client_secret: null` for an existing provider, display `"••••••••• (saved)"` as placeholder and only send the secret if the field was changed (non-empty value in form).
- Add a "Secret saved — enter a new one to replace" indicator.

---

### A2. No Security Headers Middleware

**Severity:** MEDIUM — Clickjacking, MIME-sniffing, referrer leaks on login pages

**What:** The middleware pipeline has no HTTP security headers. The login SPA is particularly exposed because it handles credentials.

**Backend — `src/Program.cs` or a new `SecurityHeadersMiddleware`:**
Add before `app.UseStaticFiles()`:
```csharp
app.Use(async (ctx, next) => {
    ctx.Response.Headers["X-Content-Type-Options"]  = "nosniff";
    ctx.Response.Headers["X-Frame-Options"]         = "DENY";
    ctx.Response.Headers["Referrer-Policy"]         = "strict-origin-when-cross-origin";
    ctx.Response.Headers["X-XSS-Protection"]        = "0"; // modern browsers use CSP
    ctx.Response.Headers["Permissions-Policy"]      = "geolocation=(), camera=(), microphone=()";
    // CSP for login SPA — tighten as needed
    if (!ctx.Request.Path.StartsWithSegments("/admin"))
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: https:; frame-ancestors 'none';";
    await next();
});
```
- Do **not** set `X-Frame-Options: DENY` on the `/preview` route (the admin SPA loads it in an iframe).

**Frontend:** No changes needed.

---

### A3. Registration Endpoint Not Rate Limited

**Severity:** MEDIUM — Mass account creation, email spam via OTP, enumeration

**What:** `POST /auth/register` and `POST /auth/password-reset/request` do not go through `LoginRateLimiter`. An attacker can flood registration or trigger thousands of password reset emails.

**Backend — `src/Controllers/AuthController.cs`:**
- At the start of `Register()`: call `await rateLimiter.IsBlockedAsync(Ip, null)` and return `429` if blocked. Call `RecordFailureAsync` if registration fails (invalid domain, duplicate email).
- At the start of `RequestPasswordReset()`: same IP-based rate check.
- Consider a separate Redis key prefix for registration attempts so it doesn't share the login lockout bucket.

**Frontend:** No changes needed. The 429 response should be surfaced as "Too many attempts, try again later."

---

### A4. OTP Comparison Not Constant-Time

**Severity:** LOW-MEDIUM — Theoretical timing attack on OTP codes

**What:** OTP codes are compared with `==` string equality in `OtpCacheService`. Remote timing attacks on OTPs over HTTPS are extremely difficult in practice, but should be fixed for defence-in-depth.

**Backend — `src/Services/OtpCacheService.cs`:**
Replace `cached == code` with:
```csharp
CryptographicOperations.FixedTimeEquals(
    System.Text.Encoding.UTF8.GetBytes(cached),
    System.Text.Encoding.UTF8.GetBytes(code));
```

---

### A5. Email Enumeration via Timing Difference

**Severity:** LOW — Allows attackers to discover which emails are registered

**What:** `POST /auth/password-reset/request` may respond faster when the email doesn't exist (short-circuits without hitting the email service). An attacker timing responses can enumerate registered emails.

**Backend — `src/Controllers/AuthController.cs` (`RequestPasswordReset`):**
- Always await the same set of operations regardless of whether the user is found.
- Use a random artificial delay or ensure both code paths take equal time.
- The endpoint should return `200 OK` with the same body (`"message": "if_registered_email_sent"`) whether the user exists or not — never reveal which branch was taken.

---

## B — Core Missing Features (High Priority)

---

### B1. User Invitation Flow

**What:** When an admin creates a user via `POST /org/userlists/{id}/users`, the user is created but has no mechanism to set a password. They can't log in. There is no email invite → set-password token flow.

**Backend — `src/Controllers`:**

1. **Invite token type:** Add `"invite"` as a valid `Kind` in `EmailToken`. It must be single-use and expire (72 hours recommended).

2. **Trigger on user creation:** In `OrgController.AddUserToList()` and `SystemAdminController.AddUserToList()`:
   - If the user is newly created and has no password (or a placeholder hash), generate an invite token.
   - Store `EmailToken { Kind = "invite", UserId = ..., TokenHash = SHA256(token), ExpiresAt = now + 72h }`.
   - Send via `IEmailService.SendInviteAsync(email, inviteUrl, orgName)` — add this method to the interface.
   - Mark user with `Active = false` until invite is accepted.

3. **New endpoint** `POST /auth/invite/complete`:
   ```
   Body: { "token": "...", "password": "..." }
   ```
   - Validate token (hash lookup, not expired, not used).
   - Enforce project password policy (look up project via UserList → Project FK).
   - Set `User.PasswordHash`, `User.Active = true`, `User.EmailVerified = true`.
   - Mark token `UsedAt = now()`.
   - Complete the Hydra login if a `login_challenge` was passed (for inline onboarding).
   - Return `200 OK`.

4. **Resend invite:** `POST /org/userlists/{id}/users/{uid}/resend-invite` — invalidates previous invite tokens, generates a new one, re-sends email.

5. **Expose invite status:** Add `invite_pending: bool` field to user responses (true when user has an unused invite token).

**Backend — `src/Services/NotificationService.cs`:**
- Add `SendInviteAsync(string to, string inviteUrl, string orgName, Guid? projectId)` to `IEmailService`.
- Implement in `SmtpEmailService` with a proper invite email template.

**Frontend Admin SPA:**

- **`UserListDetail.tsx` / `ProjectUsers.tsx`:** Show a `Badge variant="outline"` "Invite pending" next to users with `invite_pending: true`. Add a "Resend invite" button (three-dot menu or direct).
- **Error handling:** If `resend-invite` fails (user already active), show toast "User has already accepted the invitation."

**Frontend Login SPA:**
- New page `SetPasswordPage` (similar to `PasswordReset.tsx` but reads `?invite_token=` from URL).
- Show password policy requirements (loaded via `/auth/login/theme?project_id=`).
- On submit, call `POST /auth/invite/complete`.
- On success, redirect to the project login page.

---

### B2. Admin Account Unlock

**What:** When a user is locked (`LockedUntil > now`), the only way to unlock them is via self-service password reset. Admins have no direct unlock action. The `locked_until` and `failed_login_count` fields are already returned in user responses.

**Backend — new endpoints:**

`POST /admin/users/{id}/unlock`:
```csharp
user.LockedUntil = null;
user.FailedLoginCount = 0;
await auditLog.LogAsync("user.unlocked", ...);
await db.SaveChangesAsync();
```

`POST /org/userlists/{id}/users/{uid}/unlock` — same, accessible to org admins.

Both must validate the caller has management rights over that user.

**Frontend Admin SPA:**
- In user detail views (system and org level): when `locked_until` is set and in the future, show an amber "Account locked" badge + unlock timestamp.
- Add an "Unlock Account" button that calls the unlock endpoint. Show confirmation toast on success.
- In user list tables: consider showing a lock icon next to locked users for quick visibility.

---

### B3. Mandatory MFA Enforcement Per Project

**What:** A project admin can require all users to have at least one MFA method before they can complete a login. Currently, MFA is always optional — there is no per-project `require_mfa` flag.

**Backend:**

1. **Entity:** Add `RequireMfa bool` to `Project` entity. Add raw SQL to `Program.cs` startup block:
   ```sql
   ALTER TABLE projects ADD COLUMN IF NOT EXISTS "RequireMfa" BOOLEAN NOT NULL DEFAULT false;
   ```

2. **`ProjectController` + `SystemAdminController`:** Expose `RequireMfa` in PATCH endpoints and GET responses.

3. **`AuthController` — `POST /auth/login`:** After password validation succeeds (and before existing MFA check), add:
   ```csharp
   bool hasMfa = user.TotpEnabled || user.PhoneVerified || (await db.WebAuthnCredentials.AnyAsync(w => w.UserId == user.Id));
   if (project.RequireMfa && !hasMfa) {
       HttpContext.Session.SetString("mfa_setup_required", "true");
       HttpContext.Session.SetString("mfa_pending_user", user.Id.ToString());
       HttpContext.Session.SetString("mfa_pending_project", projectId);
       return Ok(new { requires_mfa_setup = true });
   }
   ```
4. **Resume login after MFA setup:** After `POST /account/mfa/totp/confirm` or `POST /account/mfa/webauthn/register/complete`, check if `mfa_setup_required` is in session. If yes, complete the Hydra accept login flow.

**Frontend — `Authentication.tsx` (Registration tab):**
- Add a "Require MFA" toggle below "Allow self-registration".
- Description: "Users without a second factor cannot complete login until they enroll."

**Frontend Login SPA:**
- Handle the new `requires_mfa_setup: true` response from the login endpoint.
- Redirect user to an MFA setup wizard (subset of the account MFA setup pages, but inline in the login flow).
- After setup is complete, resume the Hydra login flow.

---

### B4. System Service Accounts (CRUD + PAT + Roles)

**What:** System-level service accounts exist in the DB (root UserList with `OrgId = null`) and are listed by `GET /admin/service-accounts`, but there are no endpoints to create, delete, manage PATs, or assign roles for them. The admin SPA has no detail page for system SAs.

**Backend — `src/Controllers/SystemAdminController.cs`:**

1. **`POST /admin/service-accounts`** — create SA in the root UserList (`GetOrCreateRootListAsync` helper). Required fields: `name`, optional `description`. Return `{ id, name, description }`.

2. **`GET /admin/service-accounts/{id}`** — get SA with PAT list and assigned roles. Filter: SA's UserList must have `OrgId == null`.

3. **`DELETE /admin/service-accounts/{id}`** — delete SA (cascade removes PATs via EF config). Audit log.

4. **PAT endpoints:**
   - `POST /admin/service-accounts/{id}/pat` — generate PAT. Token format: `rediens_pat_<32 random bytes hex>`. Store SHA-256 hash only. Return raw token **once**.
   - `GET /admin/service-accounts/{id}/pat` — list PATs (name + expiry only, never hash or raw token).
   - `DELETE /admin/service-accounts/{id}/pat/{patId}` — revoke.

5. **Role endpoints:**
   - Before implementing: read `src/Data/Entities/OrgRole.cs` and `RediensIamDbContext.cs` to check whether `OrgRole.UserId` has a FK constraint to `users` only. If yes, a `ServiceAccountOrgRole` table is needed. If no FK, reuse `OrgRole` with `UserId = sa.Id` and `OrgId = null`.
   - `POST /admin/service-accounts/{id}/roles` — assign role (`super_admin` is the only valid option for system SAs, `OrgId = null`).
   - `DELETE /admin/service-accounts/{id}/roles/{roleId}` — revoke.
   - `GET /admin/service-accounts/{id}/roles` — list.

6. **PAT introspection** — verify `src/Controllers/InternalController.cs` correctly resolves system SA PATs and returns `org_id: null`, `project_id: null`, `roles: ["super_admin"]`, `is_service_account: true`. Fix if it doesn't.

**Frontend — `src/pages/system/SystemServiceAccounts.tsx` (rewrite):**
- Table: Name, Description, Status, Last used.
- `[+ New Service Account]` button → dialog.
- Row click → navigate to `/system/service-accounts/:id`.

**Frontend — new `src/pages/system/SystemServiceAccountDetail.tsx`:**
- SA info card (name, status, created_at) + disable/enable toggle + delete.
- "Assigned Roles" section: table + assign/revoke (`super_admin` only).
- "Personal Access Tokens" section: table + generate PAT dialog (show raw token once with copy button + warning) + revoke.

**Frontend — `App.tsx`:**
- Add `<Route path="system/service-accounts/:id" element={<SystemServiceAccountDetail />} />`.

---

### B5. Webhooks

**What:** External systems need to react to IAM events (user created, role assigned, session revoked, etc.) without polling. Webhooks deliver signed HTTP POST payloads to a configured URL.

**Events to support:** `user.created`, `user.updated`, `user.deleted`, `user.locked`, `user.login.success`, `user.login.failure`, `role.assigned`, `role.revoked`, `session.revoked`, `project.updated`

**Backend — new entities:**

`src/Data/Entities/Webhook.cs`:
```csharp
public class Webhook {
    public Guid Id { get; set; }
    public Guid? OrgId { get; set; }      // null = system-level
    public Guid? ProjectId { get; set; }  // null = org-level
    public string Url { get; set; } = "";
    public string SecretEnc { get; set; } = ""; // AES-256-GCM encrypted
    public string[] Events { get; set; } = [];   // JSONB
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
```

`src/Data/Entities/WebhookDelivery.cs`:
```csharp
public class WebhookDelivery {
    public Guid Id { get; set; }
    public Guid WebhookId { get; set; }
    public string Event { get; set; } = "";
    public string Payload { get; set; } = "";   // JSONB
    public int? StatusCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

**Backend — `src/Services/WebhookService.cs`:**
- `DispatchAsync(string eventType, object payload, Guid? orgId, Guid? projectId)` — finds matching active webhooks, signs payload with HMAC-SHA256, sends HTTP POST.
- Signature: `X-RediensIAM-Signature: sha256=<hex(HMAC-SHA256(secret, payload_bytes))>` — matches GitHub/Stripe convention.
- Retry: 3 attempts with exponential backoff (2s, 8s, 32s) using `IHostedService` or `BackgroundService` with a Redis queue. Do NOT retry synchronously in the request path.
- Log each attempt to `WebhookDelivery`.
- Timeout: 10 seconds per attempt. Do not let slow webhook targets block the IAM.

**Backend — API endpoints (`OrgController` + `ProjectController`):**
```
GET    /org/webhooks                  — list org webhooks
POST   /org/webhooks                  — create (validate URL is HTTPS)
GET    /org/webhooks/{id}             — get details + recent deliveries
DELETE /org/webhooks/{id}             — delete
POST   /org/webhooks/{id}/test        — send test payload
GET    /org/webhooks/{id}/deliveries  — list recent delivery attempts

GET    /project/webhooks              — list project webhooks
POST   /project/webhooks              — create
DELETE /project/webhooks/{id}         — delete
POST   /project/webhooks/{id}/test    — send test payload
```

**Security:** Validate that `url` is HTTPS only. Never expose the raw secret — show it exactly once on creation; store encrypted. Provide a "Rotate secret" endpoint.

**Backend — inject dispatch calls:**
- In `AuditLogService.LogAsync()`: after writing the audit record, call `WebhookService.DispatchAsync(...)` for supported event types.
- Keep the webhook dispatch async/fire-and-forget — it must not affect the response latency.

**Frontend Admin SPA — new pages:**

`OrgWebhooks.tsx`:
- List table: URL, events subscribed, active/inactive, last delivery status.
- Create dialog: URL input (HTTPS enforced client-side), event type checkboxes, secret shown once.
- Delivery log: accordion per webhook showing last 10 deliveries with status code, timestamp, payload preview.

`ProjectWebhooks.tsx` — same, project-scoped. Can be a tab in Project Settings.

Add to sidebar/routing:
- `/org/webhooks` → `OrgWebhooks`
- `/project/webhooks` → (tab in ProjectSettings or separate page)

---

## C — Integration & Developer Experience (Medium Priority)

---

### C1. OpenAPI / Swagger Specification

**What:** No machine-readable API spec exists. Required for generating SDKs, documenting the API for integrators, and Postman collections.

**Backend:**
1. Add NuGet: `Swashbuckle.AspNetCore`
2. In `Program.cs`:
   ```csharp
   builder.Services.AddEndpointsApiExplorer();
   builder.Services.AddSwaggerGen(c => {
       c.SwaggerDoc("v1", new() { Title = "RediensIAM API", Version = "v1" });
       c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { ... });
       c.AddSecurityRequirement(...);
       var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
       c.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFile));
   });
   // Expose only on admin port:
   app.UseWhen(ctx => ctx.Connection.LocalPort == appConfig.AdminPort, branch => {
       branch.UseSwagger();
       branch.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "RediensIAM v1"));
   });
   ```
3. Add `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to the `.csproj`.
4. Add `/// <summary>` XML doc comments to all controller action methods.
5. Add `[ProducesResponseType(200)]`, `[ProducesResponseType(401)]`, `[ProducesResponseType(403)]` attributes to key endpoints.

**Frontend:** None.

---

### C2. Account Linking (Social Provider Linking Post-Login)

**What:** `UserSocialAccount` entity exists and social login works, but a logged-in user cannot link an additional social provider to their account. If they registered with email/password, they can't add Google sign-in. `GET /account/mfa` shows MFA but not linked social accounts.

**Backend — `src/Controllers/AccountController.cs`:**

1. **`GET /account/social-accounts`** — returns list of `UserSocialAccount` records for current user (provider, linked_at, provider email — never access tokens).

2. **`DELETE /account/social-accounts/{id}`** — unlinks a provider.
   - Guard: if this is the user's only auth method (no password AND no other social account), refuse with `{ "error": "cannot_remove_last_auth_method" }`.

3. **Link flow** — reuse the existing `SocialLoginService` OAuth2 flow with a `link_mode` flag:
   - `GET /auth/oauth2/start?provider=google&link_mode=true` — set `link_mode` in the Redis state blob. User must be authenticated (check session cookie).
   - In `GET /auth/oauth2/callback`: if `state.link_mode == true` and user is authenticated, create `UserSocialAccount` instead of logging in. Return redirect to `/account`.

**Frontend — `AccountPage.tsx`:**
- Add "Linked Accounts" section listing connected providers (icon + provider name + "Unlink" button).
- Show "Connect [Google] [GitHub] [GitLab] [Facebook]" buttons for unlinked providers.
- Clicking "Connect" opens OAuth2 flow in current window (`window.location.href = /auth/oauth2/start?provider=X&link_mode=true`).

---

### C3. Session Visibility for Admins

**What:** `DELETE /admin/users/{id}/sessions` exists (force logout) but there is no `GET` counterpart — admins cannot see what sessions a user has active. Hydra has admin APIs to list consent sessions.

**Backend — `src/Controllers/SystemAdminController.cs`:**
```
GET /admin/users/{id}/sessions
```
- Call `GET {hydra-admin}/admin/oauth2/consent/sessions?subject={userId}` (Hydra admin API).
- Return: client name, client ID, granted scopes, created_at, last access.
- Similarly expose for org admins: `GET /org/userlists/{id}/users/{uid}/sessions`.

**Frontend Admin SPA:**
- In user detail views (system + org level): add "Active Sessions" tab/section.
- Table: App name, scopes granted, login date, "Revoke" button per session.
- "Revoke All" button.

---

### C4. Prometheus Metrics Endpoint

**What:** The `/admin/metrics` endpoint returns JSON stats for the admin SPA but there is no Prometheus-format scraping endpoint for infrastructure monitoring.

**Backend:**
1. Add NuGet: `prometheus-net.AspNetCore`
2. In `Program.cs`:
   ```csharp
   // Before MapControllers:
   app.UseHttpMetrics();

   // Restrict scrape endpoint to admin port:
   app.UseWhen(ctx => ctx.Connection.LocalPort == appConfig.AdminPort,
       branch => branch.MapMetrics("/metrics"));
   ```
3. Add custom IAM-specific metrics in relevant services:
   ```csharp
   // In AuthController:
   private static readonly Counter LoginAttempts = Metrics.CreateCounter(
       "iam_login_attempts_total", "Login attempts", new[] { "result", "project_id" });
   // result: success | failure | locked | mfa_required

   // In AuditLogService:
   private static readonly Counter AuditEvents = Metrics.CreateCounter(
       "iam_audit_events_total", "Audit events", new[] { "action" });

   // As gauges populated by /admin/metrics query:
   private static readonly Gauge RegisteredUsers = Metrics.CreateGauge(
       "iam_registered_users_total", "Total registered users");
   ```
4. **Helm** — add Prometheus scrape annotations to `templates/deployment.yaml`:
   ```yaml
   annotations:
     prometheus.io/scrape: "true"
     prometheus.io/port: "{{ .Values.app.adminPort }}"
     prometheus.io/path: "/metrics"
   ```

**Frontend:** None (ops tooling — Grafana reads from Prometheus).

---

### C5. Breach Password Check (HaveIBeenPwned)

**What:** Passwords are not checked against known breach databases at registration or change time. This is a recommended NIST 800-63B control.

**Implementation uses k-Anonymity** — only the first 5 characters of the SHA-1 hash are sent to HIBP, never the actual password.

**Backend — `src/Services/BreachCheckService.cs`:**
```csharp
public class BreachCheckService(IHttpClientFactory http) {
    public async Task<int> GetBreachCountAsync(string password) {
        var sha1 = SHA1.HashData(Encoding.UTF8.GetBytes(password));
        var hex = Convert.ToHexString(sha1);          // "3D4F2..."
        var prefix = hex[..5];                         // "3D4F2"
        var suffix = hex[5..].ToUpperInvariant();      // rest

        using var client = http.CreateClient();
        var resp = await client.GetStringAsync(
            $"https://api.pwnedpasswords.com/range/{prefix}");

        // Response: "SUFFIX:count\nSUFFIX:count\n..."
        foreach (var line in resp.Split('\n')) {
            var parts = line.Split(':');
            if (parts[0].TrimEnd() == suffix)
                return int.Parse(parts[1].Trim());
        }
        return 0; // not found
    }
}
```

**Backend — integrate into `AuthController` and `AccountController`:**
- On `POST /auth/register` and `POST /account/password` and `POST /auth/invite/complete`:
  - If `project.CheckBreachedPasswords == true`, call `BreachCheckService`.
  - Return `400 { "error": "password_breached", "count": 12345 }` if found in breaches.

**Backend — add `CheckBreachedPasswords bool` to `Project` entity.**

**Frontend — `Authentication.tsx` (Registration tab):**
- Toggle: "Reject passwords found in data breaches".
- Link to HIBP for user education.

**Frontend Login SPA:**
- Handle `password_breached` error code on register/reset forms with a meaningful message.

---

### C6. IP Allowlist Per Project

**What:** Organisations with corporate security requirements need to restrict project access to specific IP ranges (e.g. `10.0.0.0/8` for VPN-only apps).

**Backend:**
1. Add `IpAllowlist string[]` JSONB to `Project` entity. Add column via startup SQL.
2. In `AuthController POST /auth/login`:
   ```csharp
   if (project.IpAllowlist?.Length > 0) {
       var clientIp = IPAddress.Parse(Ip);
       bool allowed = project.IpAllowlist.Any(cidr => IsInRange(clientIp, cidr));
       if (!allowed) {
           await auditLog.LogAsync("login.ip_blocked", ...);
           return Unauthorized(new { error = "ip_not_allowed" });
       }
   }
   ```
   Use `System.Net.IPNetwork` or a small CIDR parsing helper.
3. Expose in `ProjectController` and `SystemAdminController` PATCH endpoints.

**Frontend — `Authentication.tsx` (new "Security" tab or in Registration tab):**
- Textarea: "Allowed IP ranges (CIDR, one per line)".
- Client-side CIDR validation with format hint.
- Warning: "Leaving blank allows all IPs."

---

### C7. Custom OAuth2 Scopes Per Project

**What:** All Hydra clients are created with `scope: "openid offline"`. Projects should be able to define custom scopes (e.g. `read:orders`, `write:inventory`) that appear in the consent screen and can be validated by downstream APIs.

**Backend:**
1. Add `AllowedScopes string[]` JSONB to `Project` entity.
2. In `HydraService.CreateClientAsync()` and any client update path: use `project.AllowedScopes ?? ["openid", "offline"]` when setting the Hydra client scope list.
3. New endpoints in `ProjectController` and `SystemAdminController`:
   - `PUT /project/scopes` — replace scope list, triggers Hydra client update.
4. Scope names must be validated: lowercase, colon-delimited, no spaces.

**Frontend — `Authentication.tsx` (new "Scopes" tab):**
- Tag-style input to add/remove custom scopes.
- Built-in scopes (`openid`, `offline`) shown as non-removable pills.
- Warning about the implications of scope changes for existing tokens.

---

## D — Operational Excellence (Medium Priority)

---

### D1. Audit Log Retention Policy

**What:** Audit logs grow forever. Compliance requirements typically mandate 90 days to 1 year retention, after which old logs must be purged.

**Backend:**
1. Add `AuditRetentionDays int?` to `Organisation` entity (null = global default). Add column via startup SQL.
2. Add `AuditRetentionDays int` to `AppConfig` (default: 365) via env var `Audit__RetentionDays`.
3. Add `src/Services/AuditLogRetentionService.cs` as a `BackgroundService`:
   ```csharp
   protected override async Task ExecuteAsync(CancellationToken ct) {
       while (!ct.IsCancellationRequested) {
           await PurgeExpiredLogsAsync(ct);
           await Task.Delay(TimeSpan.FromHours(24), ct);
       }
   }
   ```
   The purge runs a single parameterized SQL DELETE per org (or global for system logs).
4. Register in `Program.cs`: `builder.Services.AddHostedService<AuditLogRetentionService>()`.

**Frontend Admin SPA:**
- Org settings area (currently no dedicated "Org settings" page exists for org admins — add one): "Audit log retention" dropdown: 30 / 60 / 90 / 180 / 365 days / Forever.
- Super admin can also set the global default in the System Email/Settings area.

---

### D2. Data Export

**What:** Compliance teams need to export user lists and audit logs. No export endpoints exist.

**Backend — streaming CSV responses:**
```csharp
// GET /admin/organizations/{id}/export/users?format=csv
// GET /admin/organizations/{id}/export/audit-log?format=csv&from=2026-01-01&to=2026-03-31
// GET /org/userlists/{id}/export?format=csv
```
- Use `Response.Headers["Content-Disposition"] = "attachment; filename=users.csv"`.
- Stream rows directly from the DB query to avoid loading large datasets into memory (`yield return` / `IAsyncEnumerable`).
- Rate-limit: max 1 export per minute per caller.
- Audit log the export action.
- Never include password hashes, TOTP secrets, or encrypted fields in exports.

**Frontend Admin SPA:**
- Export button (with download icon) on user list pages and audit log pages.
- Dropdown: CSV / JSON format selector.

---

## E — Enterprise Features (Lower Priority / Future)

---

### E1. SAML 2.0 Enterprise Federation

**What:** Allow enterprises to log in via their corporate IdP (Okta, Azure AD, ADFS) using SAML 2.0 assertions. This is required for enterprises that have centralised IAM and cannot use per-user credentials.

**Library:** `Sustainsys.Saml2.AspNetCore2` (open source, MIT)

**Backend — new entity `SamlIdpConfig`:**
```csharp
public class SamlIdpConfig {
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string EntityId { get; set; } = "";         // IdP entity ID
    public string MetadataUrl { get; set; } = "";      // or inline XML
    public string? CertificatePem { get; set; }        // IdP signing cert
    public string EmailAttributeName { get; set; } = "email"; // SAML attribute → user email
    public string? NameAttributeName { get; set; }     // SAML attribute → display name
    public bool JitProvisioning { get; set; } = true;  // create user on first login
    public Guid? DefaultRoleId { get; set; }
    public bool Active { get; set; } = true;
}
```

**Backend — authentication flow:**
1. `GET /auth/saml/start?project_id=X&idp_id=Y` — generate AuthnRequest, redirect to IdP.
2. `POST /auth/saml/acs` — Assertion Consumer Service. Validates assertion signature, extracts user attributes, performs JIT provisioning if enabled, completes Hydra login accept.
3. `GET /admin/projects/{id}/saml/metadata` — serves SP metadata XML for the IdP to register.

**Backend — CRUD endpoints:**
- `GET/POST/DELETE /project/saml-providers` — manage SAML IdP configs.
- Same under `/admin/organizations/:oid/projects/:pid/saml-providers` for super admin.

**Frontend — `Authentication.tsx` (Providers tab):**
- "SAML 2.0 Identity Provider" section below custom OIDC.
- Fields: IdP metadata URL, email attribute name, display name attribute, JIT provisioning toggle, default role.
- Show "SP Metadata URL" (copyable) for the admin to give to their IdP.

---

### E2. Suspicious Login / New Device Detection

**What:** Email a user when they log in from a device or IP subnet they have never used before.

**Backend:**
1. Track device fingerprints (HMAC of `user-agent + IP /24 subnet`) per user in Redis with a 90-day TTL.
2. On successful login in `AuthController`: check if fingerprint is new.
3. If new: add `IEmailService.SendNewDeviceAlertAsync(...)` and dispatch it.
4. Add `User.NewDeviceAlertsEnabled bool` (default: true).

**Backend — `AccountController`:**
- `PATCH /account/me`: allow toggling `new_device_alerts_enabled`.

**Frontend — `AccountPage.tsx`:**
- Toggle "Email me on login from a new device."

---

## Summary Table

| ID  | Item                                 | Priority | Backend | Frontend |
|-----|--------------------------------------|----------|---------|----------|
| A1  | Encrypt social login client_secrets  | 🔴 CRIT  | ✎       | ✎        |
| A2  | Security headers middleware           | 🔴 HIGH  | ✎       | —        |
| A3  | Rate limit registration endpoints     | 🔴 HIGH  | ✎       | —        |
| A4  | Constant-time OTP comparison          | 🟡 MED   | ✎       | —        |
| A5  | Email enumeration fix                 | 🟡 MED   | ✎       | —        |
| B1  | User invitation flow                  | 🔴 HIGH  | ✎       | ✎        |
| B2  | Admin account unlock                  | 🔴 HIGH  | ✎       | ✎        |
| B3  | Mandatory MFA per project             | 🔴 HIGH  | ✎       | ✎        |
| B4  | System service accounts CRUD + PATs   | 🔴 HIGH  | ✎       | ✎        |
| B5  | Webhooks                              | 🔴 HIGH  | ✎       | ✎        |
| C1  | OpenAPI / Swagger                     | 🟡 MED   | ✎       | —        |
| C2  | Account linking (social providers)    | 🟡 MED   | ✎       | ✎        |
| C3  | Session visibility for admins         | 🟡 MED   | ✎       | ✎        |
| C4  | Prometheus metrics endpoint           | 🟡 MED   | ✎       | —        |
| C5  | Breach password check (HIBP)          | 🟡 MED   | ✎       | ✎        |
| C6  | IP allowlist per project              | 🟡 MED   | ✎       | ✎        |
| C7  | Custom OAuth2 scopes per project      | 🟡 MED   | ✎       | ✎        |
| D1  | Audit log retention policy            | 🟡 MED   | ✎       | ✎        |
| D2  | Data export (CSV/JSON)                | 🟢 LOW   | ✎       | ✎        |
| E1  | SAML 2.0 enterprise federation        | 🟢 LOW   | ✎       | ✎        |
| E2  | Suspicious login / new device alert   | 🟢 LOW   | ✎       | ✎        |
