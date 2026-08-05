# RediensIAM Architecture

How the system is put together, and where its authority actually lives.

Written against the code. Where a `file:line` reference appears it was checked; routes, type names
and method names are cited instead of line numbers for anything under `src/Controllers/`, which
moves too often for line numbers to stay true. If this document and the code disagree, the code
wins and this document is a bug.

Companion reads: [`DIAGRAMS.md`](DIAGRAMS.md) (this document, drawn — topology, the request
pipeline, the authorisation decision, the OIDC and introspection sequences, the data model, key
material and the audit chain), [`SECURITY.md`](SECURITY.md) (what protects what, and what does not),
[`API.md`](API.md) (every route), [`INTEGRATION.md`](INTEGRATION.md) (the integrator contract),
[`DEPLOYMENT.md`](DEPLOYMENT.md) (the operator guide), [`TESTING.md`](TESTING.md).

---

## Philosophy

1. **Stateless app, stateful infrastructure.** Pods carry no per-instance data. Postgres holds all of it —
   the durable state (users, orgs, projects, audit log) **and** the shared short-lived state
   (sessions, rate-limit counters, OTP challenges, the DataProtection key ring, the webhook queue).
   One datastore, not two: none of that second list was a cache, and every item in it either
   breaks or weakens when a replica keeps its own copy. Any number of replicas can run side by
   side.
2. **Standards over re-invention.** OAuth2/OIDC = Ory Hydra. Fine-grained authorisation = Ory Keto.
   Argon2id for passwords. WebAuthn level 2 for passkeys. We do not re-implement these.
3. **Authority is a type, not a convention.** A level read out of a token is a *claim*. A level
   confirmed against Keto during this request is a *grant*. They are different C# types and the
   compiler will not let one stand in for the other. This is the single most important structural
   property of the codebase and the rest of this document keeps referring back to it.
4. **Defence in depth, but no magic.** Every webhook URL is re-validated for SSRF on each delivery.
   Every redirect target passes through an allowlist. Static analysers (SecurityCodeScan,
   SonarAnalyzer.CSharp) run at build time — and neither models cross-tenant authorisation, so a
   clean quality gate is not evidence of tenant isolation.

---

## Components

```
        ┌────────────────┐                 ┌────────────────┐
        │  Login SPA     │                 │  Admin SPA     │
        │ (Vite + React) │                 │ (Vite + React) │
        └────────┬───────┘                 └────────┬───────┘
                 │                                  │
       ┌─────────▼──────────┐        ┌──────────────▼───────┐
       │ Backend public :5000│        │ Backend admin :5001  │
       │   (one dotnet process, two listeners)               │
       └─────────┬──────────┘        └──────────────┬───────┘
                 │                                  │
                 └──────┬──────────────┬────────────┘
                        │              │
                 ┌──────▼──────┐┌──────▼──────┐┌──────────────┐
                 │  Ory Hydra  ││  Ory Keto   ││  PostgreSQL  │
                 └─────────────┘└─────────────┘└──────────────┘
```

One binary listens on two ports. **The port split is not a trust boundary and must not be treated
as one.** `app.MapControllers()` maps every route on *both* listeners; only the Swagger UI
(`src/Program.cs:343`) and the Prometheus `/metrics` scrape endpoint (`:389`) are gated on the
admin port. The real separation is the **public hostname**, enforced at the ingress — see
[Reachability](#reachability-the-public-host-the-admin-host-and-apimanage) below and
[`API.md`](API.md).

---

## Authorisation: the claim/grant split

This is the S-1 structural change from `SECURITY-AUDIT-LOG.md` step 03, and unlike
most of that list it is fully landed, including the compile-time half.

### The two types

| Type | Means | Who can make one |
|---|---|---|
| `ManagementLevel` (`src/Config/Roles.cs:4`) | a plain enum — `SuperAdmin = 1, OrgAdmin = 2, ProjectAdmin = 3, None = 99`. Lower is more privileged. | anyone |
| `GrantedLevel` (`src/Services/GrantedLevel.cs:25`) | a level **confirmed against Keto during this request** | nothing outside the type itself |

`GrantedLevel` is a `readonly struct` with a private constructor (`GrantedLevel.cs:30`). Its only
producer is `GrantedLevel.ResolveAsync` (`:43-48`), which calls
`LiveAuthorizationService.IsStillGrantedAsync` and returns `null` — never a level — when the claim
is absent or no longer granted. You cannot fabricate one, cast to one, or deserialise one.

`ClaimsExtensions.GetManagementLevel` — the old reader that returned a level straight off
`ext.roles` — **no longer exists**. It was made private and then deleted; only the comment in
`src/Middleware/GatewayAuthMiddleware.cs:58-62` explaining why remains. Reading a claimed level as
authority no longer compiles. The one deliberate way to read a claim as a claim is
`GrantedLevel.ClaimedLevel(claims)` (`GrantedLevel.cs:57`) — `internal`, and named for what it is;
introspection uses it, because introspection is asking about *somebody else's* token.

### How a request acquires a grant

1. `GatewayAuthMiddleware` (`src/Middleware/GatewayAuthMiddleware.cs`) resolves the bearer token —
   PAT prefix → `PatService.IntrospectAsync`, otherwise `HydraService.ValidateJwtAsync` — and puts
   the resulting `TokenClaims` in `HttpContext.Items["Claims"]`.
2. It calls `ClaimsExtensions.MarkCallerClaims(claims)` (`:63`), registering this instance in a
   `ConditionalWeakTable<TokenClaims, StrongBox<GrantedLevel?>>` (`:133`) with a null grant.
3. `RequireManagementLevelAttribute` runs as an action filter. It rejects on the claim first as a
   cheap pre-filter, then calls `GrantedLevel.ResolveAsync`, then `RecordGrantedLevel` (`:154`).
4. Controllers read `HttpContext.GetGrantedLevel()` (`:173`), which is `GrantedLevel?` and is
   `null` until step 3 has run. Null is the only honest answer when no live check happened.

The weak table is keyed by object identity and every request deserialises its own `TokenClaims`,
so nothing leaks between requests.

### Default deny

Before S-1, an action was privileged because somebody remembered to write
`[RequireManagementLevel]` on it. `ServiceAccountController` once did not (finding R-22), and the
unauthenticated `/admin` GET branch was safe only because every controller happened to carry one
(finding I-02).

`GatewayAuthMiddleware` now refuses any request on a management prefix that reaches an MVC action
carrying no authorisation gate (`:69-75`), with `403 {"error":"forbidden","detail":"no_authorisation_gate"}`:

```
ManagementPrefixes = ["/admin", "/org", "/project", "/service-accounts", "/api", "/internal"]
```

The exemption set is a single greppable array, `SelfGatedControllers` (`:85-91`), and it currently
holds exactly one entry: `"Introspection"`. `/api/introspect` and `/api/authorize` gate on *being a
service account*, which is a different question from a management level. A new controller added on
any management prefix fails closed.

A second gate runs just before it (`:49-55`): the **audience gate**. A valid access token is not
automatically a token meant for this surface. Unless the caller is a PAT or a service-account
client (`sa_` prefix), its `client_id` must appear in `Security:ManagementClientIds`, or the
request is `403 token_audience_not_allowed`. Without this, any token Hydra issued — including one
minted for a tenant's own application — would reach the management API with only `ext.roles`
standing in the way.

### Keto is the single authority (S-8)

`LiveAuthorizationService.CheckAsync` (`src/Services/LiveAuthorizationService.cs:71-93`) does two
things and no more: refuse if the claimed organisation is suspended (`:82-84`, skipped for
SuperAdmin), then ask Keto. The `|| await db.OrgRoles.AnyAsync(...)` fallback that used to answer
"project_admin *somewhere*" to the question "project_admin *here*" is gone; only the comment
recording its removal remains (`:86-88`).

There is one implementation of the question — `KetoService.IsManagementLevelGrantedAsync`
(`src/Services/KetoService.cs:96-123`) — and it accepts three tuple shapes for ProjectAdmin
(`:113-119`).

Verdicts are cached per user and level for **30 seconds** (`LiveAuthorizationService.cs:28`). That
window, not the token lifetime, is the upper bound on how long a revoked role keeps working on this
deployment's own surface. Keto being unreachable is a **403, not a pass** (`:58-64`) — and the
negative verdict is cached for the same 30 s, so a Keto outage produces sticky refusals rather than
sticky admissions.

**Honest limit.** `org_roles` is still written alongside every Keto tuple. It is no longer
*consulted* for an authorisation answer — it holds the scope, the display name and the grant
provenance — but the dual write is real, it is tuple-first/row-second with a compensating tuple
delete in the `catch`, and **there is no reconciler and no outbox**. A killed process between the
two writes leaves them divergent. `AuthController`'s consent path also still reads `db.OrgRoles`
(`AuthController.cs:654-662`) to resolve scopes into the minted token, *after* the role list itself
came from Keto — so "Keto is the only store consulted anywhere" would be an overstatement.

---

## Audit as a property of persistence (S-3)

Audit used to be ~98 hand-written `RecordAsync` calls and nothing else, which is exactly the
"someone forgot" failure mode. It now has a floor underneath it.

`RediensIamDbContext.SaveChangesAsync` (`src/Data/RediensIamDbContext.cs:48-70`) is overridden, and
every save does three things in order:

1. **`RejectAuditLogTampering()`** (`:84-91`) — throws if any tracked `AuditLog` entity is
   `Modified` or `Deleted`. Application-layer only; see the caveat below.
2. **`RecordUnloggedSecurityMutationsAsync()`** (`:158-198`) — writes an audit row for security
   mutations *without a call site*. A `User` whose `PasswordHash`, `TotpSecret`, `TotpEnabled`,
   `WebAuthnEnabled`, `Phone`, `PhoneVerified`, `Email`, `EmailVerified` or `Active` changed
   (`:151-156`) produces `entity.users.credential_changed` naming the changed columns. Any state
   change on `BackupCode`, `WebAuthnCredential`, `UserSocialAccount`, `SamlIdpConfig` or `Instance`
   produces `entity.{table}.{inserted|updated|deleted}` (`:217-236`).
3. **`ChainAsync(pending)`** (`:110-140`) — links the new rows into the hash chain, inside a
   transaction it opens if none exists.

The 99 hand-written `RecordAsync` calls are still there and were kept deliberately
(`RediensIamDbContext.cs:44-46`): they carry *intent* ("this was a role revocation"), which a
column diff cannot. The automatic rows are named `entity.*` so a query can tell the two apart.

### The hash chain

`AuditLog` gained `Hash` and `PrevHash` columns (migration
`src/Data/Migrations/20260731132739_AuditLogHashChain.cs`). `AuditChain.Compute`
(`src/Data/AuditChain.cs:45-63`) hashes a ``-separated canonical string of
`prevHash, OrgId, ProjectId, UserId, ActorId, Action, TargetType, TargetId, IpAddress, UserAgent,
CreatedAt` plus canonicalised metadata (keys sorted ordinal).

The chain is **per organisation** (`RediensIamDbContext.cs:118`), because retention purges are per
organisation and a global chain would break on every purge. `ChainAsync` takes a
`pg_advisory_xact_lock` per org in fixed key order (`:125`) so concurrent writers cannot interleave
into the same chain.

`AuditChain.FirstBreak` (`AuditChain.cs:85-97`) returns the id of the first row whose link fails.
Its entry point is `AuditLogService.VerifyChainAsync` (`src/Services/AuditLogService.cs:68-79`).

**Two caveats this document will not bury.** The hash is **plain unkeyed SHA-256**, not an HMAC:
it detects accidental corruption and a careless edit, and it does not stop anyone who can write to
the table from recomputing the chain. And `VerifyChainAsync` **has no production caller** — no
endpoint, no hosted service, no schedule. It exists and it is tested; nothing runs it for you. See
[`SECURITY.md`](SECURITY.md#5-the-audit-trail).

There is no database-level append-only enforcement. The application role must retain `DELETE` on
`audit_log` because the retention sweep uses `ExecuteDeleteAsync`
(`src/Services/AuditLogRetentionService.cs:48,56`), which bypasses the change tracker and therefore
the guard in step 1.

---

## Tenant scope and row-level security (S-5)

### The interceptor — implemented and active

`src/Data/TenantScopeInterceptor.cs` is a `DbConnectionInterceptor` registered on the application
`DbContext` (`src/Program.cs:41-45`). On every `ConnectionOpened` — i.e. every pool checkout — it
issues one parameterised statement (`:106-113`):

```sql
SELECT set_config('rediensiam.org_id', @value, false)
```

`@value` is the caller's org UUID when the request has one, and the literal string `'system'`
otherwise (`CurrentScope`, `:153-165`). `false` means session scope rather than `SET LOCAL`, so it
survives the individual command.

The login flow has no token and therefore no claims, so it supplies its own value:
`PinToOrganisationAsync` parks an organisation on `HttpContext.Items`, which `CurrentScope` reads
ahead of the claims, and issues the setting on the connection the context is already holding.
The organisation comes from the `org_id` RediensIAM wrote into the login challenge's OAuth2 client
metadata at project creation — a value the API offers no way for a caller to set — so most pins cost
no database read. `grep PinScope src/Controllers/AuthController.cs` is the complete list of sites.

Two connection-string shapes would silently break the invariant, and both are refused at startup by
`AppConfig.ConnectionString` (`src/Config/AppConfig.cs:18-52`) rather than tolerated:
`No Reset On Close=true` (Npgsql's `DISCARD ALL` on pool return is what clears the setting) and
`Multiplexing=true` (one physical session shared by several logical connections).

`LegitimatelyUnscopedPaths` (`TenantScopeInterceptor.cs:71-92`) is the auditable list of the code
paths that run as `'system'` on purpose. It has twelve entries and it no longer names the whole of
`AuthController`: `AdminLogin` (the `__system__` user list has `OrgId IS NULL`, so no tenant scope
can see it), the consent handler's admin-client branch, the token-keyed endpoints, the fallback
`projects` read, PAT introspection in `GatewayAuthMiddleware`, `SamlController` (which *can* be
pinned and is not — see [`SECURITY.md`](SECURITY.md#what-still-runs-unscoped-and-why)), plus schema
creation, super-admin bootstrap, the instance-config provider, the audit retention sweep, the
webhook dispatcher and `SystemAdminController`.

### RLS itself — shipped, not enabled

`deploy/rediensiam/files/rls.sql` exists and is complete: `ALTER TABLE … FORCE ROW LEVEL SECURITY`
plus a `rediensiam_tenant` policy per tenant table (`rls.sql:159-166`), and it *raises* if a public
table is neither policied nor declared deployment-global (`:181`). It is applied by a
post-install/post-upgrade Job.

`postgres.rls.enabled` is **`false`** in `values.yaml:314-315`. `values.dev.yaml` overrides it to
**`true`** — 19 tables carry a policy on the dev cluster — and `values.prod.yaml` does not override
it, so **RLS is on in dev and off in prod**. The policies are fail-closed —
a connection that has not set the variable sees zero rows in every tenant table, which for an
identity provider is a total outage, not a degraded mode — so turning it on is a runbook, not a
flag flip. See [`DEPLOYMENT.md`](DEPLOYMENT.md#turning-rls-on).

The model declares **no EF global query filter**: `grep HasQueryFilter src/` returns one comment and
no code. Tenant scoping in the application is still ~200 hand-written conjuncts. RLS would be the
schema-level backstop under them; today there is none.

**Why RLS would not make the system tenant-safe on its own** is important enough that it lives in
[`SECURITY.md`](SECURITY.md#2-tenant-isolation-and-its-honest-limit) rather than here.

---

## Secrets, encryption and rotation (S-10)

### One root, many purposes

`Security:EncryptionKey` (or the key ring, below) is a 32-byte root. Nothing uses it directly.
Every purpose gets its own HKDF-SHA256 subkey (`AppConfig.cs:255-260`): TOTP secrets, webhook
signing secrets, per-org SMTP passwords, login themes (`:196-199`), the DataProtection key ring
(`:207`), and the device fingerprint key (`:209-215`, deliberately *not* versioned — it follows the
active root only and invalidates known-device state on retirement, which is the safe direction).

### The `k<id>:` ciphertext envelope

Before rotation existed, a ciphertext carried no key identifier, so rotating the root destroyed
every TOTP secret, webhook secret and SMTP password in every tenant at once. Ciphertexts now carry
their key id (`src/Services/TotpEncryptionService.cs:81-89`):

```
k<id>:  ‖  Base64( nonce[12] ‖ tag[16] ‖ ciphertext )      AES-GCM
```

Key id **1 writes no prefix at all** (`LegacyKeyId`, `:52-59`), so a deployment that never rotated
produces byte-identical output to the pre-rotation build. The parser accepts `k<digits>:` with a
positive id and treats anything else as key 1; the disambiguation is safe because Base64 contains
no `:`.

The ring is `Security:EncryptionKeys`, format `id:hex,id:hex,…` with the **active key first**
(`AppConfig.cs:159-183`). Everything new is encrypted under the first entry; everything listed can
decrypt. Ids are positive integers and must never be reused. A malformed ring is a startup failure,
not a first-decrypt failure (`Program.cs:438-440`).

Decrypting under an id that is not configured throws a `CryptographicException` naming the id
(`TotpEncryptionService.cs:34-38`) rather than returning garbage.

The re-encryption sweep is an **operator-triggered admin endpoint**, not a background job:
`GET /admin/key-rotation` (status) and `POST /admin/key-rotation/reencrypt`
(`SystemAdminController`, backed by `src/Services/KeyRotationService.cs`). It covers four columns
(`:47-53`) — `User.TotpSecret`, `Webhook.SecretEnc`, `OrgSmtpConfig.PasswordEnc` and the
`Project.LoginTheme` jsonb — in batches of 500 with no resume cursor; re-run it to continue.
`totalPending == 0` is the only signal that a retired key may safely be dropped.

### The Argon2 pepper

Password hashes carry a `$k=<id>` suffix on the standard PHC string
(`src/Services/PasswordService.cs:82-87`); pepper id 1 and "no pepper" write no marker, so an
un-rotated deployment is again byte-identical. The ring is `Security:Argon2Peppers`, same format
and same validation.

A pepper cannot be swept: the plaintext password only exists at verify time. `NeedsRepepper`
(`:74-81`) is checked after every successful login (`AuthController`) and the hash is re-derived
then. Finishing a pepper rotation is therefore a policy decision about dormant accounts, not a job
that completes. Verification is fail-closed on a dropped pepper (`:30-43`, `:97`).

Backup codes have their own versioned format `sha256:{keyId}:{hex}` (`:107-124`).

### The Hydra system secret

Rotation is a **runbook only** (`SECURITY-AUDIT-LOG.md` step 16 §7.4). There is no
rotation code in `src/`. Hydra takes a list and signs with the first entry, so the procedure is
prepend-never-replace and keep the old entry for at least the refresh-token TTL.

---

## Data stores and what lives where

### Postgres — four roles, not one

The single `iam` SUPERUSER shared by the app, Hydra and Keto is gone. `postgres.yaml`'s `init.sh`
creates four least-privilege login roles on a first-ever start
(`deploy/rediensiam/values.yaml:196-210`):

| Role | Database | Purpose |
|---|---|---|
| `iam_app` | `rediensiam` | the application |
| `iam_hydra` | `hydra` | Ory Hydra |
| `iam_keto` | `keto` | Ory Keto |
| `iam_backup` | read-only across all three | the nightly `pg_dumpall` |

Each is `NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS`
(`deploy/rediensiam/templates/postgres.yaml:85-91`); `CONNECT` is revoked from `PUBLIC` on all four
databases first (`:102-105`), which is what makes the isolation real. `iam_backup` gets
`GRANT pg_read_all_data` (`:92`) and the dump runs `--no-role-passwords`
(`templates/backup.yaml:121`), which is the only part that would otherwise need superuser.

`iam` **is still a SUPERUSER** — initdb always makes `POSTGRES_USER` one. It is now used by nothing
at runtime: it is initdb's owner and the break-glass account, and it belongs in no DSN.
`verify-deployment.sh` V-21 fails the deployment if any DSN username is `iam`.

Authentication is **scram-sha-256** on both `local` and `host` lines
(`POSTGRES_INITDB_ARGS`, `postgres.yaml:191-192`); the old `local all all trust` is gone, and
`verify-deployment.sh` V-20 fails if any `pg_hba` line still ends in `trust`.

`init.sh` only ever runs against an empty data directory, so **an existing installation does not
get the split, or scram, from a chart upgrade**. The live migration is
`SECURITY-AUDIT-LOG.md` step 15c §T-04.

Durable state: users, orgs, projects, roles and assignments; hashed PATs; service accounts;
WebAuthn credentials; webhooks and delivery history; the audit log; and the single-row `instances`
configuration table.


Sessions (the MFA challenge step), the DataProtection key ring, rate-limit counters, the OTP store,
the PAT introspection cache (5 min TTL), the Hydra introspect cache (≤ 60 s, clamped by token
`exp`), the webhook job queue and the OAuth2 social-login state store.

The PAT cache **skips the join, never the decision**: `PatService.IntrospectAsync` re-checks on
every hit that the PAT is unexpired and that its service account and organisation are still live,
so deactivating a service account takes effect immediately rather than at TTL.

### Per-pod state

HTTP request scope, in-process caches (OIDC discovery, the dummy Argon2 hash), and the webhook
be replaced without losing anything user-visible.

---

## Configuration model — Zitadel-style

Runtime configuration (URLs, ports, SMTP, rate-limit thresholds, Argon2 parameters) lives in a
single-row **`instances`** table. Pods read it at startup; environment variables go dormant after
the first boot, so a fleet is reconfigured atomically by changing one row.

| Scenario | Result |
|---|---|
| First start (row missing) | read env → write row → use values from row |
| Normal start (row present) | load row → ignore env for these keys |
| `RECONFIGURE_FROM_ENV=true` | read env → overwrite row → bump `config_version` |

Editing a value in `values.yaml` without setting `RECONFIGURE_FROM_ENV` does nothing.

⚠ **The chart has no generic environment passthrough.** `deployment.yaml` sets a fixed list of
variables and there is no `rediensiam.env` map in `values.yaml`. `INSTANCE_ID` and
`RECONFIGURE_FROM_ENV` are read by `InstanceConfiguration.cs:186-187` but **cannot be set through
the chart as it ships** — reconfiguring a running instance currently means editing
`templates/deployment.yaml`, or writing the `instances` row directly. Older documentation described
a `yq -i '.rediensiam.env.RECONFIGURE_FROM_ENV = "true"'` recipe; that key does not exist.

**Trust anchors are deliberately excluded from the DB-sourced layer** (finding R-14/T-N5). A
mutable database row must not be able to redirect the deployment's authorisation store:
`Hydra:*Url`, `Keto:*Url` and `App:TrustedProxies` are absent from the dict
`InstanceConfiguration.cs` builds (`:84`, `:114-125`), and every security parameter it *does* carry
is `Math.Clamp`ed on read (`AppConfig.cs:25,47,48,57,64,118-119`) so a hostile row cannot set the
Argon2 cost to 1 or the audit retention to 0.

Secrets stay env-only: the connection strings, `Security:EncryptionKey(s)`,
`Security:Argon2Pepper(s)`, `IAM_BOOTSTRAP_EMAIL` / `IAM_BOOTSTRAP_PASSWORD`, and `INSTANCE_ID`
(which row to load).

| Piece | File |
|---|---|
| Entity | `src/Data/Entities/Instance.cs` |
| Provider (DB row → `IConfiguration`) | `src/Config/InstanceConfiguration.cs` |
| Wired at startup | `src/Program.cs` (`AddInstanceConfiguration()`) |

---

## Reachability: the public host, the admin host, and `/api/manage`

Because the listener port is not a boundary, the boundary is the ingress. The public host serves a
catch-all to the app and then a **second Ingress with a `255.255.255.255/32` `ipAllowList`
middleware** — an unconditional deny, not an allowlist whose correctness depends on what source IP
Traefik sees — over the longer path prefixes (`deploy/rediensiam/templates/ingress.yaml:110-165`):

```yaml
adminOnlyPaths: [/admin, /org, /project, /service-accounts]
```

`/auth`, `/account` and `/api` are deliberately **absent**. The login SPA and end-user self-service
are public by design, and `/api` carries the resource-server surface (`/api/introspect`,
`/api/authorize`) that in-cluster gateways call — plus `/api/manage`, which is the machine-callable
alias of the whole super-admin surface.

That alias is one controller with two `[Route]` attributes, not a second implementation:

```csharp
[Route("admin")]
[Route("api/manage")]
[RequireManagementLevel(ManagementLevel.SuperAdmin)]
public class SystemAdminController
```

`SystemHealthController` (`admin/system` + `api/manage/system`) and `AdminWebhookController`
(`admin/webhooks` + `api/manage/webhooks`) do the same. One action, one token check, one live Keto
re-check, whichever prefix you used. A duplicated handler is where an authorisation check goes
missing, and the seven `/api/manage` re-implementations that used to exist had already drifted.

The practical consequence: **`/api/manage/*` is reachable on the public hostname and `/admin/*` is
not**, by design. A machine credential needs a route that does not depend on reaching the admin
ingress; an interactive console does not. Full route table in [`API.md`](API.md).

---

## Authentication flows

### Password login

```
Browser  →  /auth/login              (challenge from Hydra)
Backend  →  validate user/password   (Argon2id + optional peppered ring)
Backend  →  rate-limit check         (per IP and per user)
Backend  →  if MFA enrolled or required → /auth/mfa/…
Backend  →  hydra.AcceptLoginAsync(subject = orgId:userId)
Browser  →  Hydra consent flow → /auth/consent
Hydra    →  redirect to SPA with code
SPA      →  exchange code for token at Hydra public
```

### Social login (Google / GitHub / generic OIDC)

```
Browser  →  /auth/oauth2/start?provider_id=…
Backend  →  build authorize URL server-side, SafeRedirect to provider
Provider →  /auth/oauth2/callback?code=…
Backend  →  exchange code, fetch profile (email must be verified)
Backend  →  find-or-create user, link social account
Backend  →  hydra.AcceptLoginAsync → SafeRedirect into the normal flow
```

Discovery-derived endpoints are re-validated against the SSRF blocklist
(`src/Services/SocialLoginService.cs:409`) and all three outbound HTTP clients use an
SSRF-safe handler that pins the resolved address in a `ConnectCallback`
(`src/Program.cs:85,109,111`), which is what closes the DNS-rebinding TOCTOU.

### SAML login

```
Browser  →  /auth/saml/start?idp_id=…
            the IdP must belong to the login challenge's project (SamlController.Start)
Backend  →  AuthnRequest (unsigned; SP metadata advertises AuthnRequestsSigned="false")
IdP      →  POST SAMLResponse to /auth/saml/acs
Backend  →  verify signature against the pinned IdP certificate
Backend  →  JIT-provision the user if enabled
Backend  →  hydra.AcceptLoginAsync
```

`SsoUrl` must be an absolute HTTPS URL (`src/Services/SamlService.cs:85-86`). `/auth/saml/acs`
carries `[IgnoreAntiforgeryToken]` — a signature-verified POST from an external IdP cannot carry a
CSRF token.

### MFA

- **SMS OTP** — rate-limited per user (default 3 / 10 min). The shipped provider is
  `StubSmsService` and **does not deliver**; it reports `IsConfigured => false` so the server does
  not offer an undeliverable factor.
- **WebAuthn** — Fido2NetLib, `UserVerification = Required` on both registration and assertion: as
  a *second* factor, possession of the authenticator is not the point, the PIN or biometric is.
  Assertions are looked up scoped to the user pending MFA, so another account's authenticator
  cannot satisfy the factor. Resident keys are discouraged; there is no passwordless entry point.
- **Backup codes** — HMAC-SHA256, versioned `sha256:{keyId}:{hex}`.

The session cookie is rotated on every successful MFA completion, to defeat session fixation.

**Enforcement.** A project sets `Project.RequireMfa`; a user with no factor is sent through
enrolment (`requires_mfa_setup`) rather than refused. The management console applies the same
shape without a setting: the first administrator of a deployment signs in on a password alone, and
every one after that is sent through enrolment. Turning `require_mfa` *off* on a project with
enrolled users is a two-step confirmed call — see
[`INTEGRATION.md`](INTEGRATION.md#turning-require_mfa-off).

**Mutating a factor** requires re-authentication against an existing one (`current_password` or
`totp_code`; refused with `401 reauthentication_required` naming the methods the account can
supply). This covers adding, replacing and removing — enrolling the attacker's own authenticator
*alongside* the victim's is the same takeover as overwriting theirs. Enrolling the *first* factor on
an account that has none needs no proof, and an account with no re-auth method and no factor is
handled explicitly rather than falling through.

---

## Trust boundaries

| From → To | Trust |
|---|---|
| Browser → public listener `:5000` | Untrusted. Rate-limited at the ingress and in-app, anti-CSRF, CSP |
| Browser → admin listener `:5001` | OIDC-authenticated JWT bearer, plus the audience gate. Not cookie-free: `/account/mfa/totp/*` and `/account/mfa/webauthn/*` keep setup state in the ASP.NET session cookie, which `SameSite=Strict` blocks whenever `App__AdminSpaOrigin` differs from `App__PublicUrl` |
| Backend → Hydra admin `:4445` | In-cluster only; NetworkPolicy-locked. **Verify your CNI enforces NetworkPolicy** — if it does not, Hydra's admin API is open to the whole cluster |
| Backend → Keto write `:4467` | Same |
| Backend → Postgres `:5432` | Role `iam_app`, own database only; TLS on in both shipped environments; NetworkPolicy locked to {app, hydra, keto} |
| Hydra → public listener (consent) | Browser-mediated redirect; allowlist via `RedirectValidator` |
| External IdP → `/auth/saml/acs` | SAML assertion verified against the pinned IdP certificate |
| Operator / machine → management API | Bearer PAT or `client_credentials` token; audience gate, then live Keto re-check per request |

---

## Transport and at-rest encryption in the cluster

| | Chart default | dev | prod |
|---|---|---|---|
| Public ingress TLS | off | **off** — `iam.localhost` cannot be certified | on (`letsencrypt`) |
| Admin ingress TLS | — | NodePort, no ingress | on, but self-signed by the release's own namespaced `Issuer` |
| Postgres server TLS (`postgres.local.tls.enabled`) | off | **on** | **on** |
| Postgres `requireSsl` (`hostssl` in `pg_hba.conf`) | off | **on** | **on** |
| `postgres.rls.enabled` | off | **on** | off |

Dev being cleartext on the ingress is the one place finding R-02 is not fixed, and it is gated to
dev so prod cannot inherit it.

`cacheUrl` must gain `ssl=true` in the same change. `deploy.sh` derives it from the flag and
by accident. It is set in **both** `values.dev.yaml` and `values.prod.yaml`. It has been executed in
dev, and once under the prod profile in a scratch namespace on the dev cluster, where the server was
confirmed to refuse cleartext and the app to read and write through the pinned tunnel
(`SECURITY-AUDIT-LOG.md` step 33 §3). No production cluster has run it, and the
*cutover* — flipping this on a cache that is already up and already holds a key ring — remains
reasoned from the dev experience rather than observed.

The app's cache TLS is **pinned, not trusting** (`src/Config/CacheTls.cs`, wired at
`src/Program.cs:53-59`). The callback builds an `X509Chain` with `TrustMode = CustomRootTrust` over
only the roots mounted at `/etc/cache-tls/ca.crt` (`:122-123`); name mismatch stays fatal (`:117`);
the serverAuth EKU is required (`:128`). It is not a `return true`. `RevocationMode = NoCheck` is a
stated ceiling — cert-manager publishes no CRL or OCSP responder (`:124-127`). On a cleartext DSN
the whole thing is a no-op (`:68`).

### The DataProtection key ring

survive a pod restart, and it is **encrypted at rest** (`src/Program.cs:68-71`):

```csharp
builder.Services.AddDataProtection()
    .PersistKeysToStackExchangeRedis(cacheMultiplexer, "rediensiam:dataprotection:keys")
    .ProtectKeysWithRootKey(appConfig)
    .SetApplicationName("rediensiam");
```

Each key is wrapped in AES-GCM under an HKDF-SHA256 subkey of the encryption root
(`info = "rediensiam-dataprotection-v1"`, `src/Config/AppConfig.cs:207`), using the same
`k<id>:base64(nonce‖tag‖ciphertext)` envelope as everything else — so root rotation covers the key
ring for free.

The important half is the **read** side. `EncryptedOnlyXmlRepository.GetAllElements`
(`src/Config/KeyRingProtection.cs:122-140`) throws on any `<key>` element that is not wrapped,
rather than adopting it. An attacker with *write* access to the cache could otherwise append a
plaintext key that DataProtection would happily use to mint session cookies. The failure mode is a
startup exception naming the fix (`DEL rediensiam:dataprotection:keys`, one-time session loss),
which is the correct trade.

---

## Where to go next

| Question | Document |
|---|---|
| The same system, drawn — topology, pipeline, authorisation, sequences, data model, keys, audit chain | [`DIAGRAMS.md`](DIAGRAMS.md) |
| What actually protects what, and what is still open | [`SECURITY.md`](SECURITY.md) |
| Every route, its authority, and where it is reachable | [`API.md`](API.md) |
| How to integrate an app or a resource server | [`INTEGRATION.md`](INTEGRATION.md) |
| How to install, upgrade and rotate | [`DEPLOYMENT.md`](DEPLOYMENT.md) |
| How to run the tests | [`TESTING.md`](TESTING.md) |
| The audit trail itself, step by step | `SECURITY-AUDIT-LOG.md` — the step index, and which reports were retired and why |
