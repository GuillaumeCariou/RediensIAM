# Step 4 — Critical fixes applied

**Branch:** `security/audit-2026-07-28` · **Working tree, not committed**
**Scope:** CVSS ≥ 7.0 code-layer findings, plus R-09 (5.4) pulled forward for the C-6 ordering hazard.
**Suite:** `dotnet test tests/RediensIAM.IntegrationTests` — **1151 passed, 0 failed, 0 skipped, 3 m 05 s**
(baseline before this step on the same tree: 1118 passed, 0 failed, 2 m 23 s).
Rust SDK: `cargo test` — **6 unit + 1 doc test, all passing.**

Nine findings closed. Nothing was half-applied. Three deliberate non-fixes and four breaking
contract changes are listed at the end — **integrators must read §Breaking changes.**

---

## Summary

| Finding | CVSS | Status | Landing point |
|---|---|---|---|
| **R-23** tenant role names unvalidated | 8.2 | Fixed | `Roles.ProjectRoleNameError` + both create paths |
| **T-N3** role claims unnamespaced across tenants | 7.4 | Fixed | `AuthController.GetConsent` — the single issuance point |
| **R-24** MFA factor takeover | 8.1 | Fixed | `AccountController.RequireReauthAsync` + 5 call sites |
| **T-N2** no audit on MFA mutations | 7.0 | Fixed | 9 `audit.RecordAsync` calls in `AccountController` |
| **R-28** Rust SDK FNV-1a cache key | 7.4 | Fixed | `cache_key` → SHA-256 |
| **R-22** SA controller authorises from the snapshot; unbounded PATs | 7.2 | Fixed (3/3 residuals) | class filter + PAT clamp + scoped authz cache |
| **R-14 / T-N5** trust anchors in a mutable DB row | 8.6 | Fixed | `InstanceConfiguration.ToDict` deletion + `AppConfig` clamps |
| **T-N4** `audit_retention_days` accepts 0/negative | 7.1 | Fixed | floor at both write paths **and** at the delete |
| **R-09** tenant `custom_css` unvalidated | 5.4 | Fixed | `LoginThemeValidator`, wired into all 3 write paths |

---

## R-23 — Tenant role names collide with management role names (8.2)

**What changed.** Role names are validated at creation, on both paths that can create one.

- `src/Config/Roles.cs` — `Roles.Management` now holds the single list of management names, and
  `SystemAdminController.KnownManagementRoles` is an alias of it, so there is still exactly one
  list (the instruction was explicit about not inventing a second). `Roles.ProjectRoleNameError`
  rejects: empty, > 64 chars, containing `/` (the namespace separator), or **case-insensitively**
  equal to a management name. Case-insensitivity matters because a downstream resource server
  comparing case-insensitively would otherwise honour `Super_Admin`.
- `src/Controllers/ProjectController.cs` — `POST /project/roles` guards before touching the DB.
- `src/Controllers/SystemAdminController.cs` — `POST /admin/projects/{id}/roles` likewise.

**Why there.** These are the only two writers of `Roles.Name`. Guarding at creation means the
guard also protects the *read* paths that copy the name into a token, without a second check
having to be remembered in each of them.

**Regression test.** `Tests/Regression/ClaimForgeryRegressionTests.cs` — reserved name refused on
both paths (theory over all three names plus `SUPER_ADMIN`), `/` refused, ordinary name still
accepted, and the DB asserted to hold no such row.

**Residual risk.** Roles created *before* this fix keep their names. Reservation only protects
roles created after it — which is exactly why T-N3 below had to land as well: namespacing makes a
pre-existing role named `super_admin` harmless because it is emitted as `{project_id}/super_admin`.
A migration sweeping existing `Roles` rows for reserved names was not run; the namespacing makes it
unnecessary for security, but an operator wanting clean data should run one.

---

## T-N3 — Role claims unnamespaced across tenants (7.4)

**What changed.** `src/Controllers/AuthController.cs` (`GetConsent`, tenant branch) now emits
tenant roles through `Roles.ProjectRoleClaim(projectId, name)`, i.e. `{project_id}/{name}`. The
admin-client branch is untouched: it builds `adminRoles` from Keto, never from tenant strings, so
management names stay bare — which is what makes them distinguishable.

**Why there.** `GetConsent` is the *only* place tenant role names enter a token
(`grep AcceptConsentAsync` returns two call sites, both in this method). Fixing it there means the
introspection surface, the JWKS/local-validation path and all three SDKs inherit the fix without a
line of change each. `IntrospectionController` returns `claims.Roles` verbatim, so it now returns
qualified names automatically, and its management-role strip still matches exactly the three bare
names it is meant to.

The knock-on effect is the one §4.3 of the architecture review asked for, achieved with no SDK
signature change: the .NET SDK maps roles onto `ClaimTypes.Role` verbatim, so
`[Authorize(Roles = "admin")]` now fails closed for every tenant instead of matching all of them.
`HasRole`/`has_role` still exist and are now *safe by construction* — they can only match a
management role. `HasProjectRole(projectId, role)` / `has_project_role` were added for the tenant
case in the .NET and Rust SDKs.

**Regression test.** `ClaimForgeryRegressionTests.Consent_EmitsTenantRolesQualifiedByProject`
inspects the actual body RediensIAM sends to Hydra's consent-accept (new `HydraStub.AcceptedConsentBody`
helper) and asserts the qualified form is present and the bare form is not. Plus
`tenant_roles_do_not_match_across_projects` in the Rust SDK.

**Residual risk.** The introspection response still carries `roles` as **strings**, not objects
(§4.3 item 4). Namespacing achieves the boundary; the object shape would additionally let a
consumer discover the scoping rather than have to know it. Left as structural debt (S-2) — it is a
larger wire-contract change than this step should make on top of the one already made.
`aud` is still not mandatory (§4.2) — untouched, see §Not fixed.

---

## R-24 — MFA factor takeover with a bearer token alone (8.1)

**What changed.** `src/Controllers/AccountController.cs` gained one helper,
`RequireReauthAsync(User, MfaReauth?)`, applied at every point where an **existing** factor is
replaced or destroyed:

| Endpoint | Guarded when |
|---|---|
| `POST /account/mfa/totp/confirm` | `user.TotpEnabled` — i.e. an overwrite, not a first enrolment |
| `POST /account/mfa/backup-codes` | always (regeneration invalidates every existing code) |
| `DELETE /account/mfa/phone` | `user.PhoneVerified` |
| `DELETE /account/mfa/webauthn/credentials/{id}` | always |

Proof is `{ "current_password": … }` or `{ "totp_code": … }` against the *existing* factor. The
TOTP path goes through the same `OtpCacheService` anti-replay used on login, so an observed code
cannot be reused. Failures charge the `LoginRateLimiter` under a `mfareauth` key, mirroring
`ChangePassword`.

**Why there.** One helper, four call sites, rather than four inlined checks — and it is placed on
the mutation, not on `setup`, because `setup` persists nothing. First enrolment is deliberately
still a one-step flow: it is not a takeover.

**Regression test.** `Tests/Regression/MfaTakeoverRegressionTests.cs` — the full attack: enrol a
second authenticator against an account that already has TOTP, confirm, and assert 401 **and that
`user.TotpSecret` is byte-identical afterwards**. Plus the positive path with `current_password`,
first-enrolment-still-works, and refusal on backup codes / phone / passkey.

**Residual risk.** An account with **no password and no TOTP** (passkey-only, or social-only) has
nothing to re-authenticate against, so the guard steps aside for it — requiring a factor the user
cannot have would lock them out of their own credential list. That leaves passkey deletion
unguarded for passkey-only accounts. Closing it properly needs a WebAuthn re-auth assertion
(`fido2.GetAssertionOptions` → verify), which is a new flow, not a guard; recorded as follow-up.

---

## T-N2 — No audit record on any MFA mutation (7.0)

**What changed.** `AccountController` had exactly one `audit.RecordAsync`. It now records:
`user.mfa.totp_setup_started` (with `replacing_existing`), `user.mfa.totp_enabled` /
`user.mfa.totp_replaced`, `user.mfa.backup_codes_regenerated`, `user.mfa.phone_verified`,
`user.mfa.phone_removed`, `user.mfa.passkey_registered`, `user.mfa.passkey_removed`,
`user.social_account_unlinked`, `user.sessions_revoked`, `user.session_revoked`.

**Why there and not as a filter.** S-3 proposes an audit action filter. A filter would have to
infer action names, org scope and target ids generically across `/account`, `/org`, `/project`,
`/service-accounts` and `/api` — that is a sweeping change with real risk of mislabelled or
missing records, and rule 1 says prefer the instance fix in that case. Explicit calls, nine lines,
matching the existing `:106` pattern. **Structural debt recorded: S-3 still stands** — the reason
this finding existed is "someone forgot", and explicit calls do not stop the next omission.

`user.mfa.totp_setup_started` is recorded even though nothing is persisted: an enrolment started
against an account that already has TOTP is the first observable step of a takeover, and it is the
only event that fires if the attacker never completes.

**Regression test.** `MfaTakeoverRegressionTests` asserts the audit row exists for setup, replace
and backup-code regeneration.

**Residual risk.** Audit writes are best-effort in the sense that they are not in the same
transaction as the mutation (by design — `AuditLogService` uses its own DbContext scope so a
caller's uncommitted state cannot ride along). A crash between the two loses the record.

---

## R-28 — Rust SDK keyed its authorisation cache on 64-bit FNV-1a (7.4)

**What changed.** `sdk/rust/rediensiam-client/src/lib.rs` — `cache_key` is now SHA-256 hex, and
`sha2 = "0.11"` is a direct dependency. The comment that disclaimed the security property is
replaced with one that states it: the map returns a full `TokenInfo`, roles included, *before* any
server call, so a collision is authentication as that token.

This is the .NET SDK's approach (`RediensIamClient.CacheKey`, SHA-256) copied verbatim in
substance, as instructed.

**Regression test.** `cache_key_is_a_sha256_digest` is a known-answer test pinning SHA-256("") and
the hex encoding — it fails if anyone swaps the algorithm back or changes the encoding.

**Residual risk.** A new dependency (`sha2`, RustCrypto, pure Rust, no C). The alternative was
implementing SHA-256 inline (~60 lines of hand-rolled crypto) or keying on the raw token, both
worse. `sha2` adds 6 transitive crates; the lockfile is updated in the tree.

---

## R-22 — Live authorisation not universal; unbounded PATs (7.2)

All three residuals from step 1 are closed.

**Residual 1 — the missing filter.** `src/Controllers/ServiceAccountController.cs` now carries
`[RequireManagementLevel(ManagementLevel.ProjectAdmin)]` at class level. ProjectAdmin is the least
privileged level any action here admits; each action's own `Level` switch still applies on top. A
revoked administrator now loses `/service-accounts/*` within the 30 s live-authorisation window
instead of at token expiry.

**Residual 2 — the cache key omitted the org scope.**
`src/Services/LiveAuthorizationService.cs` — the OrgAdmin decision depends on `claims.OrgId`, which
was not part of the cache key. The scope now travels in the cached **value** (`"1|{orgId}"`) and a
mismatch is treated as a miss. Deliberately not put in the key, so `InvalidateAsync(userId)` can
still drop every decision for a user without enumerating orgs.

**Residual 3 — `ProjectAdmin` checked without a project.** Left as-is. The check is
"manager of *some* project"; scoping to the specific project is done correctly by each controller
(`ProjectController.GetProjectAsync`). Making the live check project-aware means threading the
project through `IsStillGrantedAsync` from every call site — that is S-1's `GrantedLevel` refactor,
not a surgical fix. **Recorded as structural debt, see §Not fixed.**

**Unbounded PATs.** `POST /service-accounts/{id}/pat` clamped to `AppConfig.MaxPatLifetimeDays`
(default 365, itself clamped 1–730, `Security:MaxPatLifetimeDays`). An absent or over-long expiry
is clamped rather than rejected so existing callers keep working; an expiry in the past is a 400.

**Regression tests.** `Tests/Regression/TrustAnchorRegressionTests.cs` —
`ServiceAccounts_AfterTheKetoGrantIsRevoked_AreRefused` (allow → 200, revoke + flush → 403),
`GeneratePat_WithNoRequestedExpiry_IsBounded`, `GeneratePat_WithAnExpiryInThePast_IsRefused`.

**Residual risk.** Revocation lag is still up to `LiveAuthorizationService.CacheTtlSeconds` (30 s).
PATs minted before this change still have `expires_at = null`; nothing sweeps them. An operator
should query `personal_access_tokens WHERE expires_at IS NULL` and set a bound.

---

## R-14 / T-N5 — Runtime trust anchors and security parameters in a mutable DB row (8.6)

**What changed — mostly a deletion.**

`src/Config/InstanceConfiguration.cs` no longer emits, and no longer writes,
`App:TrustedProxies`, `Hydra:AdminUrl`, `Hydra:PublicUrl`, `Keto:ReadUrl`, `Keto:WriteUrl`. The
columns remain on `Instance` (no migration), they are simply no longer part of the configuration
the process reads. Those five keys now come from env/appsettings only — which is already how
`deploy/rediensiam/templates/deployment.yaml:50-60` supplies them, so no deployment change is
needed. `App:PublicUrl` and `App:AdminSpaOrigin` were **deliberately kept** in the row: they are
issuer/redirect identity that operators reconfigure through this mechanism, and the review's item 1
names only lines `:119` and `:124-127`.

`src/Config/AppConfig.cs` clamps every security parameter that stays DB-settable:
`MaxLoginAttempts` 1–10, `LockoutMinutes` 1–1440, `ArgonTimeCost` ≥ 2, `ArgonMemoryCost` ≥ 19456
(the OWASP Argon2id minimum), `ArgonParallelism` 1–16, `PatCacheTtlMinutes` 0–15,
`AuditRetentionDays` 90–3650.

**Why there.** The provider is the single boundary between the row and `IConfiguration`; the clamps
are on the `AppConfig` property, which is the single read path for each value. Neither needed a
call-site change.

**Regression tests.** `TrustAnchorRegressionTests.InstanceConfiguration_NeverEmitsTrustAnchors`
builds a provider against a throwaway instance id with every anchor set to `http://attacker.invalid`
and asserts `TryGet` returns false for all five, while operational config (`Smtp:FromName`) still
loads. `SecurityParameters_AreClampedToASafeRange` feeds hostile values to `AppConfig`.

**Residual risk.**
- The clamps are silent — nothing logs when a bound fires (review item 2 asked for a log). `AppConfig`
  is constructed before logging is available; a startup diff would be the right place. Not done.
- Items 3 (validate anchors against a hostname allowlist at startup), 4 (audit the config load) and
  the `ConfigVersion` tamper-detection gap are **not** addressed — see §Not fixed.
- The shared `iam` Postgres role across app/Hydra/Keto is unchanged; that is deployment-layer.

---

## T-N4 — `audit_retention_days` accepts 0/negative (7.1)

**What changed.** A floor of `AppConfig.MinAuditRetentionDays` (90) enforced in three places:

- `src/Controllers/OrgController.cs` `PATCH /org/settings` — 400 `audit_retention_too_short`;
- `src/Controllers/SystemAdminController.cs` `PATCH /admin/organizations/{id}` — same;
- `src/Services/AuditLogRetentionService.cs` — `AppConfig.ClampRetention` applied to the effective
  value before computing the cutoff.

`-1` still means "reset to the global default" on both write paths.

**Why all three.** The two controllers are the trust boundary and must reject, not silently
correct. The retention service is the only code that *deletes*, so it also clamps — that covers
rows written directly in the database (the same primitive T-N5 describes) and any future writer.

**Regression tests.** `TrustAnchorRegressionTests.OrgSettings_AuditRetentionBelowTheFloor_IsRefused`
(theory: 0, −7, 1) asserts both the 400 and that the row is unchanged;
`OrgSettings_MinusOne_StillResetsToTheGlobalDefault` protects the reset semantics.

**Residual risk.** Retention is still enforced by deletion with no WORM/append-only export
(S-3's second half). A SuperAdmin can still shorten retention to 90 days.

---

## R-09 — Tenant `custom_css` unvalidated server-side (5.4) — fixed **before** any CSP work

C-6 says fixing R-26's CSP re-arms this. It is now closed ahead of step 6.

**What changed.** New `src/Services/LoginThemeValidator.cs`. It **refuses** rather than sanitises:
any of `/*`, `\`, `@`, `url(`, `image-set(`, `attr(`, `expression(`, `<` — matched after stripping
whitespace and lowercasing, so `URL (` and `u r l(` do not slip past — plus a 20 KB cap. It also
performs the `logo_url` HTTPS check.

Wired into **all three** write paths: `ProjectController.UpdateInfo`, `OrgController.UpdateProject`
(which previously reached `ApplyLoginTheme` with no validation at all), and
`SystemAdminController.AdminUpdateProject` (a third path step 1 did not name).

**Why refusal, and why there.** The client sanitiser's own header says a real parser is required;
adding a CSS parser dependency to validate a field a tenant admin hand-writes is disproportionate,
and a denylist-sanitiser has to be right every time. Refusal is sound: with `url()`, `@import`,
`image-set()` and `attr()` all rejected, a CSS keylogger has no way to get data off the page.
`<` is rejected because it is not valid CSS; `>` is not, because it is the child combinator.

**Bonus correctness fix.** The old `ValidateLoginTheme` checked `logoVal is string`, but request
bodies bind `Dictionary<string, object>` values as `JsonElement` — so the `logo_url` HTTPS check
**never fired on any path**. `LoginThemeValidator.AsString` handles both, so it now works.

**Regression tests.** `TrustAnchorRegressionTests` — theory over five hostile CSS payloads on
`/project/info` asserting 400 *and* that nothing was persisted, hostile CSS on the org route,
non-HTTPS logo on the org route, and ordinary CSS still accepted.

**Residual risk.** UI redressing and phishing-grade defacement remain possible — a tenant admin
legitimately controls their own login page's appearance, and no server-side rule separates
"restyled" from "impersonated". `@media` is rejected along with every other at-rule, matching what
the client sanitiser already did; tenants lose responsive custom CSS.

---

## Breaking changes — read this if you integrate

### 1. `ext.roles` shape changed (T-N3) — **loudest item in this document**

Tenant roles are now emitted as `{project_id}/{name}`. A role named `admin` in project
`7f3ac1…` appears as `7f3ac1…/admin`, never as `admin`. Management role names remain bare.

This affects **every resource server that reads `ext.roles`**, whether via `/api/introspect` or by
local JWKS validation — the population step 2 records RediensIAM cannot inventory. Concretely:

- `roles.contains("admin")` now returns false for every tenant. **Fails closed** — this is the
  intended direction, but it *will* break working authorisation code.
- .NET SDK: `ClaimTypes.Role` now carries the qualified string, so `[Authorize(Roles = "admin")]`
  matches nothing. Use `TokenInfo.HasProjectRole(projectId, "admin")`, or authorise on
  `$"{projectId}/admin"`.
- Rust SDK: use `info.has_project_role(project_id, "admin")`. `has_role` is unchanged in signature
  and now matches management roles only.
- No deprecation window was provided. Doing this additively (a new `tenant_roles` claim beside the
  old `roles`) would have left the vulnerable claim in place and not closed T-N3 at all.

`docs/INTEGRATION.md` is updated with the migration text (the old "open weakness" warning is
replaced).

### 2. MFA endpoints now require re-authentication (R-24)

Four endpoints changed their request contract:

| Endpoint | New requirement |
|---|---|
| `POST /account/mfa/totp/confirm` | `{"code":…, "reauth":{"current_password":…}}` **when replacing an existing factor** |
| `POST /account/mfa/backup-codes` | body `{"current_password":…}` or `{"totp_code":…}` |
| `DELETE /account/mfa/phone` | same, as a request body, when a phone is verified |
| `DELETE /account/mfa/webauthn/credentials/{id}` | same, as a request body |

Refusal is `401 {"error":"reauthentication_required","methods":[…]}`, where `methods` tells the
client which proofs the account can supply.

**The admin console does not yet send it.** `frontend/admin/src/api.ts:249` (`removePhone`),
`:263` (`deleteWebAuthnCredential`), `:283` (`confirmTotp`), `:286` (`regenerateBackupCodes`) all
call these with no body. Those four flows will return 401 until step 6 adds a password prompt.
This is deliberate — the frontend is step 6's scope and the backend fix is the priority — but it is
a working-UI regression and must not be forgotten. First TOTP enrolment is unaffected.

### 3. Service-account PATs now expire (R-22)

`POST /service-accounts/{id}/pat` with no `expires_at` used to mint a permanent credential; it now
returns one bounded to `Security:MaxPatLifetimeDays` (365 default). Any automation that assumed a
non-expiring PAT needs a rotation plan. Existing PATs are untouched — including the null-expiry
ones, which an operator should audit.

### 4. Reserved and validated role names (R-23)

`POST /project/roles` and `POST /admin/projects/{id}/roles` now return
`400 {"error":"role_name_reserved"|"role_name_invalid_character"|"role_name_too_long"|"role_name_required"}`.
Existing roles are not migrated.

Minor: `/service-accounts/*` now answers **403** rather than 401 to a token with no management
level (the filter runs before the action body). Two existing tests were updated accordingly.

---

## Deliberately not fixed

1. **S-1 — `GrantedLevel` type / default-deny authorisation.** Would close R-22 residual 3 and
   I-02's whole class. It makes `GetManagementLevel()` internal and breaks every call site until
   each is threaded through `LiveAuthorizationService` — a sweeping refactor across six
   controllers. Rule 1 says prefer the instance fix in that case, and I did (the class filter).
   **What it would take:** introduce the type, make `TokenClaims.Roles` internal to the auth layer,
   convert `ServiceAccountController.Level`, `ProjectController.ProjectId` and
   `OrgController`'s `IsSuperAdmin` to consume it, and pass the project/org scope into
   `IsStillGrantedAsync`. Estimate: a day, with its own test pass.
2. **S-2's remaining half — `aud` mandatory, `ver` claim, introspection roles as objects.** Named
   in §4.2 as what bounds every token-theft chain. Each is a further wire-contract change on top of
   the one already made in this step; shipping two breaking claim changes in one commit with no
   deprecation window is worse for integrators than shipping the security-critical one alone.
   **What it would take:** request a per-RS audience at consent, add `ver`, make the SDKs reject
   audience-less tokens, and give `IntrospectionResult.Roles` an additive object field. Needs a
   published deprecation window.
3. **T-N6 / I-07 — introspection surface has no tenant scoping.** Rated 6.5, below this step's
   threshold, and it is a design change (what *should* a tenant's SA be allowed to introspect?),
   not a guard. Untouched.
4. **R-14 items 3 and 4** — startup validation of anchors against a hostname allowlist, and
   auditing the config load. Both are additions rather than deletions and neither is needed once
   the anchors are env-only, which is the part that closes the finding.
5. **Argon2 clamp logging** — no warning is emitted when a bound fires. `AppConfig` is built before
   logging exists.
6. **R-06** — untouched as instructed. `deploy/rediensiam/values.secret.yaml` still holds
   `changeme`, the all-zero-adjacent bootstrap password and the literal
   `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`. **Carry to step 10.**
7. **Deployment-layer items** R-16, R-02, R-05, R-15, R-19, R-07/R-08 — out of scope, step 9/10.
   No file under `deploy/` was modified.

---

## Files changed

**Backend**
`src/Config/Roles.cs`, `src/Config/AppConfig.cs`, `src/Config/InstanceConfiguration.cs`,
`src/Controllers/AccountController.cs`, `src/Controllers/AuthController.cs`,
`src/Controllers/OrgController.cs`, `src/Controllers/ProjectController.cs`,
`src/Controllers/ServiceAccountController.cs`, `src/Controllers/SystemAdminController.cs`,
`src/Services/AuditLogRetentionService.cs`, `src/Services/LiveAuthorizationService.cs`,
`src/Services/LoginThemeValidator.cs` *(new)*

**SDKs**
`sdk/rust/rediensiam-client/src/lib.rs`, `sdk/rust/rediensiam-client/Cargo.toml`,
`sdk/rust/rediensiam-client/Cargo.lock`, `sdk/dotnet/RediensIAM.Client/RediensIamClient.cs`

**Tests**
`tests/…/Tests/Regression/ClaimForgeryRegressionTests.cs` *(new)*,
`tests/…/Tests/Regression/MfaTakeoverRegressionTests.cs` *(new)*,
`tests/…/Tests/Regression/TrustAnchorRegressionTests.cs` *(new)*,
`tests/…/Infrastructure/HydraStub.cs` (added `AcceptedConsentBody`),
`tests/…/Infrastructure/SeedData.cs` (added `DefaultPassword`),
`tests/…/Tests/Account/MfaSetupTests.cs`, `tests/…/Tests/Account/AccountExtendedTests.cs`,
`tests/…/Tests/ServiceAccounts/ServiceAccountBranchCoverageTests.cs`,
`tests/…/Tests/System/OrganisationTests.cs` — the last four encoded the pre-fix behaviour and were
updated to the new contract, not weakened.

**Docs**
`docs/INTEGRATION.md` — the `ext.roles` warning replaced with migration guidance.

Pre-existing uncommitted work on this branch (`sdk/README.md`,
`SystemAdminController.CreateHydraClient` client-id allowlist, and its test) was left in place; the
`Roles.Management` refactor touches the same file but not that hunk.

---

## Ordering notes for the next steps

- **C-6 is discharged.** R-09 is fixed server-side. Step 6 may now widen the CSP without re-arming
  tenant CSS injection.
- **C-7 still stands.** Fixing R-26 turns a broken admin surface into a working one on NodePort
  30501. Sequence it with R-05.
- **Step 6 must also add the MFA re-auth prompt** to the admin console — see §Breaking changes 2.
