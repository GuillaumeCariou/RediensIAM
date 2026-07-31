# RediensIAM security posture

What protects what, how far each control actually reaches, and what is deliberately still open.

This is the document to read **before** trusting the system with anything. It is written to be
useful to someone deciding whether to deploy it, which means it is not a feature list. Every claim
below was checked against the code on the `security/hardening-2026-07-30` branch; where an audit
report and the code disagreed, the code won and the disagreement is named.

The finding-by-finding audit trail lives in `.security-hardening/`. The status ledger is
`.security-hardening/14-finding-ledger.md`; it was written mid-audit and several of the items it
lists as open have since been closed by steps 15–23. The current status is what is written here.

---

## The one-paragraph summary

Authorisation on RediensIAM's own management surface is re-verified against Ory Keto on every
request, with a `GrantedLevel` type the compiler will not let you fabricate and a default-deny gate
that refuses any management route reaching a controller with no authorisation filter. Tenant
isolation is enforced by ~200 hand-written query conjuncts; the row-level-security backstop under
them is **on in dev, off in prod**, and does not by itself make the login path tenant-safe. Secrets are versioned and rotatable. The audit trail is written by the persistence
layer rather than remembered by developers, and is hash-chained — with an **unkeyed** hash, and
with no scheduled verifier. Four things are known-open and named at the bottom.

---

## 1. Authorisation: why claims are not authority

### The problem this solves

A JWT's `ext.roles` is a snapshot taken when the token was minted. It cannot see a role revoked, an
account deactivated, an organisation suspended or a token revoked since. Every authorisation defect
in this system's history came from treating that snapshot as authority: a `project_admin` reading
its own claimed level and acting on it (R-22), a controller that simply had no filter (R-22
residual), a stale `org_admin` grant reaching system service accounts (R-01), a cross-tenant
service-account grant (P-01).

### The mechanism

Two C# types, and only one of them is authority:

| | `ManagementLevel` | `GrantedLevel` |
|---|---|---|
| What it is | a plain enum | a `readonly struct` with a **private constructor** |
| Where it comes from | anywhere, including a token | `GrantedLevel.ResolveAsync` only — which asks Keto |
| Can you make one? | yes | **no** |

`src/Services/GrantedLevel.cs:25-48`. The old reader that turned `ext.roles` into a level,
`ClaimsExtensions.GetManagementLevel`, was made private and has since been deleted outright; only
the comment explaining why survives (`src/Middleware/GatewayAuthMiddleware.cs:58-62`). **Reading a
claimed level as authority does not compile.** Controllers read `HttpContext.GetGrantedLevel()`,
which returns
`GrantedLevel?` and is `null` until a live check has run — null being the only honest answer when
nothing was verified.

Reading a *claim* as a *claim* is still possible and still needed: `GrantedLevel.ClaimedLevel` is
named for what it does, and introspection uses it because introspection is asking about somebody
else's token, not the caller's authority.

### Three gates, in order

Every request on a management prefix (`/admin`, `/org`, `/project`, `/service-accounts`, `/api`,
`/internal`) passes through `GatewayAuthMiddleware`:

1. **Token resolution** — PAT prefix → `PatService`, otherwise Hydra JWT validation. No token or an
   invalid one is `401`.
2. **Audience gate** (`:49-55`) — a valid token is not automatically a token *for this surface*.
   Unless the caller is a PAT or an `sa_`-prefixed service-account client, its `client_id` must be
   in `Security:ManagementClientIds`, or `403 token_audience_not_allowed`. Without this, a token
   minted for a tenant's own application reaches the management API with only `ext.roles` in the
   way.
3. **Default deny** (`:69-75`) — if the request resolves to an MVC action and that action carries
   no `[RequireManagementLevel]`, the answer is `403 no_authorisation_gate`. A new controller added
   on a management prefix fails closed. The exemption set is one greppable array
   (`SelfGatedControllers`, `:85-91`) with exactly one entry — `Introspection`, which gates on
   *being a service account* instead.

Then `RequireManagementLevelAttribute` runs: reject on the claim as a cheap pre-filter, resolve a
`GrantedLevel` against Keto, and record it for the controller to read.

### Keto is the single authority

`LiveAuthorizationService.CheckAsync` (`src/Services/LiveAuthorizationService.cs:71-93`) refuses a
suspended organisation and then asks Keto. The `|| await db.OrgRoles.AnyAsync(...)` fallback — which
answered "project_admin *somewhere*" to the question "project_admin *here*" — is gone. One
implementation: `KetoService.IsManagementLevelGrantedAsync`.

**Keto unreachable is a 403, not a pass** (`:58-64`).

### The honest limits

- **The cache window is 30 s.** A revoked role keeps working on this deployment's surface for up to
  30 seconds. Negative verdicts are cached for the same 30 s, so a Keto outage produces *sticky
  refusals* — the safe direction, but a real availability cost.
- **The bound does not reach a resource server that validates the JWT locally.** It sees the
  `ext.roles` snapshot and has no way to learn a role was revoked. That is why role changes and org
  suspensions also revoke the affected Hydra sessions, and why `POST /api/introspect` is the only
  way for a relying party to see live state.
- **The dual write is real and unreconciled.** Every grant writes a Keto tuple *and* an `org_roles`
  row (tuple-first, row-second, compensating tuple delete in the `catch`). `org_roles` is no longer
  *consulted* for an answer — it holds scope, display name and provenance — but a killed process
  between the two writes leaves them divergent, and **there is no reconciler and no outbox**.
- **`AuthController`'s consent path still reads `db.OrgRoles`** to resolve scopes into the minted
  token, after the role list itself came from Keto. "Keto is the only store consulted anywhere"
  would be an overstatement.

---

## 2. Tenant isolation, and its honest limit

**Read this section before assuming multi-tenant safety.**

### What exists

- **Query-level scoping.** Roughly 200 hand-written conjuncts across the controllers. This is the
  actual enforcement today.
- **Claim namespacing.** Tenant roles reach a token as `{project_id}/{name}`
  (`Roles.ProjectRoleClaim`, `src/Config/Roles.cs:45-46`). Two tenants both naming a role `admin`
  used to be byte-identical in every consumer's claims. Management role names (`super_admin`,
  `org_admin`, `project_admin`) are reserved case-insensitively and refused as tenant role names.
- **Audience binding on introspection.** `aud` is mandatory; a deployment-scoped gateway credential
  no longer gets `active: true` for every tenant in the deployment.
- **Object scoping on `/api/authorize`.** The object must belong to the answer's tenant;
  `Organisations`, `Projects` and `UserLists` are checked against the database and **any other
  namespace is refused**.
- **The tenant-scope interceptor.** `src/Data/TenantScopeInterceptor.cs` issues
  `SELECT set_config('rediensiam.org_id', …, false)` on every connection open. Two connection-string
  shapes that would silently break this — `No Reset On Close=true` and `Multiplexing=true` — are
  refused at startup (`src/Config/AppConfig.cs:18-52`) rather than tolerated.
- **Row-level security, live in dev.** `postgres.rls.enabled: true` in `values.dev.yaml`: 19 tables
  `ENABLE` + `FORCE` with one policy each, applied by a post-upgrade hook Job as the table owner and
  verified against two real tenants on the running cluster — cross-tenant read, insert, update and
  delete all refused at the database. Read the limit below before reporting this as tenant
  isolation.

### What does not exist

- **Row-level security in production.** `postgres.rls.enabled` stays `false` in `values.yaml` and
  `values.prod.yaml` does not override it: on an already-running database this is a migration, not
  an upgrade, and no production cluster has been through it. The enablement as performed in dev,
  including the rollback, is `.security-hardening/29-rls-prod-tls.md`.
- **There is no EF global query filter.** `grep HasQueryFilter src/` returns one comment and no
  code. Nothing catches a forgotten conjunct.

### The limit that matters even when RLS is on

The interceptor's fallback is the string `'system'`, not "deny"
(`TenantScopeInterceptor.cs:82-90`). A request with no org context runs **unscoped**, and
`LegitimatelyUnscopedPaths` (`:59-75`) lists nine such paths — including the whole of
`AuthController` and `SystemAdminController`.

That is not an oversight; it is unavoidable. **The login path resolves a user before a tenant is
known.** You cannot scope the query that finds the user by email to an organisation you have not
identified yet. The same is true of password reset, email verification, social callback, SAML ACS,
PAT introspection, the bootstrap path, schema creation, the audit retention sweep and the webhook
dispatcher.

So: **turning RLS on does not make the login path tenant-safe.** It puts a schema-level backstop
under the ~200 conjuncts on the *authenticated tenant* paths, which is a genuine and worthwhile
defence in depth, and it leaves the highest-traffic unauthenticated surface exactly as safe as its
hand-written queries. Anyone who reads "RLS" and hears "tenant isolation is now structural" has been
misled, and this document would rather say so than let a flag flip imply it.

That is measurable rather than theoretical. With statement logging on for one minute of ordinary dev
traffic — an OIDC login, TOTP, consent, a token exchange, SuperAdmin listings and one
PAT-authenticated introspection — the scopes the application actually set were:

```
  5 94177c59-8d98-4dd1-8a4b-1e6b6add59b8    ← org-bearing, RLS-protected
 15 system                                   ← unscoped by necessity
```

Enabling RLS is also a real outage risk: the policies deny everything to a connection that has not
set the variable, which for an identity provider is total. The runbook is in
[`DEPLOYMENT.md`](DEPLOYMENT.md#turning-rls-on).

---

## 3. The token contract

### What a token carries

Assembled in `AuthController`'s consent path and handed to Hydra, which nests it under `ext`:

| Claim | Tenant token | Admin console token |
|---|---|---|
| `ext.org_id`, `ext.project_id`, `ext.user_id` | yes | yes (`""` when absent) |
| `ext.roles` | tenant roles as `{project_id}/{name}` | **bare** management roles, resolved live from Keto |
| `aud` | **not set by RediensIAM** | not set |
| `ver` | no such claim | no such claim |

Two things worth stating plainly, because a report implies otherwise:

- **Nothing in `src/` sets an OAuth2 audience on issued tokens.** `AcceptConsentAsync` sends
  `grant_scope`, `session` and `remember` and no `grant_access_token_audience`. Audience binding at
  introspection therefore works via `project_id`/`org_id` only; the third documented path
  (`aud` ∈ the token's OAuth2 audiences) is unreachable for tokens this deployment mints, unless the
  relying party requested an audience at `/oauth2/auth`.
- **`ver` is a response field of `/api/introspect` and `/api/authorize`, not a token claim.** It
  signals server capability, so an SDK can detect a server that silently discarded the `aud` it
  sent. It says nothing about the token.

The claims-assembly component that S-2 asked for was never built: issuance is still open-coded in
`AuthController.GetConsent`.

### The four wire-contract breaks in this release

All four are breaking. Integrators must read
[`INTEGRATION.md`](INTEGRATION.md) before upgrading.

| # | Break | What fails if you ignore it |
|---|---|---|
| 1 | **`ext.roles` is project-qualified.** Tenant roles are `{project_id}/{name}`; management names are reserved. | `roles.contains("admin")` matches nothing — fails **closed**, which is the intended direction. In the .NET SDK the qualified string is what lands in `ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` stops matching every tenant at once. |
| 2 | **`aud` is mandatory on `POST /api/introspect`.** | `400 {"error":"audience_required","ver":1}` on every call. No grace period, no opt-out. |
| 3 | **`aud` is mandatory on `POST /api/authorize`**, same terms. | Same. |
| 4 | **`object` on `/api/authorize` is tenant-scoped**, and unknown Keto namespaces are refused. | `{"allowed": false}` — deliberately the same shape as a genuine deny, so the endpoint cannot be used to probe which objects exist. Every refusal writes `api.authorize.object_out_of_scope`. |

Backend SDKs (.NET, Rust) now take a **required** `Audience`/`audience` option with no default — a
client constructed without one throws at construction rather than 400-ing on its first request — and
refuse any answer that arrives without `ver`. The browser SDK is unaffected; it never calls these
endpoints, because introspection needs a service-account credential and anything shipped to a
browser is readable by anyone with devtools.

### Three precision notes the reports do not make

These do not change the security properties, but a document that says "always" should mean it:

- **`ver` is not on *every* response.** It is on the 200s and on the `audience_required` 400. It is
  **not** on the `403 service_account_required`, nor on ASP.NET Core's own
  `ValidationProblemDetails` 400 for a missing `token`/`namespace`/`object`/`relation` (there is no
  `InvalidModelStateResponseFactory` override in `src/`), nor on the middleware `401`. An SDK
  enforcing "`ver >= 1` or fail closed" is unaffected — all of these are non-200s.
- **Object scoping still has a narrower fail-open edge.** `IsObjectInScopeAsync` computes
  `scope = CallerOrgScope ?? subject.OrgId`. When both are absent — a deployment-level service
  account asking about a token whose `org_id` is also empty — there is no ownership to compare, so
  the only check left is that the namespace is one this deployment writes objects into: the call
  succeeds for `Organisations`, `Projects` and `UserLists` without an object-ownership check.
  Report 19 §7.3's unconditional "unknown namespaces are refused" is therefore conditional in
  code, and unknown namespaces are refused even in that path. The `System` namespace is **no
  longer** reachable this way: `Authorize` refuses it to every caller before object scoping runs,
  because `System:rediensiam#super_admin` enumerates the deployment's administrators and never
  authorises the caller's own request. A resource server that needs that answer reads the `roles`
  field of `/api/introspect`, which re-verifies against Keto.
- **Audience matching is asymmetric.** `project_id`/`org_id` are compared case-insensitively;
  membership in the token's OAuth2 audience list is compared case-sensitively.

---

## 4. Secrets and rotation

### One root, derived per purpose

`Security:EncryptionKey` is a 32-byte root that is never used directly. Every purpose gets its own
HKDF-SHA256 subkey (`src/Config/AppConfig.cs:255-260`): TOTP secrets, webhook signing secrets,
per-org SMTP passwords, login themes, the DataProtection key ring, and the device-fingerprint key.

### Rotation is maintenance, not a disaster procedure

Before this release, a ciphertext carried no key identifier. Rotating the root destroyed every TOTP
secret in every tenant at once, with no migration path — which is another way of saying the key was
never going to be rotated, and is why chain C-3 (registry compromise → key exfiltration) had
disclosure but no recovery.

Ciphertexts now carry their key id in a `k<id>:` prefix ahead of the AES-GCM envelope
(`src/Services/TotpEncryptionService.cs:81-89`). Key id 1 writes **no prefix**, so a deployment that
never rotated is byte-identical to the old build. `Security:EncryptionKeys` is the ring, active key
first; a malformed ring is a startup failure, not a first-decrypt failure. Decrypting under an
unconfigured id throws a `CryptographicException` naming the id.

The sweep is an operator-triggered endpoint — `GET /admin/key-rotation`,
`POST /admin/key-rotation/reencrypt` — covering `User.TotpSecret`, `Webhook.SecretEnc`,
`OrgSmtpConfig.PasswordEnc` and `Project.LoginTheme`. **`totalPending == 0` is the only signal that
a retired key may be dropped**, and dropping it early is unrecoverable.

**The Argon2 pepper cannot be swept**, because the plaintext password only exists at verify time.
Hashes carry a `$k=<id>` suffix and re-derive on the account's next successful login. Finishing a
pepper rotation is a policy decision about dormant accounts, not a job that completes. Verification
is fail-closed on a dropped pepper.

**Hydra system secret rotation is a runbook only** (`.security-hardening/16-key-rotation.md §7.4`).
There is no rotation code. Prepend, never replace, and keep the old entry for at least the
refresh-token TTL.

Read `.security-hardening/16-key-rotation.md §8` before rotating anything cryptographic — it is the
table of which rollbacks are safe, and two of them are not.

### Credential generation

`deploy/deploy.sh` generates every credential per machine into a mode-600, gitignored secrets file:
the four Postgres role passwords, the cache password, the encryption root, the Argon2 pepper, the
Hydra system secret and the bootstrap admin password. The old template full of `CHANGE_ME_…`
placeholders is gone, and `deploy.sh` hard-fails rather than deploy a known default to production.

Secrets live in a mode-600 file on the deploy host. That is the honest ceiling for a one-operator,
one-machine deployment; SOPS + age is the answer the day a second operator or a second machine
appears.

### The DataProtection key ring

Persisted to Dragonfly, so session cookies survive a pod restart, and **encrypted at rest** under a
purpose-derived HKDF subkey. The load-bearing half is the *read* side: an unwrapped `<key>` element
causes a startup exception rather than being adopted
(`src/Config/KeyRingProtection.cs:122-140`). An attacker with cache *write* access could otherwise
append a plaintext key that DataProtection would use to mint session cookies.

---

## 5. The audit trail

### It is no longer remembered

Audit used to be ~98 hand-written `RecordAsync` calls and nothing else — precisely the "someone
forgot" failure mode that let T-N2 (no audit on any MFA mutation) exist.
`RediensIamDbContext.SaveChangesAsync` (`src/Data/RediensIamDbContext.cs:48-70`) now:

1. **rejects tampering** — throws if a tracked `AuditLog` is `Modified` or `Deleted`;
2. **writes rows without a call site** — a `User` whose `PasswordHash`, `TotpSecret`, `TotpEnabled`,
   `WebAuthnEnabled`, `Phone`, `PhoneVerified`, `Email`, `EmailVerified` or `Active` changed
   produces `entity.users.credential_changed` naming the changed columns; state changes on
   `BackupCode`, `WebAuthnCredential`, `UserSocialAccount`, `SamlIdpConfig` and `Instance` produce
   `entity.{table}.{inserted|updated|deleted}`;
3. **links the new rows into a hash chain.**

The 99 hand-written calls remain deliberately: they carry *intent* ("this was a role revocation"),
which a column diff cannot. The automatic rows are prefixed `entity.` so a query can separate them.

### The chain, and what it is worth

Per organisation (retention purges are per organisation, and a global chain would break on every
purge), with a `pg_advisory_xact_lock` per org in fixed key order so concurrent writers cannot
interleave. `AuditChain.Compute` (`src/Data/AuditChain.cs:45-63`) hashes the previous hash plus
every field of the row plus canonicalised metadata. `AuditChain.FirstBreak` returns the id of the
first row whose link fails; `AuditLogService.VerifyChainAsync` is its entry point.

**Three caveats, none of which this document will bury:**

1. **The hash is plain unkeyed SHA-256, not an HMAC.** It detects accidental corruption and a
   careless edit. It does **not** stop anyone who can write to the table from recomputing the chain
   forward from the row they altered. Tamper-*evidence* against a careless adversary, not against a
   capable one.
2. **`VerifyChainAsync` has no production caller.** No endpoint, no hosted service, no schedule. It
   exists and it is tested; nothing runs it for you. Until something does, the chain detects nothing
   on its own.
3. **There is no database-level append-only enforcement.** The guard is application-layer, and the
   application role must retain `DELETE` on `audit_log` because the retention sweep uses
   `ExecuteDeleteAsync`, which bypasses the change tracker and therefore the guard.

Retention has a hard floor of 90 days that a tenant cannot set below
(`src/Config/AppConfig.cs:113-119`), re-clamped on the delete path so a stale value cannot slip
through.

### Detection

`deploy/monitoring/audit-detections.sh` is the detection layer — 13 rules (D-01…D-13, nine
`page`-severity) queried directly against the audit table as the **read-only** `iam_backup` role.
It distinguishes "no hits" from "the rules failed to run", because a check that silently reports
all-clear because it never ran is worse than no check. `deploy/monitoring/selftest.sh` proves six
of the non-trivial predicates actually fire, against synthetic rows in a read-only transaction.

Neither runs on a schedule by default. See [`TESTING.md`](TESTING.md#detection-rules).

---

## 6. Network, transport and the cluster

| Control | State |
|---|---|
| Public ingress TLS | on in prod (`letsencrypt`); **off in dev** by design — `iam.localhost` cannot be certified |
| Admin ingress TLS | on in prod, but `selfsigned` — a known defect, see below |
| Postgres server TLS + `hostssl` | **on in both shipped environments** |
| Postgres roles | four least-privilege login roles, `scram-sha-256`, no shared superuser in any DSN |
| Dragonfly TLS | **on in dev, off in prod** — see below |
| NetworkPolicy | namespace-wide default-deny plus five lockdown policies; CGNAT (`100.64.0.0/10`) egress blocked |
| Pod hardening | non-root, drop `ALL` caps, no priv-esc, read-only root, `seccompProfile: RuntimeDefault`, `automountServiceAccountToken: false` |
| Image | pinned by `@sha256:` digest, `pullPolicy: IfNotPresent` |
| Backup | nightly `pg_dumpall` CronJob; restore proven byte-identical once |
| Management surface on the public host | denied at the ingress for `/admin`, `/org`, `/project`, `/service-accounts` |

Two of these need their qualifier stated, because a summary table elsewhere reads as unconditional:

- **Dragonfly TLS is on in dev and off in prod.** `values.prod.yaml` sets no `dragonfly` block, so
  it inherits `enabled: false` from `values.yaml:328`. The application side is complete and *pinned*
  — `src/Config/CacheTls.cs` builds an `X509Chain` with `CustomRootTrust` over only the mounted CA,
  keeps name mismatch fatal, and requires the serverAuth EKU; it is not a `return true`. The chart
  mount exists. Prod simply has the flag off. It is a **hard cutover** — `--tls` makes Dragonfly stop
  answering cleartext — so `cacheUrl` must gain `ssl=true` in the same `helm upgrade`, which the
  chart enforces as a render failure in both directions.
- **NetworkPolicy is decorative unless your CNI enforces it.** Verify that before trusting any row
  in this table; the two-minute check is in
  [`DEPLOYMENT.md`](DEPLOYMENT.md#before-the-first-install-on-a-new-cluster--two-minutes).

**Production has never been deployed from this branch.** Every prod path is template-verified and
preflight-verified; none has been run against a real production cluster.

---

## 7. Application-layer controls, briefly

| Control | Where |
|---|---|
| Argon2id + rotatable pepper ring | `PasswordService.cs` |
| Password floor of 12 characters, enforced on every write path | `PasswordPolicy.cs:32` |
| Breached-password check, **on by default** for new projects | `Project.CheckBreachedPasswords = true` |
| Rate limiting per IP **and** per user account | `RateLimitService.IsBlockedAsync(ip, userId)` |
| Open-redirect allowlist | `RedirectValidator.cs` + `AuthController.SafeRedirect` |
| SSRF re-validation on every webhook delivery, with `ConnectCallback` address pinning | `WebhookUrlValidator`, `Program.cs:85,109,111` |
| HMAC-signed webhook deliveries | `WebhookService.ComputeSignature` |
| Per-org SMTP host validated against the mesh/loopback blocklist; submission port and STARTTLS required | `SmtpEndpointValidator.cs:39` |
| Tenant `custom_css` and every other theme value validated server-side | `LoginThemeValidator.cs` |
| Re-authentication on every MFA factor mutation | `AccountController.RequireReauthAsync` |
| Session revocation on role change, org suspension, deactivation, password change | `LiveAuthorizationService.InvalidateAsync`, `SystemAdminController.SuspendOrg`, `UserHelpers.ApplyUpdate` |
| Trusted-proxy fail-closed in production (the app **refuses to start** on an empty value) | `Program.cs:ConfigureForwardedHeaders` |
| `AllowedHosts` wildcard replaced with the derived host list | `Program.cs:28-42` |
| CSP with `script-src`, `base-uri`, `form-action`, `object-src`, `frame-ancestors` on both branches | `Program.cs:448-476` |
| Security parameters clamped so a hostile config row cannot weaken them | `AppConfig.cs:25,47,48,57,64,118-119` |
| Trust anchors (`Hydra:*Url`, `Keto:*Url`, `App:TrustedProxies`) excluded from the DB config layer | `InstanceConfiguration.cs:84,114-125` |

---

## 8. What is deliberately still open

Nothing below is a surprise. Each is either a decision with a stated cost or a residual whose
exploitable half is narrow. Anything not listed here and not described above as working should be
assumed unaddressed.

### Open, with severity

| Item | Severity | State | Why it is open |
|---|---|---|---|
| **npm advisories in both SPAs** | High | **7 high per SPA**, down from 8 high + 1 low. `react-router` and `brace-expansion` among them; remaining fixes need `npm audit fix --force`, i.e. breaking major bumps | The forced upgrades were judged riskier than the advisories as reached by these SPAs. That judgement has not been re-tested since the SPAs were rewritten |
| **Dragonfly TLS in prod is untested live** | Medium | `values.prod.yaml` now sets `dragonfly.local.tls.enabled: true`; `helm lint`/`helm template` pass, no prod cluster exists to run it against | A hard cutover proven only on the dev cluster. It also costs every session at the moment it happens, and a prod Dragonfly whose key ring survives the upgrade needs `DEL rediensiam:dataprotection:keys` first — see `DEPLOYMENT.md` |
| **Registry unauthenticated, no TLS, no signature verification** | Medium | bound to loopback and digest-pinned, so the reachable attack is narrow | ~2 h; **required** if k3s is not on the deploy host — bind and auth move together |
| **Prod admin certificate is self-signed** | Medium | `values.prod.yaml:29` `clusterIssuer: selfsigned` | It trains operators to click through a warning on the most privileged UI. Ways out: an internal CA, or ACME DNS-01 |
| **No reconciler for the Keto/`org_roles` dual write** | Medium, structural | compensating delete in the `catch` covers a thrown exception, not a killed process | S-8's second half was never scoped |
| **RLS off in prod** | Medium, structural | live and verified in dev since `29-rls-prod-tls.md`; chart default and `values.prod.yaml` still `false` | On an existing prod database it is a migration, not an upgrade, and there is no prod cluster to rehearse on. And see §2 — even on, it does not make the login path tenant-safe |
| **Audit chain hash is unkeyed; no scheduled verifier; no DB-level append-only** | Medium | see §5 | An HMAC needs a key with its own rotation story; the verifier needs an owner and an alert destination |
| **`/api/authorize` object check skips ownership when both scopes are absent** | Low–Medium | `IsObjectInScopeAsync` checks only that the namespace is known when the caller is deployment-level *and* the subject token has no `org_id`. The `System` namespace is refused to every caller before this point, so what remains is an unowned check against `Organisations` / `Projects` / `UserLists` | Not named by any report; found while writing this document. The `System` half was closed in `75e9576`; the rest needs a decision about what a deployment-level caller with an org-less token may legitimately ask |
| **`GET /admin/system/health` returns raw `ex.Message`** | Low | the SMTP username is redacted; two branches still return exception text (`SystemHealthController` `:222`, `:245`) | Treat this route as equivalent to a stack trace |
| **Breach check fails open** | Low | `BreachCheckService.cs:35` — `return 0` on an outage | Deliberate availability trade |
| **SAML pending state consumed before signature validation** | Low | `ReadSamlResponse` → `GetAndDeletePendingAsync` → `Unbind` | Unauthenticated in-flight login DoS, requiring an unguessable request id |
| **Rust SDK ignores the OS trust store** | Low | `rustls-tls`, compiled-in webpki roots | A private-CA deployment will not validate |
| **No ingress base-path support** | Low, functional | serve RediensIAM on a dedicated host | Documented around rather than fixed |
| **Off-node backup copy** | Operational | the nightly dump lands on a PVC on the same node and disk as the database | Covers a bad migration or a dropped table; does not cover losing the node |
| **k3s secrets not encrypted at rest** | Operational | needs root on the server node | 15 minutes, and nobody has done it |

### Deliberate product decisions, not defects

- **`Project.RequireMfa` defaults to `false`.** Forcing a second factor on every new tenant is a UX
  call belonging to whoever owns the tenant. RediensIAM's **own** admin surface is separate and
  defaults MFA **on** (`Security:RequireAdminMfa`) — that one guards `super_admin`, so it is ours to
  set. Turning `require_mfa` off on a project with enrolled users requires an explicit confirmation
  in the same request body.
- **SMS is a stub and does not deliver.** `StubSmsService` is the only implementation
  (`src/Program.cs:142`). It reports `IsConfigured => false`, so the server does not offer an
  undeliverable factor rather than pretending to. Do not count SMS as an available second factor.
- **`token_type_hint` is not part of the introspection request record.** RFC 7662 §2.1 makes it an
  optional hint a server may ignore, and RediensIAM identifies the token shape from its prefix in
  constant time. Sending it is harmless; expecting it to change the answer is not.
- **Dev is cleartext on the ingress.** `iam.localhost` cannot be certified by any CA. This is the
  one place R-02 is not fixed and it is gated to dev so prod cannot inherit it.

### Genuinely unknown

- **SAML XML processing beyond what was assessed.** `.security-hardening/15a-backend-residuals.md
  §7` assessed T-26 and reports one real defect found and a clean bill on the two things it was
  feared for. `src/Services/SamlService.cs:29` sets
  `CertificateValidationMode = X509CertificateValidationMode.None`, and ITfoxtec's defaults govern
  `XmlResolver` / `DtdProcessing`. Do not read that assessment as an exhaustive XXE and
  signature-wrapping review.

---

## 9. Reporting a vulnerability

There is no published disclosure process in this repository. Until there is, the audit trail in
`.security-hardening/` and the finding ledger are the record of what has been looked at and by
whom. `.security-hardening/14-finding-ledger.md` §10, "findings no step ever owned", is the most
useful page in that directory for anyone deciding where to look next.
