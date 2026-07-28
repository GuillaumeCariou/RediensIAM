# RediensIAM Architecture

## Philosophy

RediensIAM is built on three principles:

1. **Stateless app, stateful infrastructure.** Pods carry no per-instance data. Postgres holds the durable state (users, orgs, projects, audit log). Redis/Dragonfly holds ephemeral shared state (sessions, rate-limit counters, OTP challenges, DataProtection keys). Any number of replicas can run side-by-side.
2. **Standards over re-invention.** OAuth2/OIDC = Ory Hydra. Fine-grained authorisation = Ory Keto. Argon2id for passwords. WebAuthn level-2 for passkeys. We do not re-implement these.
3. **Defence in depth, but no magic.** Every webhook URL is re-validated for SSRF on each delivery. Every redirect target passes through an allowlist. Static analysers (SecurityCodeScan, SonarAnalyzer.CSharp) run in CI.

> **Gap — read before relying on principle 3.** This document previously claimed "every
> privileged path checks the database, not just the token". That is **not** what the code
> does. `RequireManagementLevelAttribute` authorises purely from `ext.roles` in the
> introspected token; `KetoService.CheckAsync` is not on that path. A role revoked or an
> org suspended after issuance stays effective until the token expires. Tracked as
> finding A in [`2026-07-28-findings-securite-deploiement.md`](2026-07-28-findings-securite-deploiement.md)
> and SEC-11 in [`2026-07-28-audit-complet.md`](2026-07-28-audit-complet.md).

---

## Components

```
        ┌────────────────┐                 ┌────────────────┐
        │  Login SPA     │                 │  Admin SPA     │
        │ (Vite + React) │                 │ (Vite + React) │
        └────────┬───────┘                 └────────┬───────┘
                 │                                   │
       ┌─────────▼───────────┐         ┌─────────────▼───────┐
       │  Backend public :5000         │  Backend admin :5001 │
       │  (same dotnet process, two ports)                    │
       └─────────┬───────────┘         └─────────────┬───────┘
                 │                                   │
                 └─────┬───────────────┬─────────────┘
                       │               │
                ┌──────▼──────┐ ┌──────▼──────┐ ┌──────────────┐
                │  Ory Hydra  │ │  Ory Keto   │ │ Postgres+    │
                │ OAuth2/OIDC │ │ Permissions │ │ Dragonfly    │
                └─────────────┘ └─────────────┘ └──────────────┘
```

The dotnet process listens on two ports from one binary. Public endpoints (login, register, MFA, social OAuth callback, OIDC consent) bind to `:5000`. Admin endpoints (orgs, projects, users, webhooks, audit) bind to `:5001`. The split lets the admin port stay on a separate ingress / NodePort with stricter network policy.

---

## Statelessness model

### Per-pod state (re-derivable, never persisted across restarts)

- HTTP request scope
- In-process caches (OIDC discovery, dummy-Argon2 hash)
- Currently in-memory hosted-service queues (webhook dispatcher worker)

Every other piece of state is in Postgres or Redis. A pod can die, restart, or be replaced without losing anything user-visible.

### Shared state (Postgres)

- Users, orgs, projects, roles, role assignments
- Personal access tokens (hashed), service accounts, WebAuthn credentials
- Webhooks + delivery history
- Audit log
- **Instance config row** — see below

### Shared state (Redis / Dragonfly)

- HTTP session cookies (MFA challenge step)
- DataProtection keys (so session cookies survive pod restart)
- Rate-limit counters (per-IP, per-user)
- OTP store (email + SMS codes, anti-replay)
- PAT introspection cache (5 min TTL, invalidated on PAT revoke and SA delete only — **not** on SA deactivate or org suspend; see SEC-08)
- Hydra introspect cache (≤ 60 s, clamped by token `exp`)
- Webhook job queue (Redis sorted-set)
- OAuth2 social-login state store

---

## Configuration model — Zitadel-style

Runtime configuration (URLs, ports, SMTP, rate-limit thresholds, Argon2 parameters) is stored in a single-row **`instances`** table in Postgres. Pods read from this row at startup; environment variables become dormant after the first boot. This means multiple replicas always see the same configuration and a fleet of pods can be reconfigured atomically by changing one DB row.

**Secrets stay env-only:**
- `ConnectionStrings__Default` — needed to reach the DB
- `Security__TotpSecretEncryptionKey` — decrypts secrets stored in the DB
- `Security__Argon2Pepper` — cryptographic root
- `IAM_BOOTSTRAP_EMAIL` / `IAM_BOOTSTRAP_PASSWORD` — first-run super-admin
- `INSTANCE_ID` — which row to load (defaults to `"default"`)

### Boot flow

| Scenario | Result |
|---|---|
| First start (row missing) | Read env → write row → use values from row |
| Normal start (row present) | Load row → ignore env for these keys |
| `RECONFIGURE_FROM_ENV=true` | Read env → overwrite row → bump `config_version` → use new values |

The deployment manifest keeps all the env vars defined as the **record of last reconfigure**, but the app reads from the database at runtime. Editing a value in `values.yaml` without setting `RECONFIGURE_FROM_ENV=true` does nothing — the pod keeps reading the old DB value.

### Reconfigure procedure

```bash
# 1. Edit the value
yq -i '.rediensiam.app.publicUrl = "https://new.example.com"' values.prod.yaml

# 2. Mark this deploy as a reconfigure
yq -i '.rediensiam.env.RECONFIGURE_FROM_ENV = "true"' values.prod.yaml

# 3. Deploy
helm upgrade rediensiam ./deploy/rediensiam -f values.prod.yaml -f values.prod.secret.yaml

# 4. Remove the flag (so the next routine deploy is not a reconfigure)
yq -i 'del(.rediensiam.env.RECONFIGURE_FROM_ENV)' values.prod.yaml
helm upgrade rediensiam ./deploy/rediensiam -f values.prod.yaml -f values.prod.secret.yaml
```

### Implementation

| Piece | File |
|---|---|
| Entity | `src/Data/Entities/Instance.cs` |
| Config provider (loads DB row into `IConfiguration`) | `src/Config/InstanceConfiguration.cs` |
| Wired at startup | `src/Program.cs` (`builder.Configuration.AddInstanceConfiguration()`) |
| Migration | `src/Data/Migrations/*_AddInstanceConfig.cs` |

The provider is a stock `Microsoft.Extensions.Configuration.ConfigurationProvider` so the rest of the app (the 128 `appConfig.X` reads) is unchanged. `AppConfig` keeps reading from `IConfiguration`; the only difference is that DB-sourced values now sit in a higher-priority layer.

---

## Trust boundaries

| From → To | Trust |
|---|---|
| Browser → Backend public `:5000` | Untrusted; rate-limited, anti-CSRF cookies, CSP |
| Browser → Backend admin `:5001` | OIDC-authenticated, JWT-bearer. Not cookie-free in practice: `/account/mfa/totp/*` and `/account/mfa/webauthn/*` keep setup state in the ASP.NET session cookie, which `SameSite=Strict` blocks whenever `App__AdminSpaOrigin` differs from `App__PublicUrl` (see FUNC-07) |
| Backend → Hydra admin `:4445` | Trusted (in-cluster service); NetworkPolicy locked |
| Backend → Keto write `:4467` | Trusted (in-cluster service); NetworkPolicy locked |
| Backend → Postgres `:5432` | Shared user `iam` with full DB; NetworkPolicy locked to {app, hydra, keto} pods |
| Backend → Dragonfly `:6379` | Password-protected; NetworkPolicy locked to app pod only |
| Hydra → Backend public `:5000` (consent flow) | Browser-mediated redirect; allowlist via `RedirectValidator` |
| External IdP → Backend `/auth/saml/acs` | SAML assertion verified against pinned IdP certificate |
| Operator → Backend admin (PAT / service account) | Bearer token; SA active + org active checked on **cache miss only** (5 min TTL) |

---

## Authentication flows

### Password login

```
Browser  →  /auth/login           (challenge from Hydra)
Backend  →  validate user/password
Backend  →  rate-limit check
Backend  →  if MFA enrolled → /auth/mfa/...
Backend  →  hydra.AcceptLoginAsync(subject = orgId:userId)
Browser  →  Hydra consent flow
Hydra    →  redirect to SPA with code
SPA      →  exchange code for token via Hydra public
```

### Social login (Google / GitHub / OIDC)

```
Browser  →  /auth/oauth2/start?provider_id=google
Backend  →  build authorize URL (server-side), SafeRedirect to provider
Provider →  user consent
Provider →  redirect to /auth/oauth2/callback?code=...
Backend  →  exchange code, fetch profile (email must be verified)
Backend  →  find-or-create user, link social account
Backend  →  hydra.AcceptLoginAsync → SafeRedirect into normal flow
```

### SAML login

```
Browser  →  /auth/saml/start?idp_id=...   (idp_id is NOT checked against the challenge's project — SEC-02)
Backend  →  AuthnRequest UNSIGNED (SP metadata advertises AuthnRequestsSigned="false"), redirect to IdP
IdP      →  user consent
IdP      →  POST SAMLResponse to /auth/saml/acs
Backend  →  verify signature (pinned cert)
Backend  →  JIT-provision user if enabled
Backend  →  hydra.AcceptLoginAsync → Location header redirect into flow
```

### MFA

- TOTP — Otp.NET, anti-replay via Redis `TotpUsed` set
- SMS OTP — rate-limited per user (default 3 / 10 min)
- WebAuthn (passkey / security key) — Fido2NetLib. `UserVerification = Preferred`, **not** `Required` (`AuthController.WebAuthnOptions`), so a non-verifying authenticator still satisfies the factor. The assertion is also not bound server-side to the user pending MFA (SEC-05).
- Backup codes — HMAC-SHA256, versioned format `sha256:{keyId}:{hex}` so pepper rotation is detectable

Session cookie is rotated on every successful MFA completion to defeat session fixation.

---

## Security controls — quick reference

| Control | Where |
|---|---|
| Argon2id password hashing + optional pepper | `PasswordService.cs` |
| Open-redirect allowlist | `RedirectValidator.cs` + `AuthController.SafeRedirect` |
| Webhook SSRF re-validation (every delivery) | `WebhookUrlValidator` |
| HMAC-signed webhook deliveries | `WebhookService.ComputeSignature` |
| Per-org SMTP password encryption (AES-GCM) | `TotpEncryption.EncryptString` with HKDF-derived purpose-specific keys |
| Session-cookie DataProtection keys persisted to Redis | `Program.cs` |
| HSTS, CSP, frame-ancestors, X-Content-Type-Options | `Program.cs:AddSecurityHeaders` |
| Anti-forgery exemption for SAML ACS (signature-verified) | `[IgnoreAntiforgeryToken]` on `SamlController.AssertionConsumerService` |
| Trusted-proxy fail-closed in production | `Program.cs:ConfigureForwardedHeaders` |
| NetworkPolicy: Postgres / Dragonfly / Hydra / Keto locked down | `deploy/rediensiam/templates/network-policies.yaml` |
| Pod securityContext: non-root, drop ALL caps, no priv-esc, RO root | `deploy/rediensiam/templates/{deployment,postgres,dragonfly}.yaml` |

---

## Running locally

```bash
bash deploy/deploy.sh --dev
```

This brings up a single-node k3s with Hydra + Keto + Postgres + Dragonfly + the app. See `README.md` for full details.

## Testing

- `dotnet test tests/RediensIAM.IntegrationTests/` — 1093 tests against real Postgres/Redis containers (Testcontainers) and WireMock Hydra/Keto stubs. 1059 pass; the 34 in `Tests/Regression/` fail by design, one per open audit finding.
- `cd tests/e2e && npm test` — Playwright E2E across login + admin SPAs

## Sonar

`bash sonar-scan.sh` publishes a **single** SonarQube project, `RediensIAM`, covering the C# backend and both SPAs in one analysis (the MSBuild scanner indexes non-MSBuild files under `sonar.projectBaseDir`). The former `Admin-SPA` / `Login-SPA` projects and their `sonar-project.properties` files are gone; delete those projects server-side.

The backend ships `SecurityCodeScan.VS2019` + `SonarAnalyzer.CSharp`, so most C# issues surface at build time. Note that neither analyser models cross-tenant authorisation, so the findings in `2026-07-28-audit-complet.md` are invisible to them — a clean quality gate is not evidence of tenant isolation.
