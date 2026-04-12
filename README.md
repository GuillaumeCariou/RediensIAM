# RediensIAM

Multi-tenant Identity & Access Management system built on Ory Hydra + Keto, ASP.NET Core 10, and React.

- **Login SPA** — user-facing login, registration, MFA, password reset
- **Admin SPA** — super-admin, org-admin, and project-manager management console
- **Backend API** — ASP.NET Core 10, two ports: public `:5000` / admin `:5001`
- **Ory Hydra** — OAuth2/OIDC token issuance and consent
- **Ory Keto** — fine-grained permission checks
- **PostgreSQL + Dragonfly** — persistence and cache

---

## Prerequisites

| Tool | Version |
|------|---------|
| Docker | 20+ |
| k3s (or any k8s) | 1.28+ |
| kubectl | matching cluster |
| Helm | 3.12+ |
| .NET SDK | 10.0 |
| Node.js | 20+ |

---

## Deployment

### 1. Create `values.secret.yaml`

The deploy script reads secrets from `deploy/rediensiam/values.secret.yaml`.  
**This file must never be committed.**

Copy the template and fill in real values:

```bash
cp deploy/rediensiam/values.secret.yaml deploy/rediensiam/values.secret.yaml.bak  # keep the example
```

Full annotated `values.secret.yaml`:

```yaml
# ── Bootstrap super-admin account (created on first start) ──────────────────
env:
  IAM_BOOTSTRAP_EMAIL: "admin@example.com"
  IAM_BOOTSTRAP_PASSWORD: "ChangeMe123!"   # min 8 chars; change immediately after first login

# ── Secrets injected as env vars ────────────────────────────────────────────
secrets:
  # PostgreSQL connection string for the IAM database
  databaseUrl: "Host=rediensiam-postgres;Database=rediensiam;Username=iam;Password=STRONG_PASSWORD"

  # Dragonfly/Redis connection string
  cacheUrl: "rediensiam-dragonfly:6379,abortConnect=false"

  # 32-byte key (base64-encoded) used to encrypt TOTP secrets at rest
  # Generate: openssl rand -base64 32
  totpEncryptionKey: "CHANGE_ME_32_BYTE_KEY_BASE64_ENC="

  # Random hex string used as Argon2 pepper for password hashing
  # Generate: openssl rand -hex 32
  argon2Pepper: "CHANGE_ME_64_HEX_CHARS"

  # Global SMTP password (leave blank to use per-org SMTP only)
  smtpPassword: ""

# ── PostgreSQL password (must match the one in databaseUrl above) ────────────
postgres:
  password: STRONG_PASSWORD

# ── Ory Hydra ────────────────────────────────────────────────────────────────
hydra:
  hydra:
    config:
      # Hydra's own database (can share the same Postgres instance)
      dsn: "postgres://iam:STRONG_PASSWORD@rediensiam-postgres:5432/hydra?sslmode=disable"
      secrets:
        system:
          # At least 32 characters; used to sign Hydra tokens
          # Generate: openssl rand -hex 32
          - "CHANGE_ME_HYDRA_SYSTEM_SECRET_AT_LEAST_32_CHARS"

# ── Ory Keto ─────────────────────────────────────────────────────────────────
keto:
  keto:
    config:
      dsn: "postgres://iam:STRONG_PASSWORD@rediensiam-postgres:5432/keto?sslmode=disable"
```

> **Tip:** Run `openssl rand -hex 32` to generate each secret value.

---

### 2. Deploy

```bash
# Dev — local k3s, HTTP, Hydra dev mode
bash deploy/deploy.sh --dev

# Dev — upgrade Helm dependencies first (after chart.yaml changes)
bash deploy/deploy.sh --dev --upgrade

# Prod — uses values.prod.secret.yaml (generated interactively if missing)
bash deploy/deploy.sh --prod
```

The script does the following in order:

1. Starts the local Docker registry at `localhost:5000`
2. Builds both SPAs (`npm ci && npm run build`)
3. Builds and pushes the Docker image
4. Resolves Helm chart dependencies (Hydra, Keto)
5. Runs `helm upgrade --install` with the appropriate values
6. Bootstraps the `client_admin_system` Hydra OAuth2 client

After deployment:

```
Login  →  http://localhost/login
Admin  →  http://localhost/admin/
```

---

### `values.yaml` — Full reference

All keys below can be overridden in `values.secret.yaml` or via `--set` on the command line.

| Key | Default | Description |
|-----|---------|-------------|
| `appUrl` | `http://localhost` | Public base URL (login SPA, Hydra issuer, user-facing endpoints) |
| `image.repository` | `rediensiam` | Docker image name |
| `image.tag` | `"0.0.1"` | Docker image tag |
| `image.pullPolicy` | `IfNotPresent` | Kubernetes pull policy |
| `replicaCount` | `1` | Number of app replicas |
| `service.public.port` | `5000` | Public API port |
| `service.admin.port` | `5001` | Admin API port |
| `service.admin.nodePort` | `30501` | NodePort for admin SPA/API |
| `ingress.host` | `localhost` | Ingress hostname |
| `env.IAM_ADMIN_PATH` | `/admin` | Path prefix for the admin SPA |
| `env.App__PublicUrl` | `http://localhost` | Must match `appUrl` |
| `env.App__AdminSpaOrigin` | `http://localhost` | CORS origin and OIDC redirect base for the admin SPA — must match the origin the browser uses to load it |
| `env.App__Domain` | `localhost` | Cookie domain |
| `env.Smtp__Host` | `""` | Global SMTP host (optional) |
| `env.Smtp__Port` | `587` | Global SMTP port |
| `env.Smtp__StartTls` | `true` | Use STARTTLS |
| `env.Smtp__Username` | `""` | Global SMTP username |
| `env.Smtp__FromAddress` | `noreply@localhost` | Sender address for system emails |
| `env.Smtp__FromName` | `RediensIAM` | Sender display name |
| `env.Hydra__AdminUrl` | `http://rediensiam-hydra-admin:4445` | Hydra admin API URL (in-cluster) |
| `env.Hydra__PublicUrl` | `http://rediensiam-hydra-public:4444` | Hydra public API URL (in-cluster) |
| `env.Keto__ReadUrl` | `http://rediensiam-keto-read:4466` | Keto read API URL |
| `env.Keto__WriteUrl` | `http://rediensiam-keto-write:4467` | Keto write API URL |
| `secrets.databaseUrl` | `""` | **Required.** PostgreSQL connection string |
| `secrets.cacheUrl` | `""` | **Required.** Dragonfly/Redis connection string |
| `secrets.totpEncryptionKey` | `""` | **Required.** 32-byte base64 key for TOTP secret encryption |
| `secrets.argon2Pepper` | `""` | **Required.** Hex pepper for Argon2 password hashing |
| `secrets.smtpPassword` | `""` | Global SMTP password |
| `postgres.password` | `""` | **Required.** PostgreSQL root password |
| `postgres.storage` | `2Gi` | PVC size for PostgreSQL |
| `hydra.hydra.config.dsn` | `""` | **Required.** Hydra database connection string |
| `hydra.hydra.config.secrets.system` | `[]` | **Required.** Hydra signing secrets (≥32 chars) |
| `keto.keto.config.dsn` | `""` | **Required.** Keto database connection string |

---

## Running Tests

### Backend — integration tests (.NET)

The integration tests use Testcontainers (PostgreSQL + Redis) and WireMock (Hydra/Keto stubs). No external services needed.

```bash
# Run all integration tests
dotnet test tests/RediensIAM.IntegrationTests/

# Run a specific test class
dotnet test tests/RediensIAM.IntegrationTests/ --filter "FullyQualifiedName~LoginTests"

# Run with verbose output
dotnet test tests/RediensIAM.IntegrationTests/ --logger "console;verbosity=detailed"

# Run in parallel (default) with a specific number of workers
dotnet test tests/RediensIAM.IntegrationTests/ -- xunit.maxParallelThreads=4
```

The test suite covers 367 tests across auth flows, org/project management, service accounts, webhooks, and security.

---

### Frontend — E2E tests (Playwright)

E2E tests live in `tests/e2e/` and cover both SPAs (Login and Admin).

#### First-time setup

```bash
cd tests/e2e
npm install
npx playwright install chromium
```

#### Configure credentials

```bash
cp .env.example .env
```

Edit `.env`:

```env
TEST_BASE_URL=http://localhost          # public ingress URL (login SPA, admin SPA, OIDC flow)
TEST_ADMIN_URL=http://localhost:30501   # direct NodePort URL for smoke tests only
TEST_SUPER_ADMIN_EMAIL=admin@local      # must match IAM_BOOTSTRAP_EMAIL in values.secret.yaml
TEST_SUPER_ADMIN_PASSWORD=Admin1234!    # must match IAM_BOOTSTRAP_PASSWORD
```

> **Important:** `App__AdminSpaOrigin` in `values.yaml` (and the Hydra `redirect_uri` registered at deploy time) must match `TEST_BASE_URL`. Both default to `http://localhost`. Mismatching origins will cause the OIDC callback to fail with an empty session.

> The dev stack must be running (`bash deploy/deploy.sh --dev`) before the E2E tests can be executed.  
> The global setup will log in once via the full OIDC flow and cache the session in `.auth/admin-session.json`.

#### Run tests

```bash
# All tests
npm test

# Watch mode (interactive UI)
npm run test:ui

# Headed browser (useful for debugging)
npm run test:headed

# Login SPA tests only (no auth required)
npm run test:login

# Admin SPA tests only (uses cached auth session)
npm run test:admin

# Single spec file
npx playwright test tests/login/login.spec.ts

# With full trace on failure
npx playwright test --trace on

# Open the HTML report
npm run report
```

#### Test architecture

| Project | Files | Auth | Strategy |
|---------|-------|------|----------|
| `login` | `tests/login/*.spec.ts` | None | Hits real backend; API calls mocked via `page.route()` |
| `admin` | `tests/admin/*.spec.ts` | OIDC session injected | All API calls mocked via `page.route()` |
| `account` | `tests/account/*.spec.ts` | OIDC session injected | All API calls mocked |

**Admin SPA authentication:** The admin SPA stores its OIDC token in `sessionStorage` (via `oidc-client-ts`). Playwright's native `storageState` only covers cookies and `localStorage`, so the global setup captures `sessionStorage` after a real login and writes it to `.auth/admin-session.json`. The `adminPage` fixture re-injects these keys via `page.addInitScript` before each test.

#### Test coverage (50 tests across 13 files)

| File | Area |
|------|------|
| `login/login.spec.ts` | Credential validation, lockout, MFA redirect, success flow |
| `login/register.spec.ts` | Form validation, breach check, OTP verification step |
| `login/password-reset.spec.ts` | Three-step reset flow, breach/policy/expiry errors |
| `login/mfa.spec.ts` | TOTP, SMS OTP, backup codes, WebAuthn (mocked `credentials.get`) |
| `login/invite.spec.ts` | Set-password from invite link, policy/breach/expiry errors |
| `account/account.spec.ts` | Profile, password change, TOTP setup, sessions, social links |
| `admin/system-users.spec.ts` | Global user search, edit dialog, sessions dialog, unlock |
| `admin/org-lifecycle.spec.ts` | Org CRUD, suspend/unsuspend, navigate to detail |
| `admin/user-lists.spec.ts` | Global vs org-context view, columns, search, create, navigate |
| `admin/webhooks.spec.ts` | CRUD, test button, delivery log, secret rotation |
| `admin/service-accounts.spec.ts` | PAT generation/revocation, API key management, delete SA |
| `admin/project-authentication.spec.ts` | SAML providers, OAuth2 providers, password policy, login theme |
| `admin/deployment-smoke.spec.ts` | Public ingress and NodePort reachability for /admin/ |
| `org/org-email.spec.ts` | SMTP configure, test, delete, super-admin context |

#### What requires manual testing

Some features cannot be reliably automated due to external dependencies:

- **WebAuthn with real hardware** (YubiKey, TouchID) — `mfa.spec.ts` mocks `navigator.credentials.get`
- **Real email delivery** — use MailHog in dev; run `bash deploy/deploy.sh --dev` and check `http://localhost:8025`
- **Real SMS delivery** — stub service returns OTP via logs in dev mode
- **Social OAuth2 login** (Google, GitHub, etc.) — requires live provider consent screen
- **SAML login with external IdP** — configure a test IdP (e.g. SimpleSAMLphp) separately
- **Webhook delivery to external endpoint** — use `ngrok` or `webhook.site` manually
