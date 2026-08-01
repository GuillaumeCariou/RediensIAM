# Changelog

All notable changes to RediensIAM.

Versions are `MAJOR.MINOR.PATCH`. Before 1.0.0, **a minor bump may break the wire contract** — and
0.2.0 breaks it in four places. The chart version, the chart `appVersion`, the container image tag,
all three SDKs and both SPAs share one number.

---

## [0.2.2] — unreleased

No wire-contract change. **But one behaviour change for existing SAML integrations, and an upgrade
of an existing deployment now needs manual steps for the first time in this series — read "Before
you upgrade" below.**

### Fixed

- **One tenant's user could be signed in for another tenant's social login.**
  `FindOrCreateSocialUserAsync` matched a provider subject on `Provider` and `ProviderUserId` alone,
  with no tenant constraint. A single Google account invited by two customers resolves to the same
  `ProviderUserId`, so the *first* tenant's user was returned for the *second* tenant's login and
  the session was minted against it. The lookup is now constrained to
  `project.AssignedUserListId`. This is a real cross-tenant authentication path and it was open.
- **MFA failure audit rows were written with no tenant.** `VerifySmsOtp` and `VerifyTotp` recorded
  `user.mfa.*.failed` with `OrgId = null`, so the affected tenant could not see its own users' MFA
  failures in its audit view. They now record against the pending MFA session's org and project.
- **`Database:MigrateOnStartup` was decorative.** The key shipped in `appsettings.json` and nothing
  read it: `Program.cs` migrated unconditionally, so an operator who set it to `false` still got
  migrations applied, with no signal that the instruction had been ignored. It is honoured now.
  Default is still `true`, so nothing changes until someone sets it. When false, the app starts and
  logs at Warning how many migrations it is behind — refusing to boot would make the switch useless,
  and staying quiet would surface an un-migrated schema as unexplained 500s a long way from the
  cause.
- **`verify-deployment.sh --prod` measured the wrong host, and V-04 passed because of it.** The
  script did not layer the operator answers from `values.prod.override.yaml`, so it probed the
  committed default hostname. Traefik answers 404 on a Host it has no router for, V-04 counted 404
  as a refusal, and the P-04 management-API assertion **read green while measuring nothing**. The
  override file is now layered, and V-04 requires `/login` on the public host to answer first or the
  deny probes are reported inconclusive rather than passing.
- **Row-level security could never have been enabled on any production database.** `init.sh` granted
  `iam_backup` its required `BYPASSRLS` only when `postgres.rls.enabled` was already true at initdb
  — and `setup.sh --prod` forces RLS off on a first install without asking. The grant is
  unconditional now. **Databases created before this release still need
  `ALTER ROLE iam_backup BYPASSRLS` applied by hand**; `files/rls.sql` fails the deploy rather than
  the backup if it is missing.
- **A first-ever production install could not complete**, in four further ways, all in `deploy/`:
  a fixed-name cluster-scoped `ClusterIssuer/selfsigned` that made `helm install` fail on any
  cluster already holding one and let `helm uninstall` of either release delete the other's issuer;
  PostgreSQL crash-looping on a fresh volume because a freshly provisioned root is owned by uid 0
  and `initdb` cannot chmod a directory it does not own; `helm --wait` deadlocking on a backup PVC
  that a `WaitForFirstConsumer` StorageClass will not bind until the nightly CronJob fires; and a
  smoke probe whose failed `curl` killed the whole script under `set -e`, telling the operator a
  healthy install might be half-changed.

### Added

- **The tenant login path now runs under its own organisation's RLS scope.** A Hydra login challenge
  names an OAuth2 client, and RediensIAM writes `org_id` into that client's metadata at project
  creation — so the tenant is knowable before any user lookup, usually with **no database read at
  all**. `TenantScopeInterceptor.PinToOrganisationAsync` publishes it as `rediensiam.org_id` before
  the request reads anything. Password login, registration and its verification, consent, every MFA
  step, the social flows and the password-reset request are all pinned. Measured on the dev cluster
  over ten complete authorization-code logins: 80 connection checkouts, **0 scoped before, 80 scoped
  after**, with no extra round trips. The scope is never derived from caller input, and the pin
  refuses to move a request its own token already scoped to a different organisation.
- **SAML responses are validated against this deployment's ACS URL.** A response whose `Destination`
  names a different endpoint is refused. See the behaviour change below, and
  [`docs/SECURITY.md`](docs/SECURITY.md) §7 for the limit — this is solid against IdPs that sign the
  `<Response>` element and bypassable against IdPs that sign only the assertion, which this SP
  accepts.
- `iam_db_connection_scope_total{scope}` — a Prometheus counter incremented at the point the
  database scope is decided, so the scoped/unscoped ratio is measurable instead of asserted.

### Changed

- The chart's self-signed issuer is a namespaced `Issuer/<release>-selfsigned` rather than a
  cluster-scoped `ClusterIssuer/selfsigned`. `ingress.admin.clusterIssuer` now defaults to `""`,
  meaning "the chart's own Issuer"; a non-empty value still names a real ClusterIssuer you run.
- `PGDATA` moved to `/var/lib/postgresql/data/pgdata`.
- `deploy.sh` no longer passes `helm --wait`; it runs `kubectl rollout status` on the workloads that
  actually render. The RLS post-upgrade Job now polls for `__EFMigrationsHistory` and fails loudly
  rather than assuming Helm ordered it after the app.
- Four dead configuration keys removed from `appsettings.json`: `Cache:ProjectTtlMinutes`,
  `Cache:JwksTtlMinutes`, `App:FrontendUrl`, `App:LoginPath`. None had a reader anywhere in the
  repository. No chart or environment-variable spelling referenced them either.

### Before you upgrade

1. **SAML operators, before anything else.** For each configured IdP, confirm that
   `{App:PublicUrl}/auth/saml/acs` is the ACS URL registered at that IdP. `GET /auth/saml/metadata`
   prints the exact value the app will compare against, in `AssertionConsumerService/@Location`.
   Host case, scheme case, an explicitly written `:443`/`:80` and a trailing slash are tolerated; a
   different host, a different path, a scheme downgrade or a genuinely different port are not. The
   usual way this bites is an `App:PublicUrl` pointing at a cluster-internal address while the IdP
   holds the public ingress hostname. Miss it and every SAML login fails with
   `400 saml_response_invalid`. IdPs that send no `Destination` are unaffected — absence is accepted
   and logged at Warning.
2. **The PGDATA move is a migration.** An installation created before this release keeps its data
   directory at the volume mount root. A `pgdata-location-guard` init container **will stop the next
   deploy on purpose** rather than let Postgres `initdb` an empty cluster beside the real data and
   report success. It prints the commands; it is one `mv` with the StatefulSet scaled to zero. Doing
   nothing is safe — a running pod is unaffected.
3. **The issuer rename re-issues the Postgres and Dragonfly certificates**, which restarts
   Dragonfly, which empties the DataProtection key ring. Every session is invalidated once.
4. **If you intend to enable RLS on an existing database**, apply
   `ALTER ROLE iam_backup BYPASSRLS` as superuser first. See above.

### Known limits

- The production profile has now been installed once — into a scratch namespace on the single-node
  dev cluster, then destroyed. That establishes that the chart, the two scripts and the values files
  agree with each other. **It does not establish that production works.** ACME has never been
  executed and no publicly trusted certificate has ever been issued; no backup has been restored; no
  upgrade has been run across a schema migration on a populated database; the Postgres `requireSsl`
  and Dragonfly TLS *cutovers* against pre-existing state remain reasoned about, not observed;
  nothing has been up longer than an hour. A scratch namespace is not production.
- Scoping the login path does not make tenant isolation structural. The admin console cannot be
  scoped (its users live in a list with `OrgId IS NULL`, invisible under every tenant scope by
  construction), the token-keyed endpoints identify their subject by a random token, and
  **`SamlController` can be pinned and has not been.** See
  [`docs/SECURITY.md`](docs/SECURITY.md#what-is-scoped-and-what-still-is-not).
- RLS remains off by default and in `values.prod.yaml`.

---

## [0.2.1]

No wire-contract change. Upgrading from 0.2.0 needs nothing beyond a redeploy.

### Fixed

- **Admin console: a failed MFA mutation was reported to nobody.** `ReauthDialog`'s `guard()`
  resolved when the re-authentication prompt opened rather than when the mutation finished, so a
  mutation that failed *after* a correct proof — a 500, a 409, a dropped connection — threw into a
  caller whose `catch` had already gone out of scope, and the rethrow became an unhandled promise
  rejection. The user typed the right password, the prompt closed, and no error appeared:
  indistinguishable from success. On regenerating backup codes or deleting a passkey, believing a
  change happened when it did not is how an account gets locked out. Unreachable on a *bad* proof,
  which is why reading the code never caught it.
- Login form: the primary email-or-username field had no accessible name.

### Added

- **First tests for both SPAs** — 150 across seven files. `vitest` and `@testing-library` had been
  installed in both and left entirely unused, so every authentication change in 0.2.0 was defended
  by a typecheck and by someone having read it. The bug above is what they found.
- **Row-level security is enabled**, 19 tables with `ENABLE` + `FORCE`. Without `FORCE` the table
  owner is exempt and the policies are decorative. `ALTER ROLE iam_backup BYPASSRLS` is now granted
  at initdb and enforced by a precondition that aborts the deploy — without it `pg_dumpall` fails
  and the nightly backup stops silently, which is worse than the finding it closes.
- `k3s --secrets-encryption` documented in the README, including the `reencrypt` step: enabling the
  flag protects only what is written afterwards.

### Changed

- `values.prod.yaml` now enables Dragonfly TLS. **Verified by rendering only** — there is no
  production cluster, so this is not proven. A cache that survives the upgrade holding an
  unprotected DataProtection key ring needs `DEL rediensiam:dataprotection:keys` first; see
  `docs/DEPLOYMENT.md`.

### Known limits

Enabling RLS does **not** make the login path tenant-safe: users are resolved by e-mail before a
tenant is known, so that path runs unscoped by necessity. Measured over one minute of
tenant-exercising traffic: 5 org-scoped connections, 15 as `'system'`. See `docs/SECURITY.md`.

*(This was true of 0.2.1 and is left as the record of it. **Superseded in 0.2.2**, which pins the
tenant from the login challenge before any user lookup.)*

---

## [0.2.0] — unreleased

The security-hardening release. It is a **breaking** release for anyone who integrates against
`/api/introspect`, `/api/authorize` or the `ext.roles` claim, and it changes how the deployment
talks to its own database.

### Read this first — the upgrade in order

Four wire-contract changes ship together, and one of them makes **deploy order load-bearing**.

1. **Audit your consumers of `ext.roles`.** Any code doing `roles.contains("admin")` on a *tenant*
   role stops matching. It fails closed, so nothing is silently granted — but people lose access.
   Fix these before you deploy anything. See [break 1](#1-extroles-is-project-qualified).
2. **Decide which service account each resource server uses.** An org-scoped service account now
   gets `active: false` for other organisations' tokens. A multi-tenant gateway needs a
   deployment-level (`__system__`) service account. See [break 2](#2-introspection-and-authorisation-are-tenant-scoped).
3. **Deploy the server.** `aud` becomes mandatory the moment it starts.
   See [break 3](#3-aud-is-required-and-every-answer-carries-ver-1).
4. **Then upgrade the SDKs**, setting the new required audience option in each service.
   See [break 4](#4-the-sdks-refuse-a-cleartext-url-a-missing-audience-and-a-server-without-ver).

Steps 3 and 4 are in that order for a reason. An **upgraded SDK refuses every answer from an
un-upgraded server** — it is the anti-downgrade check doing its job, not a bug. The reverse
direction is harmless: an old SDK against a new server gets a clear `400 audience_required`, and an
old server silently ignores the `aud` a new SDK sends, which is exactly the case the check exists
to catch.

If you must upgrade the SDK first, expect that service to be down until the server follows.

---

## Breaking — the wire contract

### 1. `ext.roles` is project-qualified

Tenant role names are chosen by tenant admins and were emitted bare. Two tenants both naming a role
`admin` were byte-identical in every consumer's token, and nothing stopped a tenant from naming one
`super_admin`.

| | 0.1.0 | 0.2.0 |
|---|---|---|
| Tenant role in `ext.roles` | `"admin"` | `"{project_id}/admin"` |
| Management role in `ext.roles` | `"org_admin"` | `"org_admin"` — unchanged, still bare |
| Tenant role named `super_admin` | accepted | rejected at creation, `{"error":"role_name_reserved"}` |

The three management names (`super_admin`, `org_admin`, `project_admin`) are reserved
case-insensitively, and `/` is now rejected in a tenant role name so the qualified form is
unambiguous.

**What fails:** `roles.contains("admin")` matches nothing. In the .NET SDK the qualified string is
what lands in `ClaimTypes.Role`, so `[Authorize(Roles = "admin")]` stops matching — it used to
match *every* tenant at once, which is the defect. The direction of failure is closed, never open.

**What to do:**

| Language | Was | Now |
|---|---|---|
| C# | `info.HasRole("admin")` | `info.HasProjectRole(projectId, "admin")` |
| Rust | `info.has_role("admin")` | `info.has_project_role(&project_id, "admin")` |
| Browser | `iam.hasRole('admin')` | `iam.hasProjectRole('admin')` — project defaults to the token's |
| Raw | `roles.includes("admin")` | `roles.includes(projectId + "/admin")` |

`HasRole` / `has_role` / `hasRole` still exist and now match **management roles only**. Note the
argument order differs: project first in the backend SDKs, role first in the browser one (the
project is optional there).

**Where the tenant role names themselves are unchanged:** this is a claim-encoding change, not a
data migration. Nothing in the database moved.

### 2. Introspection and authorisation are tenant-scoped

`POST /api/introspect` and `POST /api/authorize` now answer only about the caller's own tenant.

- A service account attached to an **organisation** may introspect that organisation's tokens
  only. Another organisation's token answers `{"active": false, "ver": 1}` — deliberately
  indistinguishable from expired or revoked, because confirming that someone else's token exists
  is the disclosure being closed.
- A **deployment-level** service account — one attached to the `__system__` user list, holding no
  org-scoped role — stays unscoped, because that is what a multi-tenant gateway must hold.
- On `/api/authorize`, the `object` is scoped too: asking about another tenant's object answers
  `{"allowed": false}`, in the same shape as a genuine deny so the endpoint cannot be used to
  enumerate. Every refusal writes an `api.authorize.object_out_of_scope` audit row.
- The `System` Keto namespace is refused to **every** caller. `System:rediensiam#super_admin`
  enumerates the deployment's administrators and never authorises the caller's own request. A
  resource server that needs to know whether a subject is a super admin reads the `roles` field of
  `/api/introspect`, which re-verifies against Keto before answering.

**What fails:** a gateway holding one tenant's service account, used to validate several tenants'
tokens, starts denying everyone outside its own organisation.

**What to do:** give a multi-tenant gateway a `__system__` service account. Give a single-tenant
resource server that tenant's own account — it is strictly better scoped. See
[`docs/INTEGRATION.md`](docs/INTEGRATION.md).

### 3. `aud` is required, and every answer carries `ver: 1`

Both endpoints now require the caller to declare which tenant it serves.

```
POST /api/introspect   (form)  token=…&token_type_hint=access_token&aud=<project-or-org-id>
POST /api/authorize    (json)  {"token":…,"namespace":…,"object":…,"relation":…,"aud":"<project-or-org-id>"}
```

Omit it and the answer is `400 {"error":"audience_required","ver":1}`. **No grace period, no
opt-out.** A token that does not belong to the declared audience reads `{"active": false}` /
`{"allowed": false}` — again the same shape as a dead token.

`aud` may be the project id the service fronts, or the organisation id if it fronts a whole
organisation. Matching against `project_id`/`org_id` is case-insensitive; matching against the
token's own OAuth2 audience list is case-sensitive.

> RediensIAM does not itself set an OAuth2 `aud` on the tokens it mints — `AcceptConsentAsync`
> sends no `grant_access_token_audience`. Binding therefore works through `project_id` / `org_id`
> unless the relying party requested an audience at `/oauth2/auth`.

`ver: 1` is stamped on every 200 answer — including `{"active": false}` — and on the
`audience_required` 400. It is **not** on `403 service_account_required`, on ASP.NET Core's
`ValidationProblemDetails` for a missing required field, or on the middleware 401. An SDK enforcing
"`ver >= 1` or fail closed" is unaffected: all of those are non-200s.

`ver` is a **response field, not a token claim.** It describes the server's capability, not the
token.

**What to do:** send `aud`. If you use the SDKs, that is the new required option in break 4.

### 4. The SDKs refuse a cleartext URL, a missing audience, and a server without `ver`

All three checks fire **at construction**, not on the first request: a deployment mistake should
stop the process at startup with a message naming the fix, not turn into failures once traffic
arrives.

| | C# (`RediensIAM.Client`) | Rust (`rediensiam-client`) | Browser (`rediensiam-web`) |
|---|---|---|---|
| Audience option | `RediensIamOptions.Audience` — **required** | `Config::audience` — **required** | n/a |
| Missing audience | `ArgumentException` | `Error::Config` | n/a |
| Cleartext base URL | `ArgumentException` | `Error::Config` | `RediensIamError('config_invalid')` on `issuer` and each `apiOrigins` entry |
| Answer without `ver` | `InvalidOperationException` | `Error::ServerTooOld { found }` | n/a — never calls those endpoints |

**HTTPS is required.** `http://` is accepted **only** on a loopback host — `localhost`,
`127.0.0.1` (all of `127.0.0.0/8` in the .NET SDK, via `Uri.IsLoopback`), `::1`/`[::1]`. There is
deliberately no flag to disable the check, because a flag gets set in production too. The
service-account credential and every token being introspected ride on that URL.

**There is no default audience and there will not be one.** A default is a guess about which
tenant a service belongs to, and a wrong guess reproduces the finding this closes.

**The `ver` check is why deploy order matters.** A server older than contract version 1 does not
*reject* the `aud` you send — it silently discards the unknown field and answers exactly as it
always did. An SDK that only *sent* `aud` could not tell an enforcing server from an ignoring one
and would report success while bound to nothing. Requiring `ver` turns that silent failure into a
loud one:

```
RediensIAM answered with ver=0, expected at least 1: this server predates mandatory
audience binding and silently ignored the aud this client sent. Upgrade RediensIAM
before trusting its answers.
```

**The browser SDK needs no migration.** `rediensiam-web` never called these endpoints, because
introspection needs a service-account credential and a credential shipped to a browser belongs to
anyone who opens devtools. It gained no audience option and never will.

#### Symptom table

| Symptom | Cause |
|---|---|
| Throws at startup naming `Audience` / `audience` | SDK upgraded, option not set for that service |
| Throws at startup naming `https` | `BaseUrl`/`base_url`/`issuer` is `http://` to a non-loopback host |
| `400 audience_required` | An un-upgraded SDK, or a raw-HTTP caller, against an upgraded server |
| `ver=0, expected at least 1` / `ServerTooOld` | An upgraded SDK against an un-upgraded server — deploy order |
| `{"active": false}` on a token you know is good | The `aud` you configured names a different tenant than the token belongs to, **or** your service account belongs to a different organisation than the token |
| A role check that used to pass now fails | Break 1 — the role is qualified now |

---

## Breaking — behaviour you will meet as an error

### `POST /account/mfa/phone/setup` answers `503`

Where the deployment has no SMS provider wired up — the provider is a stub — phone enrolment now
returns `503 {"error":"sms_provider_not_configured"}` instead of appearing to succeed.

**What to do:** handle the 503 as "this deployment cannot do SMS", not as a transient failure. If
you present SMS as an MFA option in your own UI, gate it on this.

### Removing a project's `require_mfa` needs an explicit confirmation

Turning `require_mfa` **off** on a project is refused the first time:

```
409 {"error": "mfa_downgrade_requires_confirmation",
     "enrolled_user_count": 47,
     "consequence": "…a stolen password alone becomes sufficient to sign in…",
     "confirm_with": "confirm_mfa_downgrade"}
```

Retry the same request with `"confirm_mfa_downgrade": true` in the body and it proceeds, writing a
`project.mfa_requirement_removed` audit row. The confirmation travels in the body of the request it
authorises, so it cannot be replayed onto a different one.

Only the true → false direction is guarded, and only when the project's assigned user list contains
at least one user holding a factor (TOTP, verified phone, or WebAuthn credential); with zero
enrolled users there is nothing to warn about and the request proceeds unchanged. Enabling
`require_mfa` is untouched.

This applies on all three prefixes that reach the setting: `/admin` + `/api/manage`, `/org`,
`/project`.

**What to do:** if you script project settings, add the field to any request that clears
`require_mfa`.

---

## Breaking — storage and deployment

### Encrypted values gained a key-id envelope

Ciphertexts written by `TotpEncryption` — TOTP secrets, webhook secrets, SMTP passwords, social
provider client secrets in a project's login theme — may now carry a `k<id>:` prefix naming the
key they were encrypted under.

**This is inert until you rotate.** Key id 1 is written with an **empty** prefix, so a deployment
that has never rotated keeps writing the exact byte format it wrote before, and a value with no
prefix is read as key 1. Upgrading to 0.2.0 alone changes nothing on disk.

Once you add a second key to `Security:EncryptionKeys` and make it active, new writes are prefixed
`k2:`, and `POST /admin/key-rotation/reencrypt` sweeps the cold rows (`GET /admin/key-rotation`
reports what is pending — **`TotalPending == 0` is the only signal that a retired key may be
dropped from the ring**). Dropping a key that still has data under it is unrecoverable; the code
throws a `CryptographicException` naming the missing key id rather than failing silently.

> **Rolling back after a rotation is not clean.** A 0.1.0 binary has no envelope parser: it feeds
> `k2:…` straight to `Convert.FromBase64String` and throws. Any row rewritten under key 2 is
> unreadable to the older release. Before rotating, be sure you are done rolling back — or restore
> from a backup taken before the sweep.

### PostgreSQL is split into four least-privilege roles

The shared `iam` SUPERUSER is no longer a runtime credential. A fresh install creates
`iam_app`, `iam_hydra`, `iam_keto` and `iam_backup` — all `NOSUPERUSER NOCREATEDB NOCREATEROLE
NOBYPASSRLS`, each owning only its own database, with `iam_backup` holding `pg_read_all_data` and
nothing else. `iam` remains as initdb's owner and the break-glass account, and appears in no DSN.
`pg_hba.conf` is `scram-sha-256`.

Four new chart values carry the passwords; `deploy.sh` generates all four:

```
postgres.local.roles.appPassword
postgres.local.roles.hydraPassword
postgres.local.roles.ketoPassword
postgres.local.roles.backupPassword
```

> **These roles are created only on a first-ever start**, because they are created by initdb. An
> **existing** installation is not migrated by upgrading the chart — it keeps running on `iam` and
> gets none of the benefit. The manual migration is in
> [`.security-hardening/15c-infra-residuals.md`](.security-hardening/15c-infra-residuals.md).

The `iam` SUPERUSER with `local all all trust` in `pg_hba.conf` on an existing cluster remains the
highest-ranked open finding in the ledger (T-04). Splitting the roles on a *new* install is the
first half of closing it.

---

## Added

- **`/api/manage/*` — a machine-to-machine parity surface.** `SystemAdminController` is now routed
  under both `/admin` (what the admin SPA calls) and `/api/manage` (reachable with a SuperAdmin PAT
  or a `client_credentials` token). The same actions on the same class, not a second
  implementation — a re-implementation is exactly where an authorisation check goes missing, and
  the `ManagedApiController` that used to re-implement seven of them is gone. Every route passes
  through one `RequireManagementLevel` attribute, i.e. one live Keto re-check, whichever prefix was
  used. `WebhookController` and `SystemHealthController` expose `/api/manage/webhooks` and
  `/api/manage/system` on the same basis.

  **Reachability is an ingress property, not an application one.** `adminOnlyPaths` is
  `[/admin, /org, /project, /service-accounts]`; `/api` is deliberately absent, which is why
  `/api/manage/*` answers on the public host and `/admin/*` returns 403 there. Both listeners map
  every controller — the port split is not a trust boundary.
- **`ver` on the introspection and authorisation answers** (see break 3).
- **Key rotation.** `GET /admin/key-rotation` and `POST /admin/key-rotation/reencrypt`, plus the
  `Security:EncryptionKeys` ring and the `security.argon2Peppers` pepper ring. Operator-triggered
  rather than a background job, so it runs when every replica already has both keys and cannot race
  N replicas rewriting the same rows.
- **Row-level security**, complete and **shipped off** (`postgres.rls.enabled: false` everywhere).
  Policies, SQL and the migration Job are in the chart; the application side (the tenant-scope
  interceptor) is in the build. Turning it on before verifying the application half on a live
  connection is a total outage — see [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).
- **Documentation.** [`docs/API.md`](docs/API.md) (184 routes),
  [`docs/SECURITY.md`](docs/SECURITY.md), [`docs/TESTING.md`](docs/TESTING.md), a rewritten
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), a README per SDK and per SPA, and this file.

## Changed

- **Audience matching, role resolution and scope checks are re-verified live.** Management roles
  reported by `/api/introspect` are re-checked against Keto before being returned, so a role revoked
  after the token was minted does not survive in the answer.
- **Cache keys in the backend SDKs are SHA-256 digests**, replacing a 64-bit non-cryptographic hash
  in the Rust client. The cache returns a full `TokenInfo` — roles included — before any server
  call, so a collidable key was an authentication bypass.
- **The browser SDK's `fetch()` refuses off-origin targets.** The bearer is attached only to the
  app's own origin or to an origin listed in the new `apiOrigins` option; anything else throws
  `untrusted_target`.
- **The browser SDK validates the discovery document.** Every endpoint it uses must sit on the
  issuer's own origin, otherwise whoever answers for the issuer chooses where the PKCE verifier and
  the refresh token are sent.
- **Redis/Dragonfly traffic is TLS-pinned** and the DataProtection key ring is encrypted at rest
  under the root key. On in dev; `values.prod.yaml` sets no `dragonfly` block, so **prod inherits
  `enabled: false`**.
- **`App__TrustedProxies` must be set explicitly in production.** Silently trusting RFC1918 lets any
  pod in a multi-tenant cluster spoof `X-Forwarded-For` and bypass per-IP controls. The chart ships
  the k3s CIDRs for dev.
- **CSP is set by the server**, with a distinct policy for `/admin` (which pins `connect-src` to the
  exact issuer origin) and for the login pages. Each SPA's `index.html` carries a meta policy too;
  browsers enforce the intersection, and a request must satisfy both.

## Security

- **The audit log gained a floor and an append-only guard.** Security-relevant mutations are
  recorded on `SaveChanges` itself, so an endpoint written next year cannot ship unaudited by
  omission. These automatic rows are named `entity.*`, so a query can tell them from the
  hand-written ones that carry intent (`user.password.reset`) — **if you parse the audit log, expect
  new `entity.*` action names.** Updating or deleting an audit row throws.
  Rows are chained per organisation, but the chain is **unkeyed SHA-256, not an HMAC**, and its
  verifier has no production caller — tamper-evidence against a careless adversary, not a capable
  one. Database-side append-only enforcement is not in place. Stated plainly in
  [`docs/SECURITY.md`](docs/SECURITY.md) §5.
- SAML `idp_id` is bound to the challenge's project (`SamlController.Start` 404s otherwise).
- The container registry is bound to loopback and images are deployed **by digest**, so a pod
  restart replays the exact bytes that were reviewed.

## Not fixed

This release does not close everything. The ranked list of what is still open — including the
`iam` SUPERUSER on existing clusters, the absence of a rotation story for the HKDF root and the
Argon2 pepper, an untested backup restore, and 7 high npm advisories in each SPA — was
`.security-hardening/14-finding-ledger.md` §9 and §10. That ledger has since been moved out of the
repository for going stale; **[`docs/SECURITY.md`](docs/SECURITY.md) §8 is now the only current
statement of what is open and why.**

---

## [0.1.0]

The pre-hardening baseline. There is no changelog entry for it: the repository's only tag before
this release is `v0.0.1` (April 2026), the chart said `0.1.0`, the image tag said `0.0.1` and both
SPAs said `0.0.0`. 0.2.0 is the first release where all of those agree, and the first with a
changelog.

Treat "0.1.0" as "anything deployed from this repository before 0.2.0". Every break above applies.
