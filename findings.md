# Findings & Decisions — RediensIAM Production Readiness

## Requirements
- Fix all 55 issues from production readiness audit (2026-04-17)
- Prioritize by severity: Critical (15) → Important (22) → Suggestions (18)
- Do NOT break existing auth flows during fixes
- Use `deploy-dev.sh --dev` for all deployments

## Key File Locations
- Backend: `src/Controllers/AuthController.cs`, `src/Services/`, `src/Config/AppConfig.cs`
- Login SPA: `frontend/login/src/`
- Admin SPA: `frontend/admin/src/`
- Infra: `deploy/rediensiam/` (Helm chart, values.yaml, templates/)
- Config: `src/appsettings.json`, `deploy/rediensiam/values.secret.yaml`

## Critical Issues Summary
| ID | File | Issue |
|----|------|-------|
| C1 | `AuthController.cs` ~L1186 | Account takeover via unverified email social link |
| C2 | `NotificationService.cs` ~L27, L243 | OTP in plaintext logs |
| C3 | `appsettings.json` | Weak Argon2 params override secure defaults |
| C4 | `deploy/values.secret.yaml` | Creds committed to git |
| C5 | `appsettings.json` | Empty TOTP encryption key — runtime crash |
| C6 | `AppConfig.cs` | Single key for all crypto purposes |
| C7 | `WebhookController.cs` ~L236 | Admin webhook SSRF no IP check |
| C8 | `WebhookController.cs` | SSRF DNS rebinding at delivery time |
| C9 | `WebhookService.cs` ~L188 | Empty secret → empty signature header |
| C10 | `OtpCacheService.cs` | No OTP brute-force lockout |
| C11 | `AuthController.cs` MFA paths | No session rotation after MFA |
| C12 | `Program.cs` | Admin GET bypass of GatewayAuthMiddleware |
| C13 | `deploy/values.yaml` | Hydra no stable signing secret |
| C14 | `SamlService.cs` | Cert validation = None |
| C15 | `SamlService.cs` | SAML MetadataUrl SSRF |

## Technical Decisions
| Decision | Rationale |
|----------|-----------|
| HKDF for key derivation (C6) | Standard RFC 5869; derive TOTP/webhook/SMTP/HMAC keys from one master key |
| Redis INCR Lua for rate limiting (I2) | Atomic increment+check prevents TOCTOU race |
| Polly for Hydra/Keto resilience (I9) | Already in .NET ecosystem; retry + circuit breaker patterns |
| EF Core Migrations for schema (S11) | Replaces EnsureCreated; enables rollback and versioning |

## Issues Encountered
| Issue | Resolution |
|-------|------------|
| — | — |

## Resources
- Audit report: conversation context (2026-04-17)
- Deploy instructions: `deploy-dev.sh --dev` (see feedback memory)
- OWASP Argon2 minimums: t=3, m=65536, p=4
- Hydra system secret: must be stable base64 string ≥ 16 bytes
