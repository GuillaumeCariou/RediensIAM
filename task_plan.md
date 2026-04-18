# Task Plan: RediensIAM Production Readiness Fixes

## Goal
Fix all 55 issues identified in the production readiness audit to make RediensIAM safe and stable for production deployment.

## Current Phase
Complete

## Phases

### Phase 1: Critical Security Fixes (C1–C15)
- [x] C1 — Fixed: only link social account when both provider AND local email are verified
- [x] C2 — Fixed: removed OTP code from stub email/SMS log lines
- [x] C3 — Fixed: removed weak Argon2 overrides; secure defaults in AppConfig now apply
- [x] C4 — N/A: values.secret.yaml already in .gitignore; never committed to git
- [x] C5 — Fixed: startup now validates key is exactly 64 hex chars (not just non-null)
- [x] C6 — Fixed: HKDF-derived per-purpose subkeys added to AppConfig; all callsites updated
- [x] C7 — Fixed: extracted WebhookUrlValidator; AdminCreateWebhook now calls it
- [x] C8 — Fixed: WebhookDispatcherService re-validates IP at delivery time (DNS rebinding)
- [x] C9 — Fixed: X-RediensIAM-Signature header omitted when secret is empty
- [x] C10 — Fixed: VerifyOtpAsync + VerifySessionOtpAsync lock after 5 failed attempts
- [x] C11 — Fixed: Session.Clear() called after MFA completion
- [x] C12 — Fixed: RequireManagementLevelAttribute returns 401 (not 403) for unauthenticated requests
- [x] C13 — Fixed: added warning comment to values.yaml; operators must set system secret
- [x] C14 — Fixed: SamlService throws if no signing certs in metadata (was just warning)
- [x] C15 — Fixed: SSRF check applied to SAML MetadataUrl
- **Status:** complete ✅

### Phase 2: Important Security & Reliability (I1–I11)
- [x] I1 — Fixed: backup codes hashed with Argon2id (not unsalted SHA256)
- [x] I2 — Fixed: atomic rate limiter using Redis INCR+EXPIRE Lua script
- [x] I3 — Fixed: OIDC discovery cache moved to Singleton service with TTL
- [x] I4 — Fixed: PKCE (S256) added to all social OAuth2 flows; verifier stored in state → passed to callback
- [x] I5 — Fixed: exception logging attached to fire-and-forget tasks
- [x] I6 — Fixed: ValidatePasswordPolicyAsync called in ChangePassword
- [x] I7 — Fixed: replaced hardcoded 72 with appConfig.InviteExpiryHours in ResendInvite
- [x] I8 — Fixed: webhook jobs persisted to Redis sorted set; recovered on startup; removed after processing
- [x] I9 — Fixed: Polly retry + circuit breaker on Hydra and Keto HTTP clients
- [x] I10 — Fixed: token introspection results cached in Redis (TTL = remaining token lifetime)
- [x] I11 — Fixed: DB SaveChangesAsync before Keto tuple write in OrgController.CreateProject
- **Status:** complete ✅

### Phase 3: Infrastructure & Deployment (I12–I22)
- [x] I12 — Fixed: PDB maxUnavailable: 1
- [x] I13 — Fixed: SMTP NetworkPolicy egress has to: selector
- [x] I14 — Fixed: port 443 egress rule added for webhook HTTPS
- [x] I15 — Fixed: Docker base images pinned to SHA digests
- [x] I16 — Fixed: sanitizeCss() strips url(http...) in Login SPA
- [x] I17 — Fixed: response.ok checked before .json() in Login SPA api.ts
- [x] I18 — Fixed: Admin SPA disable/enable user uses PATCH with { active: false/true }
- [x] I19 — Fixed: HSTS header added in security headers middleware
- [x] I20 — Fixed: AllowedHosts set to production hostname via env var
- [x] I21 — Fixed: pagination loop implemented in ListOAuth2ClientsAsync
- [x] I22 — Fixed: startup warns on non-HTTPS PublicUrl/AdminSpaOrigin in production
- **Status:** complete ✅

### Phase 4: Operational Quality (S1–S18)
- [x] S1 — Fixed: duplicate breach check removed from CompleteInvite
- [x] S2 — Fixed: TOTP issuer set to org/project name
- [x] S3 — Fixed: sequential discriminator counter replaces random 4-digit
- [x] S4 — Fixed: SMTP connection pooling via SmtpSendAsync static helper
- [x] S5 — Fixed: org context passed to SendNewDeviceAlertAsync
- [x] S6 — Fixed: full SHA256 hash in StoreTotpUsedAsync (not 16-char truncation)
- [x] S7 — Fixed: minimum 3-char query length enforced in SearchUsers
- [x] S8 — Fixed: TestOrgSmtp sends clearly-labelled test message with fake code
- [x] S9 — Fixed: startupProbe added to deployment.yaml
- [x] S10 — Fixed: emptyDir /tmp mount + readOnlyRootFilesystem: true
- [x] S11 — Fixed: EF Core migrations scaffolded; MigrateAsync replaces EnsureCreated + raw SQL
- [x] S12 — Fixed: integration tests expanded — SAML, WebAuthn, entity models, system admin, webhooks
- [x] S13 — Fixed: webhook Channel drained on SIGTERM with 10s window
- [x] S14 — Fixed: Admin SPA tokens in InMemoryWebStorage (not sessionStorage)
- [x] S15 — Fixed: logo_url validated against HTTPS + allowlisted domains
- [x] S16 — Fixed: audit log entries for failed logins, OTP failures, password resets
- [x] S17 — Fixed: Hydra NetworkPolicy allows rediensiam pod on port 4444
- [x] S18 — Fixed: ephemeral-storage limits added to Dragonfly/Postgres pods in values.yaml
- **Status:** complete ✅

### Phase 5: Verification
- [ ] Deploy to dev environment with deploy-dev.sh --dev
- [ ] Smoke test auth flows
- **Status:** pending

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| Fix criticals first in order C1→C15 | Account takeover + credential exposure are highest risk |
| Use HKDF for key derivation (C6) | Standard approach; avoids introducing new key config burden |
| Phases match severity tiers | Allows partial deployment of fixes incrementally |
| PKCE verifier stored in OAuth state (I4) | State is already Redis-backed + HMAC-verified; cleanest transport |
| Webhook queue = Redis sorted set (I8) | Survives pod restarts; ZREM after processing prevents re-delivery |
| MigrateAsync replaces EnsureCreated (S11) | Enables incremental schema evolution without raw SQL patches |
