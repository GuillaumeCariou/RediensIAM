# Task Plan: RediensIAM Production Readiness Fixes

## Goal
Fix all 55 issues identified in the production readiness audit to make RediensIAM safe and stable for production deployment.

## Current Phase
Phase 2

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
- [ ] C12 — Investigate: admin GET bypass needs deeper audit of endpoint auth
- [x] C13 — Fixed: added warning comment to values.yaml; operators must set system secret
- [x] C14 — Fixed: SamlService throws if no signing certs in metadata (was just warning)
- [x] C15 — Fixed: SSRF check applied to SAML MetadataUrl
- **Status:** complete (C12 deferred — needs endpoint-by-endpoint audit)

### Phase 2: Important Security & Reliability (I1–I11)
- [ ] I1 — Hash backup codes with Argon2id (not unsalted SHA256)
- [ ] I2 — Atomic rate limiter using Redis INCR+EXPIRE Lua script
- [ ] I3 — Move OIDC discovery cache to Singleton service with TTL
- [ ] I4 — Add PKCE (`code_verifier`/`code_challenge`) to social login flow
- [ ] I5 — Attach exception logging to fire-and-forget tasks
- [ ] I6 — Call `ValidatePasswordPolicyAsync` in `ChangePassword`
- [ ] I7 — Replace hardcoded `72` with `appConfig.InviteExpiryHours` in `ResendInvite`
- [ ] I8 — Persist webhook queue to DB/Redis; drain on graceful shutdown
- [ ] I9 — Add Polly retry + circuit breaker to Hydra and Keto HTTP clients
- [ ] I10 — Cache token introspection results in Redis (TTL = remaining token lifetime)
- [ ] I11 — Write DB first inside transaction; only write Keto tuple after commit
- **Status:** pending

### Phase 3: Infrastructure & Deployment (I12–I22)
- [ ] I12 — Fix PDB: set `maxUnavailable: 1` or require `replicaCount >= 2`
- [ ] I13 — Add `to:` selector to SMTP NetworkPolicy egress
- [ ] I14 — Add port 443 egress rule for webhook HTTPS
- [ ] I15 — Pin Docker base images to SHA digests
- [ ] I16 — Sandbox or sanitize `custom_css` in Login SPA
- [ ] I17 — Check `response.ok` before `.json()` in Login SPA `api.ts`
- [ ] I18 — Fix Admin SPA disable/enable user API calls to match backend endpoints
- [ ] I19 — Add HSTS header in security headers middleware
- [ ] I20 — Set `AllowedHosts` to production hostname (not `"*"`)
- [ ] I21 — Implement pagination loop in `ListOAuth2ClientsAsync`
- [ ] I22 — Change Hydra `publicUrl`/`adminUrl` defaults to HTTPS
- **Status:** pending

### Phase 4: Operational Quality (S1–S18)
- [ ] S1 — Remove duplicate breach check in `CompleteInvite`
- [ ] S2 — Use org/project name as TOTP issuer
- [ ] S3 — Replace random 4-digit discriminator with sequential counter or UUID suffix
- [ ] S4 — Pool SMTP connections or switch to transactional email API
- [ ] S5 — Pass org context to `SendNewDeviceAlertAsync`
- [ ] S6 — Use full hash (not 16-char truncation) in `StoreTotpUsedAsync`
- [ ] S7 — Enforce minimum query length (3 chars) in `SearchUsers`
- [ ] S8 — Make `TestOrgSmtp` clearly say "test message" with fake code
- [ ] S9 — Add `startupProbe` to deployment.yaml
- [ ] S10 — Mount `emptyDir` for temp paths; enable `readOnlyRootFilesystem: true`
- [ ] S11 — Replace `EnsureCreated` with EF Core Migrations
- [ ] S12 — Add integration tests (auth flow, token exchange, social login)
- [ ] S13 — Drain webhook Channel on SIGTERM
- [ ] S14 — Switch Admin SPA tokens from sessionStorage to in-memory store
- [ ] S15 — Validate `logo_url` against HTTPS + allowlisted domains
- [ ] S16 — Add audit log entries for failed logins, OTP failures, password resets
- [ ] S17 — Fix Hydra NetworkPolicy to allow rediensiam pod on port 4444
- [ ] S18 — Add `ephemeral-storage` limits to Dragonfly/Postgres pods
- **Status:** pending

### Phase 5: Verification
- [ ] All critical issues resolved
- [ ] Build succeeds
- [ ] Deploy to dev environment with deploy-dev.sh --dev
- [ ] Smoke test auth flows
- **Status:** pending

## Key Questions
1. Does the team use Sealed Secrets or an external vault for managing `values.secret.yaml`?
2. Is there a staging environment to test the Hydra secret rotation safely?
3. Which transactional email provider (if any) is preferred for S4?

## Decisions Made
| Decision | Rationale |
|----------|-----------|
| Fix criticals first in order C1→C15 | Account takeover + credential exposure are highest risk |
| Use HKDF for key derivation (C6) | Standard approach; avoids introducing new key config burden |
| Phases match severity tiers | Allows partial deployment of fixes incrementally |

## Errors Encountered
| Error | Attempt | Resolution |
|-------|---------|------------|
| — | — | — |
