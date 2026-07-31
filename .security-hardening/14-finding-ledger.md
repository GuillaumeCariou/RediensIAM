# Step 14 — Finding ledger: every finding, verified against the code

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **HEAD:** `71cbc26`
**Method:** every row was checked in source, config or against the live cluster. No report's
"Fixed" was accepted as evidence. Where I could not find a fix, the row says OPEN or UNVERIFIED,
not CLOSED.

**Evidence I generated myself, not quoted:**

| Check | Result |
|---|---|
| `dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true` | **Passed! Failed: 0, Passed: 1221, Skipped: 0** — 3 m 17 s |
| `dotnet list src/RediensIAM.csproj package --vulnerable --include-transitive` | no vulnerable packages |
| `npm audit` in `frontend/admin` and `frontend/login` | **9 vulnerabilities (1 low, 8 high)** each, incl. `react-router` |
| `kubectl get {pods,svc,ingress,networkpolicy,middleware,cronjob,clusterrole}` | see below |
| `kubectl exec rediensiam-postgres-0 -- psql … pg_roles / pg_hba.conf` | `iam` is still SUPERUSER; `local all all trust` |
| live HTTP probes against `iam.localhost` via the ingress | CSP, `/admin` deny, `/health` |

**A deploy landed ~8 minutes before this audit began.** Pod age 8m15s, service age 0s while I was
reading — some service readings were taken mid-recreate and are marked. The important consequence:
**P-07 ("deployed ≠ source") is now closed.** Every control step 13's `verify-deployment.sh` listed
as failing on 2026-07-31 is now live and I verified it directly:

| Was failing (13-monitoring-siem.md) | Live now |
|---|---|
| V-01 registry on `0.0.0.0:5000` | `docker inspect registry` → `5000/tcp -> 127.0.0.1` |
| V-02 maester ClusterRole with cluster-wide Secrets `create` | `kubectl get clusterrole \| grep maester` → **none** |
| V-03 maester pod running | `kubectl get deploy -A \| grep maester` → **none** |
| V-04 `/admin/` 200 on public host | `curl -H 'Host: iam.localhost' …/admin/` → **403**; `/org` 403; `/service-accounts` 403; `/api/introspect` 401 |
| V-07/08 mutable tag + `pullPolicy: Always` | image `localhost:5000/rediensiam@sha256:2c32df17…`, `IfNotPresent` |
| V-09 no seccomp | pod `securityContext` → `{"seccompProfile":{"type":"RuntimeDefault"}}` |
| V-15 no default-deny ingress policy | `rediensiam-default-deny-ingress` present (8m31s) |
| V-17 CSP missing `script-src`/`base-uri`/`form-action` | live header carries all of them (full string in R-26 below) |

Plus: `automountServiceAccountToken=false`, `rediensiam-backup` CronJob `0 3 * * *` exists,
6 NetworkPolicies, `rediensiam-public-admin-deny` Ingress + `rediensiam-deny` Middleware.

---

## Legend

- **CLOSED** — found the fix in code/config, cited by `file:line` or test name.
- **CLOSED-PENDING-DEPLOY** — fixed in the repo, not live (only applies to prod-gated items now).
- **PARTIAL** — the named part is not fixed.
- **OPEN** — not fixed. Marked *deliberate* (with the report that decided it) or *oversight*.
- **UNVERIFIED** — I could not check it.

`⚠ no test` marks a finding recorded as fixed for which **no test exists**. That is exactly how
P-02 survived steps 4, 6 and 8, so it is called out on every row where it applies.

---

## 1. `01-vulnerability-scan.md` — R-01…R-32

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **R-01** | Orphaned Keto `org_admin` grant reaches system service accounts | CLOSED (scan §R-01) | **CLOSED** | `src/Controllers/ServiceAccountController.cs:50,73` — `sa.UserList.OrgId != null && … == CallerOrgId`. Tests `OrphanedGrantRegressionTests.OrgAdminWithNoOrgClaim_CannotReachSystemServiceAccounts`, `LiveAuthorization_OrgAdminWithoutOrgClaim_IsNotGranted` |
| **R-02** | Auth surface over cleartext HTTP | Fixed (09) | **CLOSED-PENDING-DEPLOY (prod) · OPEN by design (dev)** | `deploy/rediensiam/values.prod.yaml:20-22` `tls.enabled: true` + `clusterIssuer: letsencrypt`; `templates/ingress.yaml:50` `redirectScheme`. Dev deliberately cleartext (`values.dev.yaml:18-23`, "the one place R-02 is not fixed, gated to dev"). Live dev ingress: `PORTS 80` only. Prod has not been deployed from this branch — I cannot verify it live |
| **R-03** | `react-router` 7.13.1, 12 advisories (judged not reachable as used) | OPEN — downgraded | **OPEN** *(deliberate: scan §R-03 downgraded it; no step owned the upgrade)* | Installed `react-router@7.13.1` in both SPAs. My `npm audit --json`: `react-router high`, `react-router-dom high (isDirect)`. Unchanged |
| **R-04** | SSRF via OIDC discovery-derived endpoints | CLOSED (scan) | **CLOSED** | `src/Services/SocialLoginService.cs:409` `WebhookUrlValidator.IsPrivateOrReservedAsync`; `src/Program.cs:85,109,111` all three clients on `CreateSsrfSafeHandler`. Tests `RedirectAndSsrfRegressionTests.IsPrivateIp_*` |
| **R-05** | Admin port on NodePort, self-signed cert | Fixed (09) | **PARTIAL** | Chart default is now ClusterIP — `deploy/rediensiam/values.yaml:52-56`. Dev opts back into NodePort deliberately (`values.dev.yaml:8-14`); live `rediensiam-admin` is `NodePort 30501`, correct for dev. **Not fixed:** the prod admin cert is still self-signed — `values.prod.yaml:27` `clusterIssuer: selfsigned`, with the comment "a known click-through warning on the most privileged UI". `09-infra-security.md §8` lists "Real admin certificate" as still open |
| **R-06** | Hard-coded dev credentials incl. Hydra system secret | Fixed (10) | **CLOSED (code)** | `deploy/deploy.sh:136` `KNOWN_DEFAULTS='changeme\|CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS\|Admin1234!'`; `:243-249` hard `exit 1` in prod; dev generates a bootstrap password. Operator action outstanding on any existing install (rotate) — outside code |
| **R-07** | Generated prod secrets file, default permissions | Fixed (10) | **CLOSED** | `deploy/deploy.sh:157` `( umask 077`, `:190` `chmod 600 "${file}"`, `:241` unconditional `chmod 600` on the reuse path |
| **R-08** | Live SonarQube tokens in cleartext | Fixed (10) | **PARTIAL** | `sonar-scan.sh` no longer embeds a token: `:31-38` prompts and writes `.sonar.env` under `umask 077`; `:72` reads `$SONAR_TOKEN`. **The exposed token's revocation is an operator action I cannot verify** — `state.json` still lists "revoke SonarQube tokens" as outstanding |
| **R-09** | Tenant `custom_css` unvalidated server-side | Fixed (04) | **CLOSED** | `src/Services/LoginThemeValidator.cs:36-46,81-93`; wired at `ProjectController.cs:168`, `OrgController.cs:168`, `SystemAdminController.cs:577`. Tests `TrustAnchorRegressionTests.ProjectInfo_HostileCustomCss_IsRefused`, `OrgProjectUpdate_HostileCustomCss_IsRefused`, `ProjectInfo_OrdinaryCustomCss_IsStillAccepted` |
| **R-10** | Per-org SMTP host unvalidated; Tailscale range reachable | Fixed (05) | **CLOSED** | `src/Services/SmtpEndpointValidator.cs:39` `smtp_host_not_allowed`. Tests `BackendHardeningRegressionTests.OrgSmtp_HostInsideTheMeshOrLoopback_IsRefusedAndNotPersisted`, `OrgSmtp_NonSubmissionPort_IsRefused`, `OrgSmtp_CleartextSubmission_IsRefused`, `OrgSmtpTest_Failure_DoesNotReturnTheExceptionText` |
| **R-11** | SAML ACS not bound to the login challenge | CLOSED (scan) | **CLOSED** | `src/Controllers/SamlController.cs:39-54` — the IdP must belong to the challenge's project. Test `CrossTenantRegressionTests.SamlStart_IdpFromForeignProject_IsRejected` |
| **R-12** | — | superseded | **n/a** | Explicitly superseded by R-24 (`01-vulnerability-scan.md:23`) |
| **R-13** | PAT expiry not re-checked on the cached path | CLOSED (scan) | **CLOSED** | `src/Services/PatService.cs:152` `hit.ExpiresAt.HasValue && hit.ExpiresAt < UtcNow`. Test `OrphanedGrantRegressionTests.ExpiredPat_IsRejectedEvenWhenCached` |
| **R-14 / T-N5** | Runtime trust anchors in a mutable DB row; blast radius wider than recorded | Fixed (04) | **CLOSED** | `src/Config/InstanceConfiguration.cs:84` and `:114-125` — `Hydra:*Url`, `Keto:*Url`, `App:TrustedProxies` deliberately absent from the DB-sourced dict. Clamps at `src/Config/AppConfig.cs:25,47,48,57,64,118-119`. Tests `TrustAnchorRegressionTests.InstanceConfiguration_NeverEmitsTrustAnchors`, `SecurityParameters_AreClampedToASafeRange` |
| **R-15** | Postgres connections use `sslmode=disable` | Implemented but gated off (09) | **OPEN** *(deliberate: `09-infra-security.md §8` — "1h for `require`, +4h for `verify-full`")* | `deploy/deploy.sh:176,187` and `deploy/rediensiam/values.secret.yaml:16,24` still `sslmode=disable`. TLS templates exist in `templates/postgres.yaml` behind `postgres.local.tls.enabled`, off by default |
| **R-16** | Unauthenticated cleartext image registry | Fixed (09) | **PARTIAL** | Exposure closed: `deploy/deploy.sh:92` binds the registry to loopback (live: `5000/tcp -> 127.0.0.1`); digest pinning at `:266-268` and `values.yaml:6-13` (live image is a `@sha256:` digest, `pullPolicy: IfNotPresent`). **Not fixed:** the registry still has no TLS and no auth, and there is no cosign/admission policy. `09 §8` lists "Registry auth + TLS + signature verification" as open |
| **R-17** | `AllowedHosts: "*"` | Fixed, app side (05) | **CLOSED (app side)** | `src/Program.cs:28-42` derives the allowed host list from config and replaces the wildcard. Test `BackendHardeningRegressionTests.Request_WithAForeignHostHeader_IsRefused`, `Request_WithTheDeploymentsOwnHost_IsStillServed`. The chart still ships `AllowedHosts` (`deployment.yaml:48`) — intentional, the app narrows it |
| **R-18** | Rate limiting reads only the per-IP counter | Fixed (05) | **CLOSED** | `src/Controllers/AuthController.cs:279` `IsBlockedAsync(Ip, user.Id)`. Test `BackendHardeningRegressionTests.Login_WhenThePerUserBudgetIsSpentFromOtherAddresses_IsRefused` |
| **R-19** | Prod CORS allowlists `http://localhost:30501` | Fixed (09) | **CLOSED** | `deploy/rediensiam/values.prod.yaml:61-67` — origin removed, only `https://auth.ts.rediens.net` remains |
| **R-20** | SSRF re-validation is TOCTOU on DNS | Fixed (05) | **CLOSED** | `src/Controllers/WebhookController.cs:268-271` `CreateSsrfSafeHandler` with `ConnectCallback` pinning; applied to all three clients at `src/Program.cs:85,109,111`. Test `BackendHardeningRegressionTests.SsrfSafeHandler_RefusesToDialAPrivateAddress` |
| **R-21** | Dev-toolchain advisories (not shipped) | OPEN — not shipped | **OPEN** *(oversight — no step owned `npm audit fix`; `12-compliance-report.md` T-06 names it but 12 is a report, not a remediation step)* | My `npm audit`: `vite`, `undici`, `postcss`, `js-yaml`, `picomatch`, `brace-expansion` high; `@babel/core` low — **8 high per SPA**, `npm audit fix` available |
| **R-22** | Live authorisation re-check not universal (**finding A**) | Fixed, 3/3 residuals (04) | **CLOSED** | `src/Filters/RequireManagementLevelAttribute.cs:37-45` — `LiveAuthorizationService.IsStillGrantedAsync` on every request; `ServiceAccountController.cs:26` now carries the class-level filter. PAT lifetime clamped (`AppConfig.cs:64`). Tests `CrossTenantRegressionTests.ManagementApi_AfterRoleRevokedInKeto_IsRejected`, `ManagementApi_WhenKetoIsUnreachable_FailsClosed`, `TrustAnchorRegressionTests.ServiceAccounts_AfterTheKetoGrantIsRevoked_AreRefused`, `GeneratePat_WithNoRequestedExpiry_IsBounded`. Note the *structural* fix (S-1) was not done — containment is still by convention |
| **R-23** | Tenant role names collide with management role names | Fixed (04) | **CLOSED** | `src/Config/Roles.cs:29-38` `ProjectRoleNameError` — case-insensitive reserved-name check. Tests `ClaimForgeryRegressionTests.CreateProjectRole_NamedAfterAManagementRole_IsRefused`, `AdminCreateProjectRole_…`, `PENTEST_HELD_ReservedRoleNames_AreRefusedOnTheTenantPath` |
| **R-24** | MFA factor takeover with a bearer token alone | Fixed (04), reopened+fixed (11b) | **CLOSED** | `src/Controllers/AccountController.cs:57` `RequireReauthAsync`, guarding 7 call sites (`:245,275,349,365,435,495`). Tests: all 8 in `MfaTakeoverRegressionTests` + `PENTEST_FAILING_P02_…` |
| **R-25** | Dev deploy crash-loops on empty `App__TrustedProxies` (**finding D**) | Just fixed | **CLOSED — but uncommitted** | `deploy/rediensiam/values.yaml:35` `trustedProxies: "10.42.0.0/16,10.43.0.0/16"` and the false comment replaced. `git status` shows `M deploy/rediensiam/values.yaml` — **this fix exists only in the working tree.** Live pod `rediensiam-685b67c778-hm49x` Running, 0 restarts, 8m |
| **R-26** | Admin console CSP blocks its own OIDC login and its fonts (**finding E**) | Fixed (06) | **CLOSED — live-verified** | Header pins the issuer: `src/Program.cs:426-435` (`connect-src 'self' {issuerOrigin}` on `/admin`). Meta relaxed and `frame-ancestors` removed: `frontend/admin/index.html:17`. Fonts self-hosted: `frontend/{admin,login}/src/main.tsx:3`; zero `fonts.googleapis.com` references remain. Live header on the cluster: `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data: https:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self';`. Tests `SecurityHeadersTests` |
| **R-27** | Ingress has no base-path support (**finding C**) | OPEN | **OPEN** *(half-deliberate: the scan's alternative "document dedicated-host-only" was taken)* | No `basePath` anywhere under `deploy/` (grep returns nothing). `docs/INTEGRATION.md:271` documents "give it a dedicated host". The chart itself still carries no note. Code unchanged |
| **R-28** | Rust SDK keys its authorisation cache on 64-bit FNV-1a | Fixed (04) | **CLOSED** | `sdk/rust/rediensiam-client/src/lib.rs:272` `Sha256::digest(token.as_bytes())`. Test `cache_key_is_a_sha256_digest` (`:305-309`) |
| **R-29** | Browser SDK puts the access token in the logout URL | Fixed (06) | **CLOSED** | `sdk/typescript/rediensiam-web/src/index.ts:224` — `id_token_hint`, not the access token; no bearer in the query string |
| **R-30** | SDKs do not validate discovery endpoints or require HTTPS | — | **OPEN** *(oversight — no step ever owned it; not in step 4/5/6/8/9/10/11b scope)* | TS: `sdk/typescript/rediensiam-web/src/index.ts:302-311` fetches `{issuer}/.well-known/openid-configuration` with no scheme check and returns the discovered endpoints unvalidated; constructor `:103` only checks non-empty. .NET: `sdk/dotnet/RediensIAM.Client/ServiceCollectionExtensions.cs:29-39` — `BaseUrl` non-empty only. Rust: `sdk/rust/rediensiam-client/src/lib.rs:158` — `base_url` non-empty only. **All three SDKs will happily talk `http://`** |
| **R-31** | Browser SDK `fetch()` attaches the token to any URL | — | **OPEN** *(oversight — same gap as R-30; step 6 touched this file for R-29 and `hasProjectRole` but not this)* | `sdk/typescript/rediensiam-web/src/index.ts:235-243` — `Authorization: Bearer` is set on whatever `input` the caller passes; no same-origin or allowlist check |
| **R-32** | No `seccompProfile` on any pod | Fixed (09) | **CLOSED — live-verified** | `deployment.yaml:22`, `postgres.yaml:78`, `dragonfly.yaml:52`, `backup.yaml:49`. Live: `kubectl get deploy rediensiam -o jsonpath='{…securityContext}'` → `{"seccompProfile":{"type":"RuntimeDefault"}}` |

### Informational I-01…I-11

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **I-01** | Unused `Handlebars.Net` on a tenant-controlled field | Fixed (05) | **CLOSED** | `grep Handlebars src/RediensIAM.csproj` → no match |
| **I-02** | `/admin` GET without `Authorization` bypasses the middleware | Fixed (05) | **CLOSED** | `src/Program.cs:366-371` — gates on `GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() != null`, not on the header. Tests `BackendHardeningRegressionTests.AdminApiGet_WithoutAuthorization_IsRefusedBeforeTheController`, `AdminSpaNavigation_WithoutAuthorization_StillLoads` |
| **I-03** | Dead `/preview` framing exemption | Fixed (05) | **CLOSED** | `grep preview src/Program.cs` → no match; `AddSecurityHeaders` (`:409-436`) has no exemption |
| **I-04** | `*.bak` not gitignored | Fixed (05/10) | **CLOSED** | `.gitignore:46-47` `*.bak`; no `.bak` under `deploy/rediensiam/` |
| **I-05** | Breach check fails open | Not fixed | **OPEN** *(deliberate: `05 §Deliberately left`; `12` §2.1.7 re-records it as an availability trade)* | `src/Services/BreachCheckService.cs:33-35` — `return 0; // fail open so outages don't block users` |
| **I-06** | `docs/ARCHITECTURE.md` stale in three/four places | Fixed (08) | **CLOSED** | `docs/ARCHITECTURE.md:9-20` now describes the live Keto re-check and its 30 s cache; `:225-232` control table matches the code |
| **I-07 / T-N6** | `/api/authorize` is an unscoped Keto oracle; introspection unscoped | Fixed (05) | **PARTIAL** | Introspection **is** scoped: `IntrospectionController.cs:45` `CallerOrgScope`, `:68` `IsInCallerScopeAsync`, audited as `api.introspect.out_of_scope`. Tests `Introspect_TokenFromAnotherOrganisation_IsNotResolved`, `Introspect_FromASystemServiceAccount_IsStillUnscoped`, `PENTEST_HELD_OrgScopedGatewayPat_…`. **Not fixed:** `Authorize` guards only the `System` namespace (`:110-118`); `body.Object` still reaches `keto.CheckAsync` unchecked at `:120`. That residual is P-05 |
| **I-08** | `/admin/system/health` leaks SMTP username and raw exception text | Partly fixed (05) | **PARTIAL** | Username fixed: `SystemHealthController.cs:193` returns `"none"`/`"configured"`. **Raw exception text still returned:** `:220` and `:243` both `return … ex.Message` |
| **I-09** | Rust SDK ignores the OS trust store | Not fixed | **OPEN** *(deliberate: `05 §Deliberately left`)* | `sdk/rust/rediensiam-client/Cargo.toml:10` — `features = ["json", "rustls-tls"]`, i.e. compiled-in webpki-roots. A private-CA deployment still will not validate |
| **I-10** | SAML pending state consumed before signature validation | Not fixed | **OPEN** *(deliberate: `05 §Deliberately left`)* | `src/Controllers/SamlController.cs:156` `ReadSamlResponse` → `:163` `GetAndDeletePendingAsync` → `:186` `Unbind`. Ordering is byte-for-byte what the scan reported |
| **I-11** | `SsoUrl` not scheme-validated → open redirect | Fixed (05) | **CLOSED** | `src/Services/SamlService.cs:85-86` — absolute HTTPS required. Test `BackendHardeningRegressionTests.SamlConfig_NonHttpsSsoUrl_IsRefused` |

---

## 2. `02-threat-model.md` — T-N1…T-N6 and chains C-1…C-9

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **T-N1** | `sa_` client-id prefix squatting | Fixed (state.json, "+2 tests") | **CLOSED** | `src/Controllers/SystemAdminController.cs:934-939` — `ReservedClientIdPrefixes = ["sa_", "client_"]` + charset allowlist; `:957-963` conflict check. Tests **do** exist: `SystemAdminBranchCoverageTests.CreateHydraClient_MalformedClientId_Returns400` (`:419`, theory incl. `sa_impersonator`, `client_admin_system`) and `CreateHydraClient_ClientIdAlreadyTaken_Returns409` (`:435`) |
| **T-N2** | No audit record on any MFA mutation | Fixed (04) | **CLOSED** | `src/Controllers/AccountController.cs:225,264,287,356,370,471,500` — all seven mutations record. Test `MfaTakeoverRegressionTests.TotpSetup_IsAudited_EvenBeforeAnythingIsPersisted`, `RegenerateBackupCodes_WithCurrentPassword_SucceedsAndIsAudited` |
| **T-N3** | Role claims unnamespaced across tenants | Fixed (04) | **CLOSED** | `src/Config/Roles.cs:44-46` `ProjectRoleClaim` → `{projectId}/{name}`; separator refused inside a name (`:34`). Test `ClaimForgeryRegressionTests.Consent_EmitsTenantRolesQualifiedByProject`, `CreateProjectRole_ContainingTheNamespaceSeparator_IsRefused`, `PENTEST_HELD_UnicodeConfusableRoleName_IsNamespacedNotBare` |
| **T-N4** | Tenant-controlled audit destruction | Fixed (04) | **CLOSED** | Floor + clamp: `src/Config/AppConfig.cs:113-119` (`MinAuditRetentionDays = 90`), and the delete path re-clamps: `src/Services/AuditLogRetentionService.cs:44` `AppConfig.ClampRetention(...)`. Tests `PENTEST_HELD_AuditRetentionBelowTheFloor_IsRefused`, `PENTEST_HELD_RetentionClampHoldsForHostileValues`, `TrustAnchorRegressionTests.OrgSettings_*` |
| **T-N5** | R-14 blast radius wider (lockout, Argon2, PAT TTL, retention) | Fixed (04) | **CLOSED** | Every parameter it named is now `Math.Clamp`ed at `src/Config/AppConfig.cs:25,47,48,57,64,118`. Test `TrustAnchorRegressionTests.SecurityParameters_AreClampedToASafeRange` |
| **T-N6** | `/api/introspect` and `/api/authorize` have no tenant scoping | Fixed (05) | **PARTIAL** | See I-07 — introspection scoped and audited; `authorize`'s `object` is not scoped (P-05) |

| Chain | Verified | Evidence |
|---|---|---|
| **C-1** role forgery → downstream admin | **CLOSED** | Both legs closed: R-23 (reserved names) **and** T-N3 (project-qualified claims). SDK read-side aligned: `sdk/typescript/…/index.ts:278-290` `hasProjectRole`; Rust/`.NET` project-qualified helpers. Pentest §1 executed and refused |
| **C-2** token theft → invisible account ownership | **CLOSED** | R-24 + T-N2 closed and tested. Its R-02 leg is prod-pending-deploy |
| **C-3** registry → code execution → both root secrets | **PARTIAL** | Entry narrowed (R-16 loopback + digest pinning, live) and the maester ClusterRole with cluster-wide Secrets `create` is gone (verified: no maester ClusterRole, no maester pod). **Recovery is still impossible** — S-10 was never built, and `state.json` records "C-3 still unrecoverable (ciphertexts carry no key id)" |
| **C-4** any DB write → total auth bypass → blinded | **PARTIAL** | The trust-anchor half is closed (R-14/T-N5). **The precondition is not:** `pg_roles` on the live DB → `iam\|t\|t\|t\|` (SUPERUSER, CREATEROLE, CREATEDB, no `VALID UNTIL`), shared by app + Hydra + Keto; `pg_hba.conf` → `local all all trust`. Anyone with `kubectl exec` on the Postgres pod is still superuser |
| **C-5** revoked admin → permanent system credential → cross-tenant oracle | **CLOSED** | R-22 (live re-check) + PAT lifetime clamp + T-N6 introspection scoping. Tests as cited above |
| **C-6** fixing R-26 activates R-09 | **CLOSED** | Ordering respected: R-09 shipped in step 4 *before* the CSP widened in step 6, and P-03 closed the reassembly path in 11b before the widening was accepted. `11b §3` re-verifies the CSP claim rather than assuming it |
| **C-7** fixing R-26 increases R-05 | **CLOSED** | `values.yaml:56` admin service `ClusterIP` by default; dev NodePort is an explicit dev-only override |
| **C-8** SDK endpoint trust + live cleartext ingress | **PARTIAL** | R-02 fixed for prod (pending deploy). **The SDK half is completely untouched — R-30 is open in all three SDKs**, which is the leg the threat model said it disagreed with the 4.2 rating over |
| **C-9** operator-workstation origin trusted by production | **CLOSED** | `values.prod.yaml:61-67` — `http://localhost:30501` removed from Hydra's prod CORS |

---

## 3. `03-architecture-review.md` — S-1…S-10 structural changes

**Plainly: one of ten was delivered. Four were not attempted at all.** Several rows below would
read "closed" if you judged them by their symptoms — they are not, and that distinction is the
whole point of this section.

| ID | Change | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **S-1** | Default-deny authorisation + a `GrantedLevel` type only `LiveAuthorizationService` can construct | "Not attempted" (05, 08, state.json) | **OPEN** *(deliberate — `state.json`: "GetManagementLevel is sync, LiveAuthorizationService is async … ~1 day of restructuring"; `08` repeats it)* | `ClaimsExtensions.GetManagementLevel` is still `public` at `src/Middleware/GatewayAuthMiddleware.cs:87`; `ServiceAccountController.cs:36` and `ProjectController.cs:35` still read the token snapshot directly. R-22's *symptom* is patched by the class-level filter; the type that would make it structural does not exist |
| **S-2** | Claims-assembly component + the §4 token contract | Partly (04) | **PARTIAL** | **Done:** project-qualified tenant roles and reserved management names (`Roles.cs:29-46`), SDK read-side helpers. **Not done:** no claims-assembly component (issuance is still open-coded in `AuthController.GetConsent`); `aud` is **not** mandatory (`HydraService.cs:326,350` — `Aud` is `List<string>?`, defaulted to `[]`); there is no `ver` claim anywhere; no SDK exposes an expected-audience/project option — which is exactly finding **P-06** |
| **S-3** | Audit as a cross-cutting concern (action filter/outbox + WORM export) | Not attempted | **OPEN** *(oversight — no step claimed it; T-N2 was closed by hand-adding call sites instead)* | `src/Filters/` contains exactly one file, `RequireManagementLevelAttribute.cs`; the only `IAsyncActionFilter` in `src/` is that attribute. Audit remains ~98 hand-written `RecordAsync` call sites — the "someone forgot" failure mode is unchanged. No WORM/append-only/hash-chain/export (`12 §7.3.3` ❌) |
| **S-4** | Trust anchors out of the mutable row + clamps | Fixed (04) | **CLOSED** | The one structural change actually delivered. `InstanceConfiguration.cs:84,114-125` + `AppConfig.cs` clamps + `TrustAnchorRegressionTests` |
| **S-5** | Tenant scope as a schema property (query filters → per-component PG roles + RLS → typed `OrgId`) | Not attempted | **OPEN** *(oversight — `09 §8` lists only phase 2 as "not attempted here"; phase 1 was never owned by any step)* | Zero `HasQueryFilter` / `IgnoreQueryFilters` in `src/Data/RediensIamDbContext.cs`. Tenant scoping is still ~200 hand-written conjuncts. Postgres: one role (`pg_roles` query above), no RLS. `OrgId` is still a bare `Guid` |
| **S-6** | Zero-trust network baseline | Partly (09) | **PARTIAL** | **Live and verified:** release-scoped default-deny (`rediensiam-default-deny-ingress`), 5 lockdown policies, seccomp, admin ClusterIP by default, CGNAT (`100.64.0.0/10`) egress block. **Not done:** namespace-wide default-deny (blocked by `NAMESPACE=default` sharing the namespace — `09 §0`), Postgres TLS (R-15), Dragonfly TLS. Note: the yandee pods that blocked the namespace move are no longer in `default` on the live cluster, so the blocker `09` cited may have gone away |
| **S-7** | Supply-chain integrity (authenticated TLS registry, digest pinning, `npm ci --ignore-scripts`, cosign + admission) | Partly (09) | **PARTIAL** | **Done:** digest pinning (live image is `@sha256:…`, `IfNotPresent`), registry bound to loopback. **Not done:** registry auth, registry TLS, cosign, admission policy, and `--ignore-scripts` — `grep -rn ignore-scripts deploy/ Dockerfile* .npmrc` returns nothing |
| **S-8** | Single authority per authorisation question (drop the `db.OrgRoles` fallback; add a reconciler) | Not attempted | **OPEN** *(oversight — `08 §4b` fixed two dual-write instances and states "class still open"; the structural change was never scoped)* | The fallback is still there: `src/Services/LiveAuthorizationService.cs:89` `\|\| await db.OrgRoles.AnyAsync(...)` and `src/Services/KetoService.cs:95`. No reconciler, no outbox |
| **S-9** | Split the admin surface into its own deployment/identity, or stop calling it a boundary | Partly (11b) | **PARTIAL** | The ambiguity was resolved at the ingress, not structurally: `templates/ingress.yaml:110-133` + `values.yaml:84-89` deny `/admin`, `/org`, `/project`, `/service-accounts` on the public host (live-verified 403). **The app still maps every route on both listeners** — 11b §5 says so explicitly. No separate deployment, no separate identity |
| **S-10** | Key rotation for the HKDF root, the Argon2 pepper and the Hydra system secret | Not attempted | **OPEN** *(deliberate — `10-secrets-management.md` costs it at ~2 days in `src/`; `state.json` confirms)* | Only backup codes are versioned: `src/Services/PasswordService.cs:49-58` `sha256:{keyId}:{hex}`. No key id on TOTP ciphertexts, no pepper rotation path, no Hydra system-secret rotation. This is why C-3 is unrecoverable |

---

## 4. `11-pentest-results.md` — P-01…P-08

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **P-01** | `project_admin` stamps a foreign org id on an SA grant → cross-tenant token disclosure | Closed (11b §1) | **CLOSED (write path) · PARTIAL (data)** | `src/Controllers/ServiceAccountController.cs:347` — `if (Level != ManagementLevel.SuperAdmin && body.OrgId != CallerOrgId) return 403 org_mismatch`, outside the tier branches. Proof tests pass unmodified: `PENTEST_FAILING_P01a_…`, `P01b_…`, plus `PENTEST_HELD_OrgAdmin_CannotScopeAnSaGrantToAForeignOrg`. **Residual:** rows written before the fix are not cleaned. 11b §1 supplies the SQL; step 13 turned it into detection D-01 (`deploy/monitoring/audit-detections.sh`) — **but it has to actually be run, and I have no evidence it was** |
| **P-02** | Every MFA mutation on a passwordless account takes no proof; every social user is passwordless | Closed (11b §2) | **CLOSED** | `src/Controllers/AccountController.cs:71` — `if (ReauthMethods(user).Length == 0 && !await HasAnyFactorAsync(user)) return null;`. Proof test `PENTEST_FAILING_P02_PasskeyOnlyAccount_CannotHaveItsOnlyFactorDeletedByABearerToken` passes. **Residual (11b §2, explicit):** no WebAuthn/SMS re-auth ceremony — federated users must go through password reset to rotate a passkey or phone |
| **P-03** | Theme dictionary reaches `setProperty` unvalidated; C-6 not discharged | Closed server-side (11b §3) | **CLOSED — ⚠ no test** | `src/Services/LoginThemeValidator.cs:67-72` walks every non-`custom_css`/`logo_url` key; `:77-79` `IsUnsafeThemeValue` (>120 chars or any of ``;{}()<>"'`\``). Client-side `safeCssValue` shared across `Login.tsx`, `SetPassword.tsx`, `Preview.tsx`. **No test asserts `theme_value_invalid_character`** — `grep` across `tests/` returns zero hits. 11b §3 admits this ("step 11 filed it by inspection"). **Residuals:** `login_theme.providers[]` nested values still unvalidated (`Login.tsx:229` renders `<img src={p.logo_url}>`); stored hostile themes were not migrated |
| **P-04** | Management API + admin SPA served on the public port, which the public ingress catch-alls | Fixed in the ingress (11b §5) | **CLOSED — live-verified, ⚠ no test** | `templates/ingress.yaml:110-133` second Ingress on the public host + `rediensiam-deny` middleware with `ipAllowList: [255.255.255.255/32]`; `values.yaml:84-89` `adminOnlyPaths`. Live probes through the ingress: `/admin/` **403**, `/org` **403**, `/service-accounts` **403**, `/api/introspect` **401** (deliberately not denied). No test — it is a chart control, and 11b §5 argues (correctly) that an app-level gate would be untestable under `WebApplicationFactory` |
| **P-05** | `/api/authorize` does not scope `object` to the caller's org | Explicitly out of scope | **OPEN** *(deliberate: `04 §Not-fixed 3`, `11 §7`, `11b §8` all decline it)* | `src/Controllers/IntrospectionController.cs:120` — `keto.CheckAsync(body.Namespace, body.Object, body.Relation, subject)` with `body.Object` unmodified. Only the `System` namespace is guarded (`:110-118`) |
| **P-06** | No SDK offers an audience/tenant binding | Acknowledged open (S-2) | **OPEN** *(deliberate: `11 §7`, `11b §8`)* | No expected-project or audience field in `RediensIamOptions` (`sdk/dotnet/RediensIAM.Client/RediensIamClient.cs:53` — `BaseUrl` only) or the Rust `Config` (`lib.rs:103-120`). `IntrospectionResult.ProjectId` is returned (`RediensIamClient.cs:22`) but nothing makes comparing it mandatory |
| **P-07** | The running cluster is on pre-hardening manifests and pre-step-6 CSP | Open at time of writing | **CLOSED** | A deploy landed 8 min before this audit. Every one of step 13's 12 failing `verify-deployment.sh` checks is now live — full table at the top of this document, each verified by an independent `kubectl`/`curl`/`docker` command |
| **P-08** | Org suspension revokes only tenant sessions; the org's own admins keep management sessions | Partially closed (11b §4) | **PARTIAL — ⚠ no test** | `src/Controllers/SystemAdminController.cs:126-146` now revokes both `{orgId}:{userId}` and bare `{userId}` for members **and** for `OrgRoles` holders. **Not closed (11b §4 states it):** `Organisation.Active` is still not consulted by `RequireManagementLevelAttribute` or `LiveAuthorizationService`, so a **system-list** org_admin can log back in through `AdminLogin` and keep managing a suspended org. No dedicated test — `SuspendingAnOrganisation_RevokesEverySessionInIt` asserts containment, not the admin population |

---

## 5. `12-compliance-report.md` — the ASVS/CIS gaps it named

These were named by step 12, which is the **second-to-last** step and produced no code. Step 13 was
monitoring. **So nothing in this section had a remediation step after it except the backup**, which
step 13's author added. That is the structural reason most of this block is open.

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **T-07a** | Password floor is 8, ASVS L2 wants 12 | ❌ open (12 §V2) | **OPEN** *(oversight — no step after 12)* | `src/Services/PasswordPolicy.cs:29` `public const int AbsoluteMinimumLength = 8;` and `:34` `Math.Max(project?.MinPasswordLength ?? 0, AbsoluteMinimumLength)` |
| **T-07b** | `Project.CheckBreachedPasswords` defaults to false | ⚠ (12 §2.1.7) | **OPEN** *(oversight)* | `src/Data/Entities/Project.cs:34` — `public bool CheckBreachedPasswords { get; set; }`, no initialiser → false for every project |
| **T-07c** | `Project.RequireMfa` defaults to false | ⚠ (12) | **OPEN** *(oversight)* | `src/Data/Entities/Project.cs:17` — `public bool RequireMfa { get; set; }`, no initialiser. Note: the **console** MFA policy *was* fixed and defaults on — `src/Config/AppConfig.cs:72` `RequireAdminMfa … default true`, consumed at `AuthController.cs:1114`. Tenant projects are the open half |
| **T-07d** | SMS is `StubSmsService` — the factor does not deliver | ⚠ (12 §2.2.2) | **OPEN** *(partly mitigated)* | `src/Program.cs:135` `AddScoped<ISmsService, StubSmsService>()`; `src/Services/NotificationService.cs:268-277` — `IsConfigured => false`, logs and returns. Mitigation shipped: the flag is surfaced so the server does not offer an undeliverable factor (test `FlowStateRegressionTests.SmsService_ReportsWhetherItCanActuallyDeliver`). No real provider exists |
| **T-04** | One Postgres role `iam` with SUPERUSER/CREATEROLE/CREATEDB, shared by app+Hydra+Keto; `local all all trust` | ❌ (12 §1.5, CIS PG 4.x/5.x) | **OPEN** *(deliberate deferral: `09 §8` "Separate Postgres roles per component — 0.5d, not attempted here")* | Live, read-only: `SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolvaliduntil FROM pg_roles WHERE rolcanlogin` → `iam\|t\|t\|t\|` (null expiry). `pg_hba.conf` → `local all all trust`, `host all all 127.0.0.1/32 trust`. This is C-4's precondition and it is untouched |
| **T-03** | No backup exists anywhere | ❌ (12 §0, HIPAA §164.308(a)(7)) | **CLOSED (mechanism) · UNVERIFIED (restore)** | `deploy/rediensiam/templates/backup.yaml` — PVC + CronJob `pg_dump`. Live: `kubectl get cronjob` → `rediensiam-backup 0 3 * * *`, age 8m30s, `LAST SCHEDULE <none>` (it has not run yet). **No restore has ever been tested**, and the template itself notes the dump lives on the same node — a disk failure still loses everything |
| **T-26** | SAML XML processing (XXE, signature wrapping) unassessed | ❓ blind spot (12 §13) | **UNVERIFIED — still unassessed** | No step in `.security-hardening/` covers it and none was added. What I can say from a read, offered as a lead not a conclusion: `src/Services/SamlService.cs:29` sets `CertificateValidationMode = X509CertificateValidationMode.None`, and nothing in `SamlService.cs` or `SamlController.cs` configures `XmlResolver`, `DtdProcessing` or `XmlReaderSettings` — ITfoxtec defaults govern. **Do not read this row as "probably fine."** |
| **T-06** | 8 high npm advisories per SPA | ❌ (12 §1.3) | **OPEN** *(oversight — no step owned it)* | `npm audit` executed by me on 2026-07-31 in both SPAs: `9 vulnerabilities (1 low, 8 high)`, `npm audit fix` available. Highs: `react-router`, `react-router-dom`, `vite`, `undici`, `postcss`, `js-yaml`, `picomatch`, `brace-expansion` |

---

## 6. `docs/2026-07-28-findings-securite-deploiement.md` — findings A–E

This is the document the task flagged. **Finding D sat open from 2026-07-28 to today** and only got
fixed after it crash-looped a live deploy. I checked whether its siblings fell through the same
crack. Two did.

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **A** | Authorisation reads token claims, not live Keto/DB | CLOSED (scan, as R-22) | **CLOSED** | See R-22. `RequireManagementLevelAttribute.cs:37-45` |
| **B** | No RFC 7662 introspection surface for an external resource server | CLOSED (scan) | **CLOSED** | `src/Controllers/IntrospectionController.cs` — `POST /api/introspect` (`:53`, `[FromForm]`, RFC 7662 shape) and `POST /api/authorize` (`:97`), both service-account gated (`:56`, `:100`), both re-checking live state (`:73-75`). Documented in `docs/INTEGRATION.md`. Tests `Tests/Api/IntrospectionTests.cs` + the `Introspect_*` regression tests |
| **C** | Ingress does not support a base path | OPEN (as R-27) | **OPEN** *(half-deliberate — the "or document it" alternative was taken; the chart change never was)* | See R-27. `docs/INTEGRATION.md:271` documents the dedicated-host requirement. **No step ever owned the chart half** — steps 9 and 11b both touched `ingress.yaml` and neither addressed it |
| **D** | Dev deployment crashes on empty `App__TrustedProxies` | Just fixed | **CLOSED — uncommitted** | `deploy/rediensiam/values.yaml:35`. `git status` → `M deploy/rediensiam/values.yaml`, the only dirty file in the tree. **Commit it or the next clone reintroduces the crash.** The misleading "Empty falls back to RFC1918 + loopback" comment is gone |
| **E** | Admin console cannot complete OIDC login (CSP), fonts blocked, `frame-ancestors` in meta | Fixed (06) | **CLOSED** | All three constats addressed — see R-26. `frame-ancestors` no longer appears in either `index.html` |

### Which A–E findings fell through the same crack as D

**C is the answer.** D and C are both `deploy/`-layer findings from the same document. Step 4's
scope named R-09/R-23/R-24/R-28/R-22/R-14/T-N4 and "deployment-layer findings deferred to steps
9/10". Step 9's scope was `deploy/` — and it fixed R-02, R-05, R-16, R-19, R-32 — but it worked
from the R-numbered infra list and **never touched R-25 or R-27**. So both dev-deploy findings
from the A–E document were orphaned by exactly the same mechanism; D was only caught because it
crashed a deploy, and C has no such forcing function, which is why it is still open today.

---

## 7. `yandee_web/docs/2026-07-28-rediensiam-api-mismatches.md` — M1–M7

| ID | Description | Claimed | Verified | Evidence |
|---|---|---|---|---|
| **M1** | snake_case payloads (revision-1 error) | Retracted | **n/a** | Correctly retracted; `src/Program.cs:115-120` `SnakeCaseLower` confirms the runbook was right |
| **M2** | `client_id` not settable on `POST /admin/hydra/clients` | Fixed (option B) | **CLOSED** | `src/Controllers/SystemAdminController.cs:957-963` — optional `ClientId` with charset allowlist, reserved-prefix rejection and a 409 conflict check. Tests `SystemAdminBranchCoverageTests.CreateHydraClient_ExplicitClientId_IsAccepted` (`:398`), `_MalformedClientId_Returns400` (`:419`), `_ClientIdAlreadyTaken_Returns409` (`:435`) |
| **M3** | `token_endpoint_auth_method` / `response_types` are outside the record and silently ignored | "minor, doc fix" | **CLOSED as documented** | Code unchanged by design — `SystemAdminController.cs:944-950` still forces `token_endpoint_auth_method` and omits `response_types`. The mismatch is now documented: `docs/INTEGRATION.md:107` ("forced") and `:109` ("not part of the request record and is ignored"). That was the agreed resolution |
| **M4** | `POST /service-accounts` requires `user_list_id` (snake_case) | doc fix | **CLOSED as documented** | `docs/INTEGRATION.md:177` and `:245` state `user_list_id` is required and cite `ServiceAccountController.cs:345`; `:282` lists the `{"name":…}`-only 400 as a common mistake |
| **M5** | `/api/introspect` is `[FromForm]`, no role required | doc fix | **CLOSED as documented** | `docs/INTEGRATION.md:204` states `[FromForm]` and cites `IntrospectionController.cs:23,40`; `:284` lists "sending JSON to `/api/introspect`" as a common mistake |
| **M6** | `VITE_OIDC_CLIENT_ID` depends on M2 | Fixed with M2 | **CLOSED** | Follows from M2; the test at `SystemAdminBranchCoverageTests.cs:398` posts exactly `client_id: "yandee-web"` |
| **M7** | `token_type_hint` accepted on the wire but never bound or read | "trancher : retirer ou lier" — **never assigned to a step** | **OPEN (code) · documented** *(oversight — no step owned it; the doc note is a workaround, not the fix either option specified)* | `src/Controllers/IntrospectionController.cs:173` `public record IntrospectionRequest(string Token, string? TokenTypeHint = null);` — `TokenTypeHint` has **zero readers**; a `find`-based grep over all of `src/`, `sdk/` and `tests/` returns only this declaration. Both SDKs still send it: `sdk/dotnet/RediensIAM.Client/RediensIamClient.cs:103`, `sdk/rust/rediensiam-client/src/lib.rs:199`. `docs/INTEGRATION.md:216-217` now *warns* that the server does not read it — honest, but neither of M7's two proposed fixes (remove the field, or bind it with `[FromForm(Name=…)]` and pass it to `ResolveAsync`) was done. Functional impact remains nil |

---

## 8. Counts by verified status

87 distinct findings enumerated across the seven source documents.

| Status | Count | IDs |
|---|---|---|
| **CLOSED** | **47** | R-01, R-04, R-06, R-07, R-09, R-10, R-11, R-13, R-14, R-17, R-18, R-19, R-20, R-22, R-23, R-24, R-25\*, R-26, R-28, R-29, R-32 · I-01, I-02, I-03, I-04, I-06, I-11 · T-N1, T-N2, T-N3, T-N4, T-N5 · C-1, C-2, C-5, C-6, C-7, C-9 · S-4 · P-02, P-04, P-07 · A, B, E · M2, M3, M4, M5, M6 *(50 table rows; 47 distinct after collapsing R-14≡T-N5, A≡R-22, E≡R-26)* |
| **CLOSED-PENDING-DEPLOY** | **1** | R-02 (prod half; dev cleartext is by design) |
| **PARTIAL** | **15** | R-05, R-08, R-16 · I-07/T-N6, I-08 · C-3, C-4, C-8 · S-2, S-6, S-7, S-9 · P-01 (stale data), P-08 · T-03 (mechanism only) |
| **OPEN** | **23** | R-03, R-15, R-21, R-27/C, R-30, R-31 · I-05, I-09, I-10 · S-1, S-3, S-5, S-8, S-10 · P-05, P-06 · T-04, T-06, T-07a, T-07b, T-07c, T-07d · M7 |
| **UNVERIFIED** | **1** | T-26 (SAML XML processing) |

\* R-25 is closed in the working tree only — see §11.

Deliberate vs oversight among the 23 OPEN items: **10 deliberate** (R-03, R-15, I-05, I-09, I-10,
S-1, S-10, P-05, P-06, T-04) · **13 oversight** (R-21, R-27/C, R-30, R-31, S-3, S-5, S-8, T-06,
T-07a, T-07b, T-07c, T-07d, M7).

### ⚠ Closed-but-untested — the P-02 failure mode

Findings recorded as fixed for which **no test exists**:

| Finding | What is untested |
|---|---|
| **P-03** | Nothing asserts `theme_value_invalid_character`. The whole non-`custom_css` theme-key walk (`LoginThemeValidator.cs:67-72`) is unexercised. A refactor that restores the early `return` after `custom_css` re-arms C-6 and the suite stays green |
| **P-04** | Chart-level control; defensible (11b §5 argues an app-level gate would be untestable), but the only proof is a live `curl`. A `helm template` assertion would cost ~10 lines |
| **P-08** | `SuspendingAnOrganisation_RevokesEverySessionInIt` asserts containment only. Nothing covers the bare-subject revocation or the `OrgRoles` (system-list admin) population the fix was written for |
| **R-06/R-07/R-08** | Shell-level controls in `deploy.sh`; no `bats`/`shellcheck` gate. `verify-deployment.sh` covers the cluster, not the script |
| **T-03** | Backup CronJob exists; no restore test, and `LAST SCHEDULE` is still `<none>` |

---

## 9. Everything not fully closed, ranked by severity

(OPEN plus the PARTIAL residuals — a partial that leaves the exploitable half live is not a
lesser problem than an open finding, so they are ranked together.)

| # | Finding | Severity | Why it matters | Deliberate? |
|---|---|---|---|---|
| 1 | **T-04** — one Postgres `iam` SUPERUSER shared by app+Hydra+Keto, `local all all trust` | **Critical (chain C-4)** | Verified live. Anyone with `kubectl exec` on the Postgres pod is superuser without a password, and one DB write still reaches everything the app trusts that is *not* a trust anchor — including the audit table, which has no WORM (S-3) | Deferred, `09 §8`, 0.5 d |
| 2 | **S-10** — no key rotation for the HKDF root, the Argon2 pepper, the Hydra system secret | **Critical for recovery** | C-3 remains unrecoverable. If the registry or a secret is ever compromised there is no path back short of re-enrolling every TOTP factor in every tenant | Deliberate, ~2 d |
| 3 | **T-03 restore** — backup exists, never restored | **High** | An untested backup is a hypothesis. `LAST SCHEDULE <none>`; the dump lands on the same node as the data | New, one runbook step |
| 4 | **T-06 / R-21 / R-03** — 8 high npm advisories per SPA | **High** | `npm audit fix` is available and nobody ran it. Includes `react-router` (downgraded on reachability grounds that were never re-tested after step 6 rewrote both SPAs) | Oversight |
| 5 | **R-30** — no SDK requires HTTPS or validates discovered endpoints | **High in chain C-8** | The threat model explicitly disputed the 4.2 rating for this reason. All three SDKs will follow `http://` and trust whatever discovery returns | Oversight — never assigned |
| 6 | **T-07a-d** — password floor 8, breach check off, tenant `RequireMfa` off, SMS is a stub | **High (aggregate)** | Four one-line defaults. `12 §V2` calls them "the failures are all defaults" | Oversight — step 12 had no successor |
| 7 | **S-3** — audit is 98 hand-written call sites, no WORM | **High** | T-N2 was closed by adding call sites, which is the defect S-3 exists to prevent. Combined with T-04 the audit log is mutable by the app's own superuser | Oversight |
| 8 | **P-08 residual** — a system-list org_admin survives suspension | **Medium (4.9)** | Suspension forces a re-login, not a lock-out. 11b §4 names the exact fix and its risk | Deliberate, needs its own test |
| 9 | **R-31** — browser SDK attaches the bearer to any caller-supplied URL | **Medium** | One user-influenced URL through `iam.fetch()` ships the access token off-origin | Oversight |
| 10 | **S-1 / S-5 / S-8** — no `GrantedLevel` type, no schema-level tenant scope, dual authority on authz | **Medium, structural** | P-01 was precisely the class S-1 and S-5 forestall. Every one of these was patched at the symptom | S-1 deliberate; S-5, S-8 oversight |
| 11 | **P-05** — `/api/authorize` object unscoped | **Medium (4.3)** | Cross-tenant relation enumeration, one bit per query | Deliberate ×3 reports |
| 12 | **P-06 / S-2 residual** — no audience binding, `aud` not mandatory, no `ver` | **Medium (5.0)** | A deployment-scoped gateway credential makes every tenant's token `active:true` at every RS | Deliberate |
| 13 | **R-15** — Postgres/Dragonfly cleartext | **Medium (5.2)** | Implemented, gated off | Deliberate, `09 §8` |
| 14 | **R-16 / R-05 residuals** — registry unauthenticated; prod admin cert self-signed | **Medium** | Loopback-bound and digest-pinned, so the reachable attack is narrow; the click-through warning on the most privileged UI is not | Deliberate, `09 §8` |
| 15 | **T-26** — SAML XML unassessed | **Unknown** | Genuinely unknown. `CertificateValidationMode.None` at `SamlService.cs:29` is a lead worth an hour | Never owned |
| 16 | **R-27 / finding C** — no ingress base path | **Low (functional)** | Documented-around, not fixed | See §6 |
| 17 | **I-10** — SAML state consumed before signature check | **Low** | Unauthenticated in-flight login DoS; requires an unguessable request id | Deliberate |
| 18 | **I-08 residual / I-05 / I-09** — raw `ex.Message` from health probes; breach check fails open; Rust SDK ignores the OS trust store | **Low** | — | Deliberate |
| 19 | **M7** — `token_type_hint` accepted, never read | **Low (contract lie)** | Documented as a lie rather than fixed | Never owned |

---

## 10. Findings no step ever owned

These are the ones most likely still live, because nothing forced anyone to look at them. Every ID
here was checked in code today and none is closed.

| Finding | Why it fell through |
|---|---|
| **R-25 (finding D)** — *now fixed, but the mechanism is the point* | `deploy/`-layer finding from the A–E document. Step 4 deferred deployment-layer items to steps 9/10; step 9 worked from the R-numbered infra list (R-02/05/16/19/32) and R-25 was not on it. Open from 2026-07-28 until it crash-looped a live deploy on 2026-07-31 — three days and one production incident to fix a one-line values change |
| **R-27 (finding C)** — ingress base path | **Same crack, still open.** Steps 9 and 11b both edited `ingress.yaml`; neither addressed it. `docs/INTEGRATION.md:271` documents the workaround, which the original finding offered as an acceptable alternative — so this is half-closed by accident rather than by decision |
| **M7** — `token_type_hint` dead parameter | The M-document's own action list says "Trancher M7" and it was never assigned. Step 5 touched `IntrospectionController` heavily for T-N6 and left the dead field. The INTEGRATION.md note documents the lie without resolving it |
| **R-30** — SDK discovery/HTTPS validation | Steps 4 and 6 touched all three SDKs (R-28 cache key, R-29 logout, `hasProjectRole`) and never added a scheme check. The threat model called out the under-rating in C-8 §8; no step picked it up |
| **R-31** — browser SDK `fetch()` bearer leak | Step 6 edited the exact file for R-29 and left this. Adjacent lines, unowned finding |
| **R-21 / R-03 / T-06** — npm advisories | Step 6 was the frontend step and ran `npm run build`, not `npm audit`. Step 12 measured them; step 12 produces no code and nothing came after it except monitoring |
| **T-07a-d** — the four authentication defaults | Same shape: named by step 12, which had no successor with a code mandate. Step 8 was the auth step and ran *before* 12 |
| **S-3 / S-5 / S-8** — structural changes | `03-architecture-review.md` §8 ranked ten structural changes; only S-4 was ever assigned to a step. S-1 and S-10 were explicitly declined with costs. **S-3, S-5 and S-8 were never declined and never scheduled — they were simply not carried into any step's scope** |
| **T-26** — SAML XML | `12 §13` says it plainly: "no step covered it". Still true |

---

## 11. Two things to do before anything else

1. **Commit `deploy/rediensiam/values.yaml`.** Finding D's fix is the only dirty file in the tree. A
   clean clone of `security/hardening-2026-07-30` still crash-loops on `--dev`.
2. **Run detection D-01.** P-01's write path is closed; the rows it may already have written are
   not. `deploy/monitoring/audit-detections.sh` contains the query and there is no evidence it has
   been executed against the live database.
