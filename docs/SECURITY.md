# RediensIAM security posture

What protects what, how far each control actually reaches, and what is deliberately still open.

This is the document to read **before** trusting the system with anything. It is written to be
useful to someone deciding whether to deploy it, which means it is not a feature list. Every claim
below was checked against the code on the `security/hardening-2026-07-30` branch; where an audit
report and the code disagreed, the code won and the disagreement is named.

The finding-by-finding audit trail lives in `SECURITY-AUDIT-LOG.md`. There is no status ledger any
more: `14-finding-ledger.md` was moved out of the repository because too many of the items it
listed as open had since been closed, and a ledger that is out of date is worse than none.
**The current status is what is written here.** `SECURITY-AUDIT-LOG.md` records which
reports have been retired and why.

---

## The one-paragraph summary

Authorisation on RediensIAM's own management surface is re-verified against Ory Keto on every
request, with a `GrantedLevel` type the compiler will not let you fabricate and a default-deny gate
that refuses any management route reaching a controller with no authorisation filter. Tenant
isolation is enforced by ~200 hand-written query conjuncts; the row-level-security backstop under
them is **on in dev, off in prod**. The tenant login path now runs under its own organisation's
scope rather than unscoped — the admin console, the token-keyed endpoints and the SAML ACS still do
not, and §2 says why. Secrets are versioned and rotatable. The audit trail is written by the persistence
layer rather than remembered by developers, and is chained under a keyed HMAC that a daily verifier
actually runs. Four things are known-open and named at the bottom.

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
  between the two writes leaves them divergent. `GrantReconciler` now detects that divergence
  daily and repairs it on request, in the only two directions that are safe: a tuple with no
  backing row is **revoked** (the row is written second, so it is a grant that never completed),
  and a row with no tuple is **deleted rather than promoted** — minting authority from a database
  row is exactly the coupling S-8 removed. Mass divergence refuses to auto-repair at 100 items,
  because at that point both directions are destructive and the failure is store-level. **There is
  still no outbox**, so the window itself remains.
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
  refused at startup (`AppConfig.RequirePerCheckoutSessionState`) rather than tolerated. Its scope
  comes from the caller's validated claims, or — for the pre-authentication login flow, which has no
  token yet — from `PinToOrganisationAsync`. See "What is scoped, and what still is not" below.
- **Row-level security, live in dev.** `postgres.rls.enabled: true` in `values.dev.yaml`: 19 tables
  `ENABLE` + `FORCE` with one policy each, applied by a post-upgrade hook Job as the table owner and
  verified against two real tenants on the running cluster — cross-tenant read, insert, update and
  delete all refused at the database. Read the limit below before reporting this as tenant
  isolation.

### What does not exist

- **Row-level security in production.** `postgres.rls.enabled` stays `false` in
  `values.yaml:314-315` and `values.prod.yaml` does not override it: on an already-running database
  this is a migration, not an upgrade, and no production cluster has been through it. The
  enablement as performed in dev, including the rollback, is
  `SECURITY-AUDIT-LOG.md` step 29. It has since been turned on once on a from-scratch
  install of the *prod profile* in a scratch namespace on the dev cluster — 19 tables, verified by
  `verify-deployment.sh --prod` V-25 — which is what surfaced the fact that it could not have been
  enabled at all before then: `init.sh` granted `iam_backup` its required `BYPASSRLS` only when RLS
  was already on at initdb, while `setup.sh --prod` forces RLS off on a first install and does not
  ask. **No production database this repository could have created ever had that grant.** The grant
  is now unconditional (`templates/postgres.yaml:97-106`); a database initdb'd before 2026-08-01
  still needs `ALTER ROLE iam_backup BYPASSRLS` applied by hand, and `files/rls.sql` fails the
  deploy rather than the backup if it is missing.
- **There is no EF global query filter.** `grep HasQueryFilter src/` returns one comment and no
  code. Nothing catches a forgotten conjunct.

### What is scoped, and what still is not

The interceptor's fallback is the string `'system'`, not "deny" (`CurrentScope`,
`TenantScopeInterceptor.cs:153-165`). A request with no org context runs **unscoped**, and some
requests genuinely have none.

**The login path is no longer one of them.** This document used to say the opposite — that a login
resolves a user before a tenant is known, and therefore cannot be scoped to one — and that was true
of the code when it was written. It is not true now. A Hydra login challenge names an OAuth2 client;
RediensIAM writes `org_id` and `project_id` into that client's metadata when the project is created;
`LoginChallengeProject.ResolveOrgOrNull` reads it back and `TenantScopeInterceptor.PinToOrganisationAsync`
publishes it as `rediensiam.org_id` before the request reads anything. Most of those pins cost **no
database read at all** — the organisation is already in metadata the server itself wrote.
`grep PinScope src/Controllers/AuthController.cs` is the complete list of the points at which it
happens: the login `GET` and `POST` including the skip path, registration and its verification,
consent, every MFA step, social start and callback, and the password-reset request.

Three properties are what make that pin defensible rather than merely present:

- **The scope is never derived from caller input.** Every source is server-authored — client
  metadata the API offers no way to set, a subject RediensIAM minted itself, a server-side session
  record, or a `projects` row. The e-mail or username typed into the form has no influence on it.
- **It refuses to move a request its own token already scoped elsewhere**
  (`PinToOrganisationAsync`, `:128-133`), so it cannot be turned into a way to widen an
  authenticated caller's reach. The refusal is deliberately narrow — a token naming no organisation,
  or the same one, is not a conflict — so a stray `Authorization` header on an ordinary login does
  not become a 500.
- **It does not change what a failed login reveals.** The queries it now runs under were already
  filtered on `project.AssignedUserListId`, and both user-list assignment endpoints refuse a list
  belonging to another organisation, so the RLS predicate adds no restriction those conjuncts did
  not already impose. A foreign tenant's address takes the same `user == null` path, with the same
  constant-time dummy verify and the same response body, as an address that was never registered.

Pinning also forced two latent defects into the open, and both are fixed. MFA failure audit rows
were written with `OrgId = null`, which the `audit_log` policy's `WITH CHECK` half refuses outright
under a pinned scope; they now carry the MFA session's org and project, so a tenant can finally see
its own users' MFA failures. And `FindOrCreateSocialUserAsync` matched a provider subject with **no
tenant constraint** — one Google account invited by two customers returned the *first* tenant's user
for the *second* tenant's login. The predicate now carries
`s.User.UserListId == project.AssignedUserListId`, which is what makes that fix independent of
whether RLS is switched on.

### What still runs unscoped, and why

`TenantScopeInterceptor.LegitimatelyUnscopedPaths` (`:71-92`) is the greppable list. It has twelve
entries and no longer contains "the whole of `AuthController`":

| Path | Why it is not scoped |
|---|---|
| `AuthController.AdminLogin` | the console's users live in the `__system__` list, whose `OrgId IS NULL`. The `users` policy is `EXISTS (… ul."OrgId" = rls_org())` and `NULL = <uuid>` is never true, so **no** organisation scope can see those rows. Structural, not an oversight |
| `GET /auth/consent`, admin-client branch | its query asks which organisations a console user administers; the cross-organisation scan *is* the question |
| `verify-email`, `invite/complete`, `password-reset/verify`, `password-reset/confirm` | the subject is named by a random token, and the read that resolves the token is itself the lookup |
| the fallback read of `projects` | only for a client registered before `org_id` was written into its metadata; that read is what decides the scope, so it cannot run under it |
| `GatewayAuthMiddleware` → PAT introspection | runs inside the middleware, before any claims exist; a PAT is found by hash |
| **`SamlController`** | it resolves a project from the challenge exactly as `AuthController` does, so it **can** be pinned. It has not been. Open — listed in §8 |
| migrations, super-admin bootstrap, `InstanceConfigurationProvider`, `AuditLogRetentionService`, `WebhookDispatcherService`, `SystemAdminController` | deployment-wide or cross-tenant by design |

The honest statement is narrower than it used to be, and it is still not "tenant isolation is now
structural": **RLS now covers the tenant login path as well as authenticated tenant API traffic.
What it does not cover is the admin console and the token-keyed endpoints, which have no tenant to
be scoped to, and the SAML ACS, which has one and does not use it yet.**

### The measurement

`iam_db_connection_scope_total{scope="system"|"org"}` (`src/Services/IamMetrics.cs:53-56`) counts
every connection checkout at the point the scope is decided, so the ratio is measurable rather than
asserted. `SECURITY-AUDIT-LOG.md` step 32 §4 reports ten complete
authorization-code logins driven against the dev cluster, before and after the change:

| Build | `scope="system"` | `scope="org"` |
|---|---|---|
| before | **+80** | 0 (series never emitted) |
| after | 0 | **+80** |

Eighty checkouts either way — the scoping costs no extra round trips. **Those numbers are quoted
from that report and were not re-measured for this document**; what was verified here is the
counter, every pin site named above, and the two defect fixes, all read in the source.

The `5 org-scoped / 15 system` sample this section used to print has been dropped rather than
updated: it was a mixed minute of traffic including SuperAdmin listings and PAT introspection, which
remain unscoped by design, so it never shared a denominator with the login figure it was being read
as.

Enabling RLS remains a real outage risk: the policies deny everything to a connection that has not
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
HKDF-SHA256 subkey (`AppConfig.DeriveKey`, `src/Config/AppConfig.cs:263-266`): TOTP secrets, webhook signing secrets,
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

**Hydra system secret rotation is a runbook only** (`SECURITY-AUDIT-LOG.md` step 16 §7.4).
There is no rotation code. Prepend, never replace, and keep the old entry for at least the
refresh-token TTL.

Read `SECURITY-AUDIT-LOG.md` step 16 §8 before rotating anything cryptographic — it is the
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

The first two of the three caveats this section used to carry are now closed. How they were closed
matters as much as that they were:

1. **The link is `HMAC-SHA256(K, prevHash ‖ row)`** under a purpose derived from the HKDF root, so
   database write access alone no longer produces a table that verifies. Rows written before the
   keying are stored bare and are counted **`Unverifiable`, never `Verified`** — re-chaining them
   was rejected deliberately, because recomputing an old row under the new key launders something
   unattestable into something attested. The boundary is *positional*: an unkeyed hash is accepted
   only before the first keyed row, or the boundary itself becomes the downgrade attack. Each row
   names its key id, so retiring a root makes its rows unverifiable rather than broken.
2. **`VerifyChainAsync` has a caller.** `IntegrityMonitorService` runs it at startup and every 24
   hours, and `GET /admin/audit-chain` exposes it. `iam_audit_chain_broken_orgs` and
   `iam_audit_chain_unverifiable_rows` are the gauges to alert on — a *rise* in the second matters
   as much as any value of the first.
3. **There is still no database-level append-only enforcement.** The guard is application-layer, and
   the application role must retain `DELETE` on `audit_log` because the retention sweep uses
   `ExecuteDeleteAsync`, which bypasses the change tracker and therefore the guard.

Two limits remain, and are not softened here: **tail truncation** — deleting the newest rows and
stopping — is still invisible without an external anchor, and there is **no outbox**, so a process
killed between the row write and the chain update leaves a gap the verifier reports as a break.

Retention has a hard floor of 90 days that a tenant cannot set below
(`AppConfig.MinAuditRetentionDays`, `src/Config/AppConfig.cs:275-281`), re-clamped on the delete
path so a stale value cannot slip through.

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
| Admin ingress TLS | on in prod, but self-signed by the release's own `Issuer` — a known defect, see below |
| Postgres server TLS + `hostssl` | **on in both shipped environments** |
| Postgres roles | four least-privilege login roles, `scram-sha-256`, no shared superuser in any DSN |
| Dragonfly TLS | set in **both** values files; executed in dev, and once under the prod profile in a scratch namespace — see below |
| NetworkPolicy | namespace-wide default-deny plus five lockdown policies; CGNAT (`100.64.0.0/10`) egress blocked |
| Pod hardening | non-root, drop `ALL` caps, no priv-esc, read-only root, `seccompProfile: RuntimeDefault`, `automountServiceAccountToken: false` |
| Image | pinned by `@sha256:` digest, `pullPolicy: IfNotPresent` |
| Backup | nightly `pg_dumpall` CronJob; restore proven byte-identical once |
| Management surface on the public host | denied at the ingress for `/admin`, `/org`, `/project`, `/service-accounts` |

Two of these need their qualifier stated, because a summary table elsewhere reads as unconditional:

- **Dragonfly TLS is set in both values files. It has run in dev, and once under the prod profile in
  a scratch namespace — never on a production cluster.**
  `values.prod.yaml` sets `dragonfly.local.tls.enabled: true`; the chart default in
  `values.yaml:339-340` remains `false`. The prod-profile install described in
  `SECURITY-AUDIT-LOG.md` step 33 did exercise it: cleartext `PING` was refused by the
  server, a TLS `PING` against the mounted CA succeeded, the same connection against the OS trust
  store was rejected, and the app read and wrote through the tunnel. That is a real observation and
  it is more than the rendering this document used to claim — but it was one namespace on the
  single-node dev cluster, for about an hour, from scratch. The upgrade path this control actually
  has to survive in production — a cutover against a cache that already holds an unprotected key
  ring — remains reasoned about, not observed. The application side is complete and *pinned*
  — `src/Config/CacheTls.cs` builds an `X509Chain` with `CustomRootTrust` over only the mounted CA,
  keeps name mismatch fatal, and requires the serverAuth EKU; it is not a `return true`. The chart
  mount exists. It is a **hard cutover** — `--tls` makes Dragonfly stop
  answering cleartext — so `cacheUrl` must gain `ssl=true` in the same `helm upgrade`, which the
  chart enforces as a render failure in both directions.
- **NetworkPolicy is decorative unless your CNI enforces it.** Verify that before trusting any row
  in this table; the two-minute check is in
  [`DEPLOYMENT.md`](DEPLOYMENT.md#before-the-first-install-on-a-new-cluster--two-minutes).

**No production cluster has ever run this.** The production *profile* — `values.yaml` +
`values.prod.yaml`, installed by `setup.sh --prod` — has now been applied once, to a scratch
namespace on the single-node dev cluster, and destroyed afterwards
(`SECURITY-AUDIT-LOG.md` step 33). That established only that the chart, the two
scripts and the values files agree with each other well enough to produce a running system with the
prod-only controls live. It found **six defects, five of which made a first-ever install fail
outright or report a control it had not measured**; all six are fixed. Two are worth naming here
because they change what earlier claims in this document were worth:

- **`verify-deployment.sh --prod` was measuring the wrong hostname.** It did not layer the operator
  answers from `values.prod.override.yaml`, so it probed the committed default host. Traefik answers
  404 on a Host it has no router for, V-04 counted 404 as a refusal, and **the P-04 management-API
  assertion therefore read green while measuring nothing at all.** V-04 now runs a positive control
  first — `/login` on the public host must answer 2xx/3xx, or the four deny probes are reported as
  inconclusive rather than as passes. On the fixed run the refusals are 403 from the `ipAllowList`
  middleware, not 404 from Traefik shrugging.
- **RLS could never have been enabled on any production database** this repository could create; see
  §2.

What a real production cluster still has to establish on its own — a publicly trusted certificate
(ACME HTTP-01 has never been executed), that the admin surface is actually private, that the data
survives anything, that an upgrade across a schema migration works — is listed in full in §8 of that
report. A scratch namespace is not production.

---

## 7. Application-layer controls, briefly

| Control | Where |
|---|---|
| Argon2id + rotatable pepper ring | `PasswordService.cs` |
| Password floor of 12 characters, enforced on every write path | `PasswordPolicy.cs:32` |
| Breached-password check, **on by default** for new projects | `Project.CheckBreachedPasswords = true` |
| Rate limiting per IP **and** per user account | `RateLimitService.IsBlockedAsync(ip, userId)` |
| Open-redirect allowlist | `RedirectValidator.cs` + `AuthController.SafeRedirect` |
| SSRF re-validation on every webhook delivery, with `ConnectCallback` address pinning | `WebhookUrlValidator.CreateSsrfSafeHandler`, `Program.cs:92,117,119` |
| HMAC-signed webhook deliveries | `WebhookService.ComputeSignature` |
| Per-org SMTP host validated against the mesh/loopback blocklist; submission port and STARTTLS required | `SmtpEndpointValidator.cs:21,38` |
| Tenant `custom_css` and every other theme value validated server-side | `LoginThemeValidator.cs` |
| Re-authentication on every MFA factor mutation | `AccountController.RequireReauthAsync` |
| Session revocation on role change, org suspension, deactivation, password change | `LiveAuthorizationService.InvalidateAsync`, `SystemAdminController.SuspendOrg`, `UserHelpers.ApplyUpdate` |
| Trusted-proxy fail-closed in production (the app **refuses to start** on an empty value) | `Program.cs:ConfigureForwardedHeaders` |
| `AllowedHosts` wildcard replaced with the derived host list | `Program.ReplaceWildcardAllowedHosts`, `Program.cs:33-34,437-446` |
| CSP with `script-src`, `base-uri`, `form-action`, `object-src`, `frame-ancestors` on both branches | `Program.cs:492-502` |
| Security parameters clamped so a hostile config row cannot weaken them | `AppConfig.cs:73,95,96,103-105,122,281` |
| SAML response `Destination` validated against this deployment's ACS URL | `SamlService.DestinationMatches`, called from `SamlController.cs:178` — read the limit below |
| Trust anchors (`Hydra:*Url`, `Keto:*Url`, `App:TrustedProxies`) excluded from the DB config layer | `InstanceConfiguration.cs:92,122` |

Two of these do less than their one-line summary suggests, and neither should be read without its
qualifier:

- **The SAML `Destination` check is solid only against IdPs that sign the response.** SAML 2.0 core
  §3.2.2 makes the attribute optional, so an absent `Destination` is accepted and logged at Warning
  rather than refused — requiring it would break working IdPs that omit it, and would not close the
  gap anyway. An IdP that signs the *response* puts `Destination` inside the signature, where it
  cannot be altered without invalidating it; an IdP that signs only the *assertion* leaves the
  `<Response>` element and its `Destination` unprotected, and this SP accepts that shape. Against an
  attacker holding such a response the check is bypassable by rewriting the attribute. It stops
  naive relaying between two endpoints of *this same SP*, where the audience is identical and
  therefore proves nothing, and it stops a misconfigured endpoint. It is defence in depth. The
  load-bearing controls on that path remain `AllowedAudienceUris`, the pinned signing certificate
  and the single-use `InResponseTo` record. The library-behaviour half of this — what ITfoxtec 4.17.0
  does and does not validate — is taken from `SECURITY-AUDIT-LOG.md` step 36,
  which established it by decompiling the referenced assembly; it was **not independently re-derived
  for this document**. What was verified here is the comparison rule, the call site and the ordering.
- **This is a behaviour change for existing SAML integrations.** A response whose `Destination` does
  not resolve to `{App:PublicUrl}/auth/saml/acs` is now `400 saml_response_invalid`. Host case,
  scheme case, an explicitly written `:443`/`:80` and a trailing slash are all tolerated; a different
  host, a different path, a scheme downgrade or a genuinely different port are not. Before upgrading,
  check `GET /auth/saml/metadata` — `AssertionConsumerService/@Location` prints the exact value the
  app will compare against — against the ACS URL registered at each IdP. The usual way this bites is
  an `App:PublicUrl` pointing at a cluster-internal address while the IdP holds the public ingress
  hostname.

---

## 8. What is deliberately still open

Nothing below is a surprise. Each is either a decision with a stated cost or a residual whose
exploitable half is narrow. Anything not listed here and not described above as working should be
assumed unaddressed.

### Open, with severity

| Item | Severity | State | Why it is open |
|---|---|---|---|
| **npm advisories in both SPAs** | High | **7 high per SPA**, down from 8 high + 1 low. `react-router` and `brace-expansion` among them; remaining fixes need `npm audit fix --force`, i.e. breaking major bumps | The forced upgrades were judged riskier than the advisories as reached by these SPAs. That judgement has not been re-tested since the SPAs were rewritten |
| **The Dragonfly TLS *cutover* in prod is untested** | Medium | the control itself has now been observed live under the prod profile in a scratch namespace (§6). What has never been run is the upgrade path: a cache that already holds an unprotected DataProtection key ring | A hard cutover. It costs every session at the moment it happens, and a prod Dragonfly whose key ring survives the upgrade needs `DEL rediensiam:dataprotection:keys` first — see `DEPLOYMENT.md` |
| **Registry unauthenticated, no TLS, no signature verification** | Low **today**, Medium in production | Bound to loopback and digest-pinned, so the reachable attack is narrow: it needs local access to the host already. **No production deployment exists**, so this is currently a development-only exposure. It becomes Medium the day a registry is reachable off-host — at which point binding and authentication move together, never one without the other | needs a registry with auth, or an image pull from somewhere that has one |
| **Prod admin certificate is self-signed** | Medium, **in production only** | `values.prod.yaml` leaves `clusterIssuer: ""`, so the chart issues the admin ingress a certificate from its own Issuer. The cost is not the cryptography — it is that the operator is trained to click through a browser warning to reach the most privileged UI in the system, which is the habit an attacker relies on. No production deployment exists yet, so nobody has been trained into it | needs an internal CA the browsers already trust, or ACME DNS-01 on the Tailscale domain |
| **ACME / Let's Encrypt has never been executed** | Medium, **in production only** | The `letsencrypt` ClusterIssuer renders and lints, and has never issued a certificate. Dev serves `iam.localhost`, which no CA will certify, and the prod-profile proof ran in a scratch namespace with no public DNS. So the path is untested rather than known-broken — and an issuance failure at first production install is discovered at the worst moment | needs a real hostname, public DNS, and port 80 reaching Traefik for HTTP-01 |
| **No outbox for the Keto/`org_roles` dual write** | Low, structural | `GrantReconciler` now detects and repairs divergence daily; the write window itself is still not atomic, so a killed process still creates a gap that lives until the next run | an outbox is the real fix and was not attempted |
| **RLS off in prod** | Medium, structural — **but nothing to migrate today** | Live in dev, and applied once to a from-scratch database in the prod-profile proof. `setup.sh --prod` leaves it off on a first install. **A fresh installation has no migration to perform**: the policies apply at initdb and the `BYPASSRLS` grant is unconditional since 0.2.2. The word "migration" applies only to a database created before that release, and none exists | needs an operator to set `postgres.rls.enabled` on the install that creates the database |
| **The SAML ACS is not tenant-scoped** | Low–Medium | `SamlController` resolves a project from the login challenge exactly as `AuthController` does, so it can call `PinToOrganisationAsync`; it does not. It is named in `LegitimatelyUnscopedPaths` | ~3 lines, and the only reason it is open is that the file belonged to a different work stream when the login path was scoped. Its lookups are already filtered on `project.AssignedUserListId`, so this is a missing backstop, not a missing conjunct |
| **SAML `Destination` is bypassable against assertion-only signers** | Low | see §7 — the check is inside the signature only when the IdP signs the `<Response>` element | Closing it means refusing responses that sign the assertion alone, which is a shape this SP accepts today and some IdPs only emit |
| **No DB-level append-only on `audit_log`; tail truncation still invisible** | Low–Medium | see §5. The chain is keyed and verified daily now; what remains is that the application role must keep `DELETE` for the retention sweep, and that deleting the newest rows and stopping leaves nothing to detect | append-only needs a Postgres rule or trigger the retention sweep can bypass explicitly; truncation needs an off-box anchor |
| **`/api/authorize` object check skips ownership when both scopes are absent** | Low–Medium | `IsObjectInScopeAsync` checks only that the namespace is known when the caller is deployment-level *and* the subject token has no `org_id`. The `System` namespace is refused to every caller before this point, so what remains is an unowned check against `Organisations` / `Projects` / `UserLists` | Not named by any report; found while writing this document. The `System` half was closed in `75e9576`; the rest needs a decision about what a deployment-level caller with an org-less token may legitimately ask |
| **`GET /admin/system/health` returns raw `ex.Message`** | Low | the SMTP username is redacted; two branches still return exception text (`SystemHealthController` `:222`, `:245`) | Treat this route as equivalent to a stack trace |
| **Breach check fails open** | Low | `BreachCheckService.cs:35` — `return 0` on an outage | Deliberate availability trade |
| **SAML pending state can still be burned by a registered IdP** | Low | **Partly closed.** The order is now `ReadSamlResponse` → status → `Destination` → **`Unbind`** → `GetAndDeletePendingAsync`, so an unsigned or misdirected response no longer consumes anything — that was I-10 as reported. What remains: `Unbind` proves the response was signed by *some* configured IdP, but the IdP-to-challenge binding needs the pending record in hand, so a holder of **any** registered IdP's signing key can still burn a guessed request id | Closing it fully means peek-validate-delete, which trades away the atomic single-use of the pending record. That is the worse trade — a replayable pending record is a live authentication weakness, where this is a denial of service against one in-flight login by an already-trusted party |
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

- **SAML XML processing beyond what was assessed.** `SECURITY-AUDIT-LOG.md` step 15a
  §7` assessed T-26 and reports one real defect found and a clean bill on the two things it was
  feared for. `src/Services/SamlService.cs:39` sets
  `CertificateValidationMode = X509CertificateValidationMode.None`, and ITfoxtec's defaults govern
  `XmlResolver` / `DtdProcessing`. Do not read that assessment as an exhaustive XXE and
  signature-wrapping review. The `Destination` work in
  `SECURITY-AUDIT-LOG.md` step 36 decompiled the library to establish four
  specific facts about `Saml2Request.Read` and `Saml2Configuration`; that is not the same as an
  audit of its XML processing, and it did not attempt one.

---

## 9. Reporting a vulnerability

There is no published disclosure process in this repository. Until there is, the audit trail in
`SECURITY-AUDIT-LOG.md` is the record of what has been looked at and by whom. Start with its
`README.md`, which says which reports have been retired and why, and then with
`11-pentest-results.md` — the only one that set out to break the others. The finding ledger this
section used to point at, `14-finding-ledger.md`, is no longer in the repository; it was moved out
because it went stale.
