# Step 5 — Backend hardening

**Branch:** `security/audit-2026-07-28` · **Working tree, not committed**
**Scope:** the backend findings step 4 left open — R-10, R-20, T-N6, R-18, R-17 — plus the
informational items that were real and cheap, plus the step-5 themes where they were not already
satisfied.
**Suite:** `dotnet test tests/RediensIAM.IntegrationTests` — **1183 passed, 0 failed, 0 skipped,
3 m 08 s** (baseline on this tree before this step: 1151).

Nothing from step 4 was redone. **S-1 was not attempted** — reasoning and the full cost estimate
are in §S-1. One wire-contract change, in §Breaking changes; it is smaller than `ext.roles` but it
is real and integrators must read it.

> **Build note.** A stale `.sonarqube/` directory at the repo root makes `dotnet build`/`dotnet
> test` from the repo root fail with `CS0006: /tmp/.sonarqube/resources/0/SonarAnalyzer.CSharp.dll
> could not be found`. Every command in this document was run with
> `-p:SonarQubeTargetsImported=true` to skip the leftover scanner import. This is environmental,
> not a code problem; deleting `.sonarqube/` has the same effect.

---

## Summary

| Finding | CVSS | Status | Landing point |
|---|---|---|---|
| **R-10** per-org SMTP host/port unvalidated | 5.5 | Fixed | `SmtpEndpointValidator` + both write paths + the connect + the oracle |
| **R-20** SSRF re-validation is TOCTOU on DNS | 3.7 | Fixed | `WebhookUrlValidator.CreateSsrfSafeHandler` on all three clients |
| **T-N6** introspection/authorize have no tenant scoping | 6.5 | Fixed | `IntrospectionController.IsInCallerScopeAsync` |
| **R-18** login rate limiting reads only the per-IP counter | 3.7 | Fixed | both login paths in `AuthController` |
| **R-17** `AllowedHosts: "*"` | 3.7 | Fixed (app side) | `HostFilteringOptions` post-configure in `Program.cs` |
| **I-01** unused Handlebars.Net on a tenant field | — | Fixed | `src/RediensIAM.csproj` |
| **I-02** `/admin` GET without `Authorization` bypasses auth | — | Fixed | `Program.cs` admin `UseWhen` |
| **I-03** dead `/preview` framing exemption | — | Fixed | `Program.cs` `AddSecurityHeaders` |
| **I-04** `*.bak` not gitignored | — | Fixed | `.gitignore` |
| **I-08** health endpoint returns the SMTP username | — | Partly fixed | `SystemHealthController` |
| **I-11** `SsoUrl` not scheme-validated → open redirect | — | Fixed | `SamlService.ApplyExplicitConfig` |
| Theme: logging | — | One real leak fixed | `SocialLoginService.ExchangeCodeAsync` |
| **I-05, I-06, I-07, I-09, I-10** | — | Not fixed | see §Deliberately left |

---

## R-10 — Per-org SMTP host and port persisted with no validation (5.5)

**What changed.**

New `src/Services/SmtpEndpointValidator.cs`. It refuses:

| Rule | Error code | Why |
|---|---|---|
| empty / > 255 chars | `smtp_host_required`, `smtp_host_too_long` | — |
| port not in `{25, 465, 587, 1025, 2525}` | `smtp_port_not_allowed` | without a port allowlist the endpoint is a port scanner — every port a connect attempt can distinguish is one bit of the pod's reachable network |
| `start_tls == false` on any port but 465 | `smtp_tls_required` | the org's SMTP credentials travel over this socket |
| host resolves into a private or reserved range | `smtp_host_not_allowed` | same resolver, same range set as webhooks/OIDC/SAML |

Wired into **both** write paths — `OrgController.UpsertSmtp` (`PUT /org/smtp`) and
`SystemAdminController.UpsertOrgSmtp` (`PUT /admin/organizations/{id}/smtp`). The second is a path
step 1 did not name; it writes the same row and was equally unvalidated.

`WebhookUrlValidator` gained `IsPrivateOrReservedHostAsync(string host)` — the URL entry point now
delegates to it. An SMTP endpoint is a bare host:port pair, not a URL, which is precisely why it
had never passed through the validator; giving the validator a host-shaped entry point is what
makes "everything outbound goes through one check" true rather than aspirational.

That refactor also closed a bypass in the *existing* URL path: `Uri.Host` keeps the brackets on an
IPv6 literal (`http://[::1]/hook` → `[::1]`), `Dns.GetHostAddressesAsync("[::1]")` throws, and the
`catch` treated that as "public, go ahead". Brackets are now stripped and IP literals are checked
directly without a DNS round-trip.

`NotificationService.SmtpSendAsync` now picks `SecureSocketOptions.SslOnConnect` on port 465.
There was no implicit-TLS path at all, so a config saved as "465, start_tls off" — which is the
correct way to describe SMTPS — connected in the clear and then authenticated.

`POST /org/smtp/test` no longer returns `detail = ex.Message`. The message distinguishes
"connection refused" from "no route" from an SMTP banner, which is the whole probe oracle. It is
still logged, where only an operator sees it.

**Why there.** The two controllers are the trust boundary and must refuse, not silently correct —
same reasoning as T-N4 in step 4. The TLS selection is in `SmtpSendAsync` because that is the only
code that opens an SMTP socket, so the global operator-configured relay benefits too. The host
check is deliberately **not** applied to the operator's global `Smtp__Host`: an in-cluster relay
(`mailhog.default.svc`, `127.0.0.1`) is a legitimate operator choice and blocking it would break
every dev deployment. A per-org host is by definition not that.

**Regression tests.** `Tests/Regression/BackendHardeningRegressionTests.cs` —
`OrgSmtp_HostInsideTheMeshOrLoopback_IsRefusedAndNotPersisted` (theory over loopback, RFC1918,
169.254.169.254, **100.64.0.3** — the Tailscale admin ingress this deployment documents — a
`.svc` name, and `localhost`), asserting 400 *and* that no row was written;
`OrgSmtp_NonSubmissionPort_IsRefused` (22, 6379, 4445, 0);
`OrgSmtp_CleartextSubmission_IsRefused`; `OrgSmtp_LegitimateEndpoint_IsStillAccepted` (587+STARTTLS
and 465 implicit); `OrgSmtpTest_Failure_DoesNotReturnTheExceptionText`.

**Residual risk.**
- The NetworkPolicy still omits `100.64.0.0/10` (`deploy/rediensiam/templates/network-policies.yaml`).
  Application validation now blocks the range, but the pod can still reach it — **step 9 must
  still add it.** Defence in depth here is not optional: the app-layer check is a DNS-time
  decision and the mesh is a routing fact.
- A host that resolves publicly at write time and privately later is still reachable, because the
  SMTP client is MailKit and does not go through the `ConnectCallback` that closes this for HTTP
  (R-20). Closing it would mean resolving the host in `SmtpSendAsync` and connecting to a vetted
  `IPEndPoint`, which changes TLS certificate name validation — not a surgical change.
- Rows written before this step are not swept. An operator should audit
  `org_smtp_configs` for private hosts and non-submission ports.

---

## R-20 — SSRF re-validation was TOCTOU on DNS (3.7)

**What changed.** `WebhookUrlValidator.CreateSsrfSafeHandler()` — a `SocketsHttpHandler` whose
`ConnectCallback` resolves the name **once**, picks the first address that is not private or
reserved, and dials that address. There is no window between the check and the connection because
they are the same operation.

Applied to all three clients the finding names:

| Client | Consumer |
|---|---|
| `"webhook"` | `WebhookService` delivery |
| `SocialLoginService.NoRedirectClient` | OIDC discovery, token exchange, userinfo |
| unnamed | SAML IdP metadata (via ITfoxtec) and the HIBP range API |

The unnamed client keeps `AllowAutoRedirect = true` — the callback vets every redirect hop, so
disabling redirects is no longer the security control, and a SAML IdP that 302s its metadata URL
should still work. The other two keep it false because following a redirect was never wanted on a
webhook delivery or an OAuth2 token exchange.

**Why there.** One handler factory, three registrations, no call-site changes. The alternative the
scan proposed — pinning the validated IP through from `IsPrivateOrReservedAsync` to
`client.SendAsync` — needs a per-request channel from the validator to the handler, which is
several call sites and a new abstraction for the same result.

**Regression tests.** `SsrfSafeHandler_RefusesToDialAPrivateAddress` (127.0.0.1, `localhost`,
169.254.169.254) — pure unit, no containers; `WebhookUrlValidator_BracketedIpv6Literal_IsRefused`.

**Test-harness note.** `TestFixture` already replaced `IWebhookSsrfValidator` with a passthrough so
tests can deliver webhooks to a local WireMock. It now also replaces the `"webhook"` client's
primary handler, for the same reason and with the same scope. Without this the connect callback
refuses the loopback target and seven `WebhookDeliveryTests` fail. The real handler is covered
directly by the unit test above, so nothing is untested.

**Residual risk.** `Dns.GetHostAddressesAsync` inside the callback can still be answered by a
poisoned resolver — this closes the *time-of-check* gap, not DNS trust. A host with both a public
and a private A record is dialled on the public one, which is correct but means the private
address is not itself an error.

---

## T-N6 — Introspection and authorize had no tenant scoping (6.5)

**Decision first, because the instruction asked for it: yes, this can break a legitimate
deployment, and the fix is shaped so that it breaks the smallest possible set.**

The rule is: **a service account with a non-empty `org_id` may only resolve tokens belonging to
that organisation.** A service account with an empty `org_id` — one hanging off the `__system__`
user list with no org-scoped role, i.e. a deployment-wide credential — stays unscoped exactly as
before.

That is the correct boundary, and it is also the answer to "does this break cross-tenant use": a
gateway that genuinely fronts several tenants must hold a **system** service account, not a tenant
one. If an integrator has been fronting multiple orgs with a tenant-scoped SA, that integration
breaks and the fix is to re-issue its PAT against a `__system__` service account. There is no
legitimate case where org A's credential should resolve org B's tokens; that is the finding.

**What changed** in `src/Controllers/IntrospectionController.cs`:

1. `CallerOrgScope` — the caller's org, or null for a deployment-level caller.
2. `IsInCallerScopeAsync(subject, auditAction)` — applied to `POST /api/introspect` and
   `POST /api/authorize` immediately after `ResolveAsync`. Out of scope answers
   `{"active": false}` / `{"allowed": false}`, **not** 403: the controller's stated contract is
   that a caller cannot distinguish malformed from revoked from expired, and "that token exists
   but is not yours" is the disclosure being closed.
3. `POST /api/authorize` additionally refuses the `System` Keto namespace outright for any
   org-scoped caller. That namespace holds one object and one interesting relation —
   `rediensiam#super_admin` — so a tenant credential asking about it is enumerating the
   deployment's administrators, never authorising its own request.
4. `AuditLogService` is now a dependency, and **refusals are recorded** (`api.introspect.out_of_scope`,
   `api.authorize.out_of_scope`). The threat model's Repudiation row was that this controller had
   no audit dependency at all. Only refusals are recorded — a row per introspection is a row per
   API request behind every gateway, which would drown the table and the retention job.

**Why there.** `ResolveAsync` is the single point where a foreign token becomes claims, and both
actions call it. The scope check sits directly after it, so there is one place to read and one
place to change.

**Regression tests.** `Introspect_TokenFromAnotherOrganisation_IsNotResolved` (asserts `active:
false`, `sub: null`, **and** that the audit row exists);
`Introspect_TokenFromTheCallersOwnOrganisation_StillResolves`;
`Introspect_FromASystemServiceAccount_IsStillUnscoped` (builds an SA on an `OrgId == null` user
list and resolves another org's token — the multi-tenant-gateway case, pinned so nobody "fixes"
it later); `Authorize_TenantCallerProbingTheSystemNamespace_IsRefused` (Keto stub set to
`AllowAll`, so the refusal has to happen *before* the probe or the test fails).

Two existing tests in `Tests/Api/IntrospectionTests.cs` used a subject token minted by
`SeedData.SuperAdminToken`, which registers `org_id = null` and therefore belongs to no
organisation. They now register the subject against the calling gateway's org. Behaviour asserted
is unchanged; only the fixture's tenant assignment moved.

**Residual risk.**
- The unscoped case is keyed on "empty `org_id`", not on holding `super_admin`. A service account
  parked on the `__system__` list with no roles at all is therefore unscoped. That list is by
  construction a deployment-level object (`Immovable`, `OrgId IS NULL`, created by bootstrap), and
  `ServiceAccountController` already treats membership of it as the most privileged position in
  the deployment — but it is a fail-open direction and worth knowing.
- `/api/authorize` is still an oracle *within* the caller's own organisation: the subject is now
  guaranteed to be one of the caller's users, but namespace/object/relation are still
  caller-supplied. Narrowing further (object must belong to the caller's org, which needs a DB
  lookup per namespace) buys little: the probe requires already knowing the object's GUID and
  answers only about the caller's own users.
- Introspection still returns `roles` as bare strings (S-2). Unchanged from step 4.

---

## R-18 — Login rate limiting read only the per-IP counter (3.7)

**Verified first, as instructed.** Step 1's self-correction is right and step 4 did not touch this:

- `AuthController.Login:198` calls `rateLimiter.IsBlockedAsync(Ip)` with no `userId`.
- `AdminLogin` is reached *through* `Login` (`:209-210`), after that IP check. It was never
  unprotected.
- `LoginRateLimiter.IsBlockedAsync` consults `rate:{prefix}:user:{id}` only when a `userId` is
  supplied, and `RecordFailureAsync(Ip, user.Id)` writes it on every failure — so the per-user
  counter was written by the password path, the MFA paths, and `AdminLogin`, and read by the MFA
  paths only.

Every other authentication surface was checked and is genuinely covered:

| Surface | Control |
|---|---|
| `/auth/mfa/backup-codes/verify`, `/mfa/phone/send`, `/mfa/phone/verify`, `/mfa/totp/verify`, `/mfa/setup/totp/confirm` | `IsBlockedAsync(Ip, userGuid)` — per-IP **and** per-user already |
| `/auth/register`, `/auth/password-reset/request` | `IsBlockedAsync(Ip, null, prefix)` |
| `/auth/register/verify`, `/auth/password-reset/verify` | `OtpCacheService` — 5 attempts per session, then the code is destroyed |
| `/auth/invite/complete`, `/auth/password-reset/confirm`, `/auth/verify-email` | 256-bit random single-use token; a counter adds nothing |
| `/auth/mfa/webauthn/verify` | cryptographic assertion against a credential scoped to the pending user |
| `/account/*` re-auth and password change | `IsBlockedAsync(Ip, userId, prefix)` |

**What changed.** `IsBlockedAsync(Ip, user.Id)` added at the two points where a login path first
knows which account it is authenticating — `CheckUserCredentialsAsync` (tenant login) and
`AdminLogin` (admin console). Nothing else was touched.

**Why there and not in `LoginRateLimiter`.** The limiter cannot know the user; the caller resolves
it. These two methods are where every password verification in the codebase happens, and the check
is placed before the verify so a spent budget does not cost an Argon2 hash.

**Regression test.** `Login_WhenThePerUserBudgetIsSpentFromOtherAddresses_IsRefused` — charges five
failures against the account from `203.0.113.0-4`, then presents **correct** credentials from the
test client's address and asserts 429. It fails on the pre-fix build because the test client's own
IP counter is zero.

**Residual risk.** The per-user counter shares `MaxLoginAttempts`/`LockoutMinutes` with the DB
lockout (`User.FailedLoginCount` / `LockedUntil`), so in the common single-source case the two
controls fire together and this fix changes nothing observable. Its value is the distributed case
and the MFA-failure carry-over, both of which the DB lockout does not see.

---

## R-17 — `AllowedHosts: "*"` (3.7)

The wildcard itself lives in `deploy/rediensiam/templates/deployment.yaml:39-40`, which step 9
owns and this step did not touch. The app-side fix makes the wildcard harmless wherever it comes
from.

**What changed.** `Program.cs` post-configures `HostFilteringOptions`: **if and only if** the
effective `AllowedHosts` still contains `*`, it is replaced by the hosts this deployment already
declares as its own — the host components of `App__PublicUrl` and `App__AdminSpaOrigin`, plus
`App__Domain`. An operator who sets a real list still wins; this only replaces the wildcard.

**Why that is safe for Kubernetes.** The chart's startup, readiness and liveness probes all send
an explicit `Host` header equal to `urlParse(publicUrl).host`
(`deployment.yaml:117-119, 126-128, 135-137`), so they match the derived list. This was checked
before the change, not after.

**Regression tests.** `Request_WithAForeignHostHeader_IsRefused` (400) and
`Request_WithTheDeploymentsOwnHost_IsStillServed` (200) — the pair is what proves filtering is on
rather than everything being broken.

**Residual risk.** `src/appsettings.json` still ships `"AllowedHosts": "*"`; it is now the trigger
for the derivation rather than a bypass, and leaving it means a developer with no `App__Domain`
gets the same startup error they already get from the Fido2 configuration. Step 9 should still set
a real list in the chart — the derived one is a floor, not a policy.

---

## Informational items

### Fixed

- **I-01 — unused template engine on a tenant-controlled field.** `Handlebars.Net` removed from
  `src/RediensIAM.csproj`. `Project.LoginTemplate` is only ever compared to null
  (`AuthController.cs:156`, `:184`); nothing referenced the package. Latent SSTI surface with no
  offsetting benefit, deleted.
- **I-02 — `/admin` GET without `Authorization` bypassed `GatewayAuthMiddleware`.** The branch
  condition was "has an `Authorization` header, or is not a GET", which let every unauthenticated
  admin GET reach its controller and rely on that controller carrying `[RequireManagementLevel]`.
  `UseRouting()` runs before this `UseWhen`, so the endpoint is already resolved: the condition is
  now "resolved to a controller action, or is not a GET". A request that resolved to a static file
  or to the SPA fallback still passes; an API call does not. A new `/admin` controller that forgets
  the attribute now fails closed for unauthenticated GETs.
  Test: `AdminApiGet_WithoutAuthorization_IsRefusedBeforeTheController` asserts 401 **with an empty
  body** — the empty body is what distinguishes the middleware's refusal from the filter's, which
  also returns 401 but with `{"error":"unauthorized"}`.
- **I-03 — dead `/preview` framing exemption.** Removed. No `/preview` route exists anywhere in
  `src/`, and the same response carried `frame-ancestors 'none'` regardless, which browsers honour
  over `X-Frame-Options`. `SecurityHeadersTests.PreviewRoute_NoXFrameOptions` asserted the dead
  behaviour and was inverted to `PreviewRoute_IsFramingDeniedLikeEverythingElse`.
- **I-04 — `*.bak` not gitignored.** One line in `.gitignore`.
- **I-08 — health endpoint returned the SMTP username.** `SystemHealthController` now reports
  `auth = "configured" | "none"`. Whether auth is configured is the diagnostic; the account name is
  a credential half, and this response lands in browser history and audit metadata.
  **Partly fixed:** `ex.Message` is still returned verbatim from every component probe (`:218`,
  `:241`). Gutting it destroys the endpoint's only purpose, it is SuperAdmin-gated, and the
  disclosure is to an already-maximally-privileged caller. Left deliberately.
- **I-11 — `SsoUrl` not scheme-validated → unauthenticated open redirect.**
  `SamlService.ApplyExplicitConfig` now requires an absolute HTTPS URL, matching what the metadata
  branch already enforced. Guarding on the **read** path rather than the four write paths
  (`OrgController` create/update, `SystemAdminController` create/update) is deliberate: it also
  covers rows already in the database and any future writer.
  Test: `SamlConfig_NonHttpsSsoUrl_IsRefused` over `http://`, `javascript:` and `//evil`.

### Skipped, with the reason

- **I-05 — breach check fails open.** Deliberate and commented in `BreachCheckService.cs:31-35`.
  Failing closed on an HIBP outage would block every password change in the deployment. Correct
  availability trade-off; noise to "fix".
- **I-06 — `docs/ARCHITECTURE.md` is stale.** Real, but it is documentation and it is step 8's
  surface, not a backend control. Fixing three sentences here would also be stale again the moment
  step 6 lands. Left.
- **I-07 — promoted to T-N6 and fixed there.** No separate work.
- **I-09 — Rust SDK ignores the OS trust store.** SDK, not backend, and switching `reqwest` to
  `rustls-tls-native-roots` changes TLS behaviour for every consumer. Not this step's blast radius.
- **I-10 — SAML pending state consumed before signature validation.** Left; see §Deliberately left.

---

## Step 5 themes — verified as already satisfied, not touched

- **Input validation at trust boundaries.** `RedirectValidator`, `WebhookUrlValidator`,
  `LoginThemeValidator` (step 4), `Roles.ProjectRoleNameError` (step 4), `PasswordPolicyService`,
  `AppConfig`'s security-parameter clamps (step 4), `LoginChallengeProject.Resolve`. The one gap
  was SMTP; that is R-10.
- **Rate limiting and abuse protection.** Table in §R-18. The only surface reading the wrong
  counter was the login password path.
- **API endpoint authorization.** Every management controller carries a class-level
  `[RequireManagementLevel]` — `OrgController:19`, `ProjectController:14`, `WebhookController:18`
  and `:171`, `ManagedApiController:18`, `SystemAdminController:16`, `SystemHealthController:27`,
  and `ServiceAccountController` since step 4 — and the filter re-verifies against Keto rather than
  trusting `ext.roles`. The gap was the *middleware* in front of `/admin`, which is I-02.
- **Encryption in transit.** In-app: the SMTP fix above is the only backend-to-dependency
  connection the application chooses. Everything else is deployment-supplied and out of scope here
  — `deploy/rediensiam/values.secret.yaml` still has `sslmode=disable` on both the Hydra and Keto
  DSNs and a plaintext Dragonfly connection string, and Hydra/Keto are reached over `http://`
  in-cluster. **Carry to steps 9/10;** it is not fixable from `src/` without breaking every dev
  deployment.
- **Logging that cannot leak PII or secrets.** Swept every `Log*` call in `src/`. One real leak
  found and fixed: `SocialLoginService.ExchangeCodeAsync` logged the provider's **entire** token
  endpoint response body on failure. That request carries `client_secret` and the authorization
  code, and providers have been observed echoing request parameters into error bodies. It now logs
  the status and the RFC 6749 `error` code only. Everything else logs ids, not values —
  `PatService` logs the PAT's row id, never the token; `AuditLogService` stores IP and user-agent
  by design; the `TotpSecretEncryptionKey` warning names the env var, not the value.

---

## S-1 — not attempted

**Decision: no.** The instruction is "only if you judge it can land without destabilising 1151
tests" and "do not half-apply it", and I do not judge that. The honest reason is that S-1(b) is not
a type introduction, it is an async plumbing change through six controllers.

`GetManagementLevel()` is a synchronous extension method on `TokenClaims`; `GrantedLevel` can only
be produced by `LiveAuthorizationService`, which is `async`. So every current consumer —
`ServiceAccountController.Level` (a property, read inside `CanAccessAsync`, `IsCallerProjectListAsync`
and eleven action bodies), `OrgController`'s SuperAdmin branches, `ProjectController`'s scope
resolution, `IntrospectionController`'s management-role strip, and
`RequireManagementLevelAttribute` itself — becomes an `await` at a call site that is currently a
property read. Several of those sit inside LINQ expression trees passed to EF Core
(`ServiceAccountController.IsCallerProjectListAsync` has `Level == ManagementLevel.SuperAdmin`
*inside* a `Where`), which cannot contain an `await` at all and would need restructuring rather
than threading.

**What it would take, concretely:**

1. `readonly record struct GrantedLevel` with an `internal` constructor, in the same assembly as
   `LiveAuthorizationService`; make `ClaimsExtensions.GetManagementLevel` `internal`.
2. `LiveAuthorizationService.ResolveAsync(TokenClaims, Guid? orgScope, Guid? projectScope)` as the
   only producer — which is also where R-22 residual 3 gets fixed, because the project scope
   finally travels with the question.
3. Convert `ServiceAccountController`: hoist one `await ResolveAsync(...)` per action, materialise
   the `Where` clauses that currently reference `Level`, and rewrite `CanAccessAsync` /
   `IsCallerProjectListAsync` to take the resolved value. This is the largest single chunk.
4. Convert `OrgController`, `ProjectController`, `WebhookController`, `SystemAdminController`,
   `SystemHealthController`, `ManagedApiController` — mostly mechanical once `RequireManagementLevelAttribute`
   stashes the `GrantedLevel` in `HttpContext.Items` for the action to read.
5. `IntrospectionController`'s role strip needs an internal accessor or a dedicated
   `LiveAuthorizationService` method, since it deliberately reads the *claimed* level to decide
   what to remove.
6. S-1(a), the global default-deny filter with a `[PublicSurface]` opt-out for `AuthController`,
   `SamlController`, `/health`, `/admin/config` and the `/account` self-service routes. This half
   is small on its own — but shipping it alone is exactly the half-application the instruction
   forbids, and it would give a false sense that the class is closed while `GetManagementLevel()`
   is still public.

Estimate: a day of work plus a full test pass, with the `ServiceAccountController` conversion
carrying most of the risk (it is the controller that terminated the R-01 chain, and its `Where`
clauses encode tenant isolation). It is the right change and it should be its own commit with its
own review, not a rider on a hardening pass.

I-02 was fixed as an instance, at the middleware, not as a global filter — deliberately, so that
this step contains no fragment of S-1.

---

## Breaking changes

### 1. `/api/introspect` and `/api/authorize` are now tenant-scoped (T-N6)

A service account whose token carries a non-empty `org_id` can only resolve tokens belonging to
that organisation. Out-of-scope answers are `{"active": false}` and `{"allowed": false}` — the same
shape as an unknown token, so a caller cannot tell the two apart.

**Who this breaks:** any resource server whose service account is attached to one organisation but
which introspects tokens from several. It will start seeing `active: false` for tokens that used to
resolve, with no error to distinguish it. **The fix is to re-issue that gateway's PAT against a
service account on the `__system__` user list**, which has no `org_id` and stays unscoped.

An org-scoped caller also gets `allowed: false` for any `/api/authorize` query in the `System` Keto
namespace, regardless of what Keto would say.

Both refusals write an audit row (`api.introspect.out_of_scope`, `api.authorize.out_of_scope`), so
an operator can find affected integrations by querying `audit_logs` for those actions rather than
waiting for a support ticket.

This is a second wire-contract change in the same release as step 4's `ext.roles`. It is
deliberate: T-N6 is a cross-tenant confidentiality boundary with no enforcement, and an additive
version of it (a new opt-in "scoped" mode) leaves the vulnerable default in place, which closes
nothing.

### 2. `POST /org/smtp/test` no longer returns `detail`

The response is `400 {"error":"smtp_test_failed"}`. The `detail` field is gone. The admin console
displays it today; step 6 should drop the field from the error rendering. Minor, and the exception
text is still in the pod log.

### 3. `PUT /org/smtp` and `PUT /admin/organizations/{id}/smtp` can now return 400

New error codes: `smtp_host_required`, `smtp_host_too_long`, `smtp_port_not_allowed`,
`smtp_tls_required`, `smtp_host_not_allowed`. Existing rows are untouched and keep working — the
validation is on write only. A tenant that had saved a cleartext or non-submission-port
configuration will be refused the next time it saves that same configuration.

---

## Deliberately left

1. **S-1.** See §S-1 above for the full cost.
2. **I-10 — SAML pending state consumed before signature validation.** The fix is to move
   `httpRequest.Binding.Unbind(...)` ahead of `GetAndDeletePendingAsync`. That reorders a
   crypto-adjacent flow with ~30 tests behind it, and ITfoxtec's `Unbind` re-reads the response
   that `ReadSamlResponse` already parsed, so the interaction between the two calls has to be
   established before moving either. The scan itself rates it not practically exploitable — the
   AuthnRequest ID is unguessable, and the identity still comes from the verified assertion.
   **What it would take:** confirm whether `Unbind` alone is sufficient (dropping the separate
   `ReadSamlResponse`), accept that a non-success unsigned response then fails as a signature error
   rather than a status error, and re-run the SAML suite.
3. **The SMTP connect-time TOCTOU.** MailKit's `SmtpClient.ConnectAsync(host, port, …)` resolves
   internally. Pinning would mean connecting to a vetted `IPEndPoint` and overriding the TLS
   certificate name for validation. Non-surgical.
4. **`/api/authorize` object scoping.** See §T-N6 residual risk.
5. **Deployment-layer items** — the `100.64.0.0/10` NetworkPolicy gap (R-10's other half),
   `sslmode=disable` on the Hydra and Keto DSNs, the plaintext Dragonfly connection, R-06's
   placeholder secrets, R-19, R-32, R-05, R-02. No file under `deploy/` was modified.
6. **`frontend/`** — untouched. Step 6 owns it, and it now has three things to pick up: step 4's
   MFA re-auth prompt, the removed `detail` field on `/org/smtp/test`, and the new SMTP validation
   error codes.

---

## Files changed

**Backend**
`src/Program.cs`, `src/Controllers/AuthController.cs`, `src/Controllers/IntrospectionController.cs`,
`src/Controllers/OrgController.cs`, `src/Controllers/SystemAdminController.cs`,
`src/Controllers/SystemHealthController.cs`, `src/Controllers/WebhookController.cs`,
`src/Services/NotificationService.cs`, `src/Services/SamlService.cs`,
`src/Services/SocialLoginService.cs`, `src/Services/SmtpEndpointValidator.cs` *(new)*,
`src/RediensIAM.csproj`, `.gitignore`

**Tests**
`tests/…/Tests/Regression/BackendHardeningRegressionTests.cs` *(new, 32 cases)*,
`tests/…/Infrastructure/TestFixture.cs` (webhook client handler passthrough),
`tests/…/Tests/Api/IntrospectionTests.cs` (two subject tokens moved into the caller's org),
`tests/…/Tests/Security/SecurityHeadersTests.cs` (`/preview` assertion inverted)

---

## Test output

```
dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
    -p:SonarQubeTargetsImported=true --nologo

Passed!  - Failed:     0, Passed:  1183, Skipped:     0, Total:  1183, Duration: 3 m 08 s
         - RediensIAM.IntegrationTests.dll (net10.0)
```

Baseline before this step on the same tree: 1151 passed. The 32 new cases are the regression tests
listed per finding above; no existing test was removed and none was weakened.
