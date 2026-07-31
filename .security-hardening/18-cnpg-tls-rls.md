# Step 18 — CloudNativePG support, R-15 (transport encryption), S-5 phase 2 (RLS)

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Scope:** `deploy/` only
**Cluster:** single-node k3s, namespace `default`
**Final state:** 5 pods Running · `./deploy/verify-deployment.sh --dev` → **31 passed · 0 failed · 3 skipped**
(was 27 / 0 / 2 at the end of step 15c)

Builds on `15c-infra-residuals.md`. Nothing it established was undone: the four per-component
Postgres roles, `scram-sha-256`, the working backup and its restore proof, the `checksum/secret`
annotation and the namespace-wide default-deny are all still in place and still asserted.

---

## Summary

| # | Item | Outcome |
|---|---|---|
| 1 | CloudNativePG as an option | **Done in the chart.** `postgres.local.enabled=false` swaps the database out; StatefulSet, init ConfigMap, TLS Certificate, `postgres-lockdown` and the nightly `pg_dumpall` CronJob all stand down, and the three egress NetworkPolicies retarget at the CNPG pods. Built-in StatefulSet remains the default and is what is deployed |
| 2 | R-15 — Postgres TLS | **CLOSED, live and verified.** cert-manager v1.21.1 installed; app, Hydra and Keto all on TLS 1.3; `pg_hba.conf` rewritten to `hostssl`, so cleartext is refused by the *server*, proven by a refused connection |
| 2 | R-15 — Dragonfly TLS | **STOPPED, deliberately, at a named blocker.** Attempted twice on the live cluster. Blocked by an application-side gap, not by infrastructure: `ConnectionMultiplexer.ConnectAsync(string)` cannot trust a self-signed certificate. Evidence below. Rolled back; cache is healthy |
| 2 | *New:* the cache had **no password at all** | **Fixed.** Found because Dragonfly refuses TLS without an auth method. `--requirepass=` was empty on every install this repo has ever produced |
| 3 | S-5 phase 2 — RLS | **Implemented behind a flag, default OFF, and NOT enabled live** — because it cannot be, until the application half ships. Policies rehearsed end to end on a scratch server restored from the real backup, with two synthetic tenants. Runbook, verification query and the exact application contract are in §3 |

Two defects were found *during* this work that no report had recorded. Both are in §2.

---

## 1. CloudNativePG mode

### The decision

The bundled StatefulSet stays the default. It is one pod, one PVC, no operator, and it is what
makes `./deploy/deploy.sh --dev` work on a laptop. CNPG is an *option* for the deployment that has
already decided to run a database operator, and the chart's job in that mode is to get out of the
way completely rather than to half-manage someone else's database.

### Switching

```yaml
rediensiam:
  postgres:
    local:
      enabled: false
    external:
      podSelector:
        cnpg.io/cluster: rediensiam-db     # CNPG stamps this on every instance pod
      namespace: ""                        # empty = this release's namespace
```

plus three DSNs in the (gitignored) secrets file pointing at the cluster's read/write service,
`<cluster>-rw`:

```yaml
rediensiam:
  secrets:
    databaseUrl: "Host=rediensiam-db-rw;Database=rediensiam;Username=iam_app;Password=…;SSL Mode=Require;Trust Server Certificate=true"
hydra:
  hydra:
    config:
      dsn: "postgres://iam_hydra:…@rediensiam-db-rw:5432/hydra?sslmode=require"
keto:
  keto:
    config:
      dsn: "postgres://iam_keto:…@rediensiam-db-rw:5432/keto?sslmode=require"
```

### What the chart stops rendering, verified

```
$ helm template rediensiam . -f values.yaml -f values.dev.yaml --set rediensiam.postgres.local.enabled=false \
    | grep -cE "kind: CronJob|kind: StatefulSet|postgres-lockdown"
0
```

Gone: the Postgres `StatefulSet`, its headless `Service`, the `postgres-init` ConfigMap, the
`postgres-tls` `Certificate`, `rediensiam-postgres-lockdown`, and the backup `CronJob` **and its
PVC**.

Retargeted — the app, Hydra and Keto egress rules for `:5432`, all three via one helper
(`rediensiam.postgresPeer` in `_helpers.tpl`) so they cannot drift apart:

```yaml
  egress:
    - ports: [{port: 5432}]
      to:
        - podSelector:
            matchLabels:
              cnpg.io/cluster: rediensiam-db
```

and with `postgres.external.namespace: databases`:

```yaml
        - podSelector:
            matchLabels:
              cnpg.io/cluster: rediensiam-db
          namespaceSelector:
            matchLabels:
              kubernetes.io/metadata.name: databases
```

### Why the backup CronJob stands down rather than co-existing

This was a deliberate choice, not an omission. CNPG does continuous WAL archiving plus base
backups to object storage. That is off-node, and its recovery point is measured in seconds. The
chart's `pg_dumpall` is a nightly logical dump onto a local-path PVC **on the same node as the
data** — `backup.yaml`'s own header says so, and 15c said so again.

Running both would produce two backups with different recovery points and no statement about which
is authoritative, which is worse than one good backup. It would also need a `CONNECT` grant for
`iam_backup` on someone else's cluster and a NetworkPolicy hole to reach it.

So `backup.yaml` is now gated on `and .Values.rediensiam.backup.enabled
.Values.rediensiam.postgres.local.enabled`.

**The cost, stated plainly:** `backup.enabled: true` becomes a no-op in CNPG mode, and the chart
does not check that you actually configured `.spec.backup` on the Cluster. An operator who turns
CNPG on and forgets the ObjectStore has no backup at all and nothing in this chart will say so.
`verify-deployment.sh` V-22 will still fail (no CronJob), which is the only backstop.

### What the operator must provide

**cert-manager is not required in this mode** — CNPG issues its own server certificates. Everything
below is the operator's, not the chart's.

1. **The CNPG operator itself.** Not installed by this chart, and this step did not install it.

2. **A `Cluster` that reproduces the T-04 role split.** The built-in StatefulSet's `init.sh`
   creates four non-superuser roles, three databases with the right owners, and revokes `CONNECT`
   from `PUBLIC`. None of that happens for you under CNPG, and skipping it silently reinstates
   C-4 — one shared credential across the app, Hydra and Keto.

   Ordering matters, and it is not obvious. CNPG runs `postInitSQL` against `postgres` **before**
   the application database exists, and reconciles `managed.roles` **after** the cluster is up. So
   the roles have to be created (without passwords) in `postInitSQL`, and the passwords assigned
   by `managed.roles` from Secrets — which is also what keeps credentials out of a manifest that
   ends up in git.

   ```yaml
   apiVersion: postgresql.cnpg.io/v1
   kind: Cluster
   metadata:
     name: rediensiam-db
   spec:
     instances: 3
     storage: { size: 20Gi }

     bootstrap:
       initdb:
         database: rediensiam          # owned by the app role, as init.sh does
         owner: iam_app
         secret: { name: rediensiam-db-app }
         dataChecksums: true
         postInitSQL:
           # A LOGIN role with no password cannot authenticate under scram-sha-256, so
           # these are inert until managed.roles below assigns one.
           - CREATE ROLE iam_hydra  LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS
           - CREATE ROLE iam_keto   LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS
           - CREATE ROLE iam_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS
           - GRANT pg_read_all_data TO iam_backup
           - CREATE DATABASE hydra OWNER iam_hydra
           - CREATE DATABASE keto  OWNER iam_keto
           # Without these three REVOKEs the split is cosmetic: PUBLIC holds CONNECT on
           # every new database, so a leaked Keto DSN opens Hydra's consent records.
           - REVOKE CONNECT ON DATABASE hydra FROM PUBLIC
           - REVOKE CONNECT ON DATABASE keto  FROM PUBLIC
           - GRANT  CONNECT ON DATABASE hydra TO iam_hydra, iam_backup
           - GRANT  CONNECT ON DATABASE keto  TO iam_keto,  iam_backup

     managed:
       roles:
         - name: iam_hydra
           ensure: present
           login: true
           superuser: false
           passwordSecret: { name: rediensiam-db-hydra }
         - name: iam_keto
           ensure: present
           login: true
           superuser: false
           passwordSecret: { name: rediensiam-db-keto }
         - name: iam_backup
           ensure: present
           login: true
           superuser: false
           passwordSecret: { name: rediensiam-db-backup }

     # Continuous backup. This is the reason the chart's CronJob stands down; if you omit
     # it, nothing is backing this database up and nothing will tell you.
     plugins:
       - name: barman-cloud.cloudnative-pg.io
         isWALArchiver: true
         parameters:
           barmanObjectName: rediensiam-backup-store
   ```

   plus a `barmancloud.cnpg.io/v1 ObjectStore` named `rediensiam-backup-store` and a
   `ScheduledBackup` with `method: plugin`. (`spec.backup.barmanObjectStore` is the older in-tree
   form and is being retired in favour of the plugin; either works today.)

   `REVOKE CONNECT ON DATABASE rediensiam FROM PUBLIC` is deliberately absent above — CNPG's
   `bootstrap.initdb` creates that database itself, after `postInitSQL` has run. Do it in
   `postInitApplicationSQL`, which runs against the application database once it exists.

   **This manifest has NOT been run.** CNPG was not installed on this cluster, per the task. It is
   derived from the CNPG 1.27 documentation and from `pkg/management/postgres/initdb.go`'s
   `ConfigureNewInstance`, which is what fixes the ordering above. Treat it as a starting point to
   verify, not as a tested artefact. The thing to check first is that `iam_hydra` and `iam_keto`
   exist by the time `CREATE DATABASE … OWNER` runs.

3. **A NetworkPolicy on the CNPG side.** The chart's `postgres-lockdown` — the rule that says only
   the app, Hydra, Keto and the backup may reach `:5432` — does not render in this mode, because
   this chart does not own those pods. If nothing replaces it, the database accepts connections
   from every pod that can route to it. This is the single easiest thing to lose in the switch.

4. **`ALTER ROLE iam_backup BYPASSRLS`** if you also enable §3. Under CNPG, superuser access is
   disabled by default (`enableSuperuserAccess: false`), so this needs to be turned on for the one
   statement and turned back off.

### What `verify-deployment.sh` can no longer assert in this mode

V-20, V-21 and V-23 read `pg_hba.conf` and the StatefulSet by `kubectl exec` into
`rediensiam-postgres-0`. With CNPG that pod does not exist and all three **skip**. They do not
fail, and they do not silently pass. Skipping is honest but it is a real reduction in coverage:
the `trust`/superuser/TLS assertions become unverified rather than verified-false. Adapting them to
CNPG's pod naming is ~1 h and is not done.

---

## 2. R-15 — transport encryption

### cert-manager was the whole blocker, and it is gone

15c left both TLS paths gated because a `cert-manager.io/v1 Certificate` would have been rejected
by an API server that had never heard of the kind. Installed:

```
$ helm install cert-manager jetstack/cert-manager -n cert-manager --create-namespace \
    --version v1.21.1 --set crds.enabled=true --wait

$ kubectl get pods -n cert-manager
cert-manager-75c55f7677-z6ff9              1/1   Running
cert-manager-cainjector-55469cdb88-zw9hg   1/1   Running
cert-manager-webhook-746595f985-xk8bs      1/1   Running

$ kubectl get crd | grep cert-manager
certificaterequests.cert-manager.io   certificates.cert-manager.io
challenges.acme.cert-manager.io       clusterissuers.cert-manager.io
issuers.cert-manager.io               orders.acme.cert-manager.io
```

Three pods and a CRD set to own forever, as costed. It lives in its own namespace, so the
namespace-wide default-deny in `default` does not touch it.

### Postgres — done, in the order 15c prescribed

**Step 1, server side.** `postgres.local.tls.enabled=true`. Safe on its own: `ssl=on` makes TLS
*available*, it does not make it *required*.

```
$ kubectl get certificate -n default
rediensiam-postgres-tls   True   rediensiam-postgres-tls

$ psql … -tAc "SHOW ssl"
on
```

An immediate and slightly awkward observation: the app was *already* on TLS at this point, before
any DSN changed —

```
 usename | datname    | client_addr | ssl  | ver     | cipher
---------+------------+-------------+------+---------+------------------------
 iam_app | rediensiam | 10.42.0.217 | true | TLSv1.3 | TLS_AES_256_GCM_SHA384
```

because Npgsql 10 defaults to `SSL Mode=Prefer`. That is **not** the control. `Prefer` silently
falls back to cleartext the moment the server stops offering TLS, which is precisely the failure
mode an attacker arranges. It is worth recording because it is exactly the kind of observation that
gets mistaken for a finished job.

**Step 2, DSNs, one component at a time, verifying each before the next.** Npgsql spells it
`SSL Mode=Require`; libpq spells it `sslmode=require`. `Trust Server Certificate=true` is required
because the issuer is `selfsigned`.

| Order | Component | Redeployed | Result |
|---|---|---|---|
| 1 | app (`ConnectionStrings__Default`) | rev 6 | `/health`, `/.well-known/openid-configuration`, `/login` all 200 |
| 2 | Hydra (`hydra.hydra.config.dsn`) | rev 7 | `Successfully applied migrations!`, discovery 200 |
| 3 | Keto (`keto.keto.config.dsn`) | rev 8 | pod Ready |

**Step 3, close the cleartext door.** 15c called this optional. It is what turns "our clients are
configured correctly" into a control that survives the next service someone points at this
database with `sslmode=disable`.

```
$ kubectl exec … cp pg_hba.conf pg_hba.conf.pre-r15          # rollback point
$ kubectl exec … sed -i -E 's/^host([[:space:]])/hostssl\1/' pg_hba.conf
$ kubectl exec … psql -tAc "SELECT pg_reload_conf()"          # no restart, no downtime
t
```

`local` (unix socket) lines are deliberately untouched — that path never leaves the pod's network
namespace, and `pg_isready` uses it.

**The proof, which is the point of the whole exercise:**

```
$ PGSSLMODE=disable psql -U iam -h 127.0.0.1 -d postgres -tAc "select 'cleartext got in'"
psql: error: FATAL:  no pg_hba.conf entry for host "127.0.0.1", user "iam", database "postgres", no encryption

$ PGSSLMODE=require psql -U iam -h 127.0.0.1 -d postgres -tAc "select 'tls ok'"
tls ok
```

Then all three components were **restarted** — so these are new connections made after the door
closed, not survivors of the reload:

```
 usename   | datname    | ssl  | ver     | cipher
-----------+------------+------+---------+------------------------
 iam_app   | rediensiam | true | TLSv1.3 | TLS_AES_256_GCM_SHA384
 iam_hydra | hydra      | true | TLSv1.3 | TLS_AES_256_GCM_SHA384
 iam_keto  | keto       | true | TLSv1.3 | TLS_AES_256_GCM_SHA384
```

And the backup — the client most likely to be forgotten — still works under `hostssl`:

```
$ kubectl create job --from=cronjob/rediensiam-backup rediensiam-backup-r15
rediensiam-postgres:5432 - accepting connections
wrote /backup/rediensiam-20260731T132743Z.sql.gz (21235 bytes)
Complete 1/1
```

**Ceiling, unchanged:** the issuer is `selfsigned`, so `require` + `Trust Server Certificate=true`
— encryption, no server authentication — is the honest maximum. `verify-full` needs a real CA whose
root is distributed to the app, Hydra and Keto containers. Still ~4 h, still not done.

#### Two ordering traps, and the guards that now catch them

`requireSsl` and the DSNs must move together, and pg_hba.conf lives on the PVC where a chart
upgrade cannot reach it. Both failure modes are total outages discovered at connection time. They
are now `helm template` errors instead:

```
$ helm template … --set rediensiam.postgres.local.tls.requireSsl=true
Error: rediensiam.postgres.local.tls.requireSsl needs tls.enabled — hostssl on a server with
       ssl=off refuses every client

$ helm template … --set-string keto.keto.config.dsn='postgres://…?sslmode=disable'
Error: rediensiam.postgres.local.tls.requireSsl is set but keto.keto.config.dsn does not request
       TLS — cut every DSN over to sslmode=require FIRST
```

The guard judges only DSNs that actually name PostgreSQL. An empty one means "comes from the
gitignored secrets file", which the repository's own `helm lint values.yaml + values.<env>.yaml`
gate never sees; and the Keto subchart ships `dsn: memory` as its default, which has no transport
to encrypt. Both were caught by running the gate — the first version of this guard failed it.

`deploy.sh` closes the loop from the other side: it greps the env values file for
`requireSsl: true` and writes matching DSNs, so a fresh install cannot produce the combination the
guard rejects. `requireSsl` is grepped rather than `tls: enabled:` because there are three separate
`tls:` blocks in `values.yaml` and matching the wrong one is how this class of check becomes a lie.

#### The state is in the values files, not in `--set`

`postgres.local.tls.{enabled,requireSsl}: true` are in **`values.dev.yaml`** (live) and
**`values.prod.yaml`** (not yet run). Chart defaults stay `false`, because an install without
cert-manager cannot render the Certificate at all.

This matters more than it looks. Carrying the live state only in `helm --set` is exactly the
"deployed ≠ repository" drift step 12 exists to catch. Verified by redeploying with **no** `--set`
for TLS at all:

```
$ kubectl get statefulset rediensiam-postgres -o jsonpath='{…args}'
["-c","ssl=on","-c","ssl_cert_file=/etc/postgres-tls/tls.crt","-c","ssl_key_file=/etc/postgres-tls/tls.key"]
```

For fresh installs, `init.sh` now performs the same `host` → `hostssl` rewrite at initdb time when
`requireSsl` is set, so a rebuild reproduces this cluster rather than a weaker one.

**Turning `requireSsl` back off on this database is an outage, not a rollback.** `pg_hba.conf` is
on the PVC and still says `hostssl`; the chart flag only affects initdb. Roll back with
`cp pg_hba.conf.pre-r15 pg_hba.conf && psql -c "SELECT pg_reload_conf()"`.

### Dragonfly — stopped, and exactly where

Attempted twice on the live cluster as a single atomic change (`dragonfly.local.tls.enabled` and
`cacheUrl` in the same `helm upgrade`), because 15c is right that it cannot be staged.

**Attempt 1 failed on a defect nobody had recorded.** Dragonfly would not start at all:

```
E20260731 13:31:42.567485  server_family.cc:292] TLS configured but no authentication method is used!
```

The cause is worse than the symptom:

```
$ kubectl get secret rediensiam-secrets -o jsonpath='{.data.dragonfly-password}' | base64 -d | wc -c
0
```

`rediensiam.dragonfly.local.password` defaulted to `""` in `values.yaml` and **`deploy.sh` never
generated one**, so the chart has always rendered `--requirepass=` and the cache has always run
with no authentication. It holds the ASP.NET DataProtection key ring
(`PersistKeysToStackExchangeRedis`, `Program.cs:63`), so an unauthenticated writer there can mint
session cookies. The only thing standing in front of it was `dragonfly-lockdown` — one NetworkPolicy
between an unauthenticated key store and every pod in the namespace.

Fixed, and it is a strict improvement independent of TLS:

| File | Change |
|---|---|
| `deploy/deploy.sh` | generates `dfly=$(openssl rand -hex 24)`, writes `dragonfly.local.password` and appends `,password=…` to `cacheUrl` |
| `deploy/deploy.sh` | reuse-path guard: an existing secrets file with no cache password prints a framed R-15 notice, `exit 1` on `--prod` |
| `templates/dragonfly.yaml` | `fail` if `tls.enabled` and the password is empty — a template error instead of a CrashLoopBackOff that takes the cache with it |
| `verify-deployment.sh` | **V-24** asserts the cache password is ≥16 chars |

Applied live (48-char password, cache reachable, `V-24 PASS`).

**Attempt 2 reached the real blocker.** Dragonfly started and served TLS. The client did not:

```
StackExchange.Redis.RedisConnectionException: AuthenticationFailure on rediensiam-dragonfly:6379
  ---> System.Security.Authentication.AuthenticationException:
       The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot
     at System.Net.Security.SslStream.SendAuthResetSignal(…)
```

and from Dragonfly's side, the handshake being aborted:

```
W dragonfly_connection.cc:705] Error handshaking error:1408F10B:SSL routines:ssl3_get_record:wrong version number
```

**This is an application blocker, not an infrastructure one, and no chart change can fix it.**
`src/Program.cs:53` is `await ConnectionMultiplexer.ConnectAsync(appConfig.CacheConnectionString)`
— a bare connection string. StackExchange.Redis validates the server certificate against the OS
trust store, and there is **no connection-string keyword** that disables or redirects that. A
cert-manager `selfsigned` leaf is untrusted by construction.

Rolled back in one `helm upgrade`; cache healthy, cache password kept, orphaned
`rediensiam-dragonfly-tls` Certificate and Secret deleted.

**One thing the attempt taught that 15c had not:** the cutover is atomic across the *rollout*, not
just across the two settings. A Deployment keeps the old app pod running until the new one is
Ready — but the Dragonfly pod flips immediately, so the **old, still-serving** pod loses its cache
too. There is no version of this that is invisible to users.

**What would unblock it** (application change — out of scope here, see §4):

```csharp
var opts = ConfigurationOptions.Parse(appConfig.CacheConnectionString);
opts.CertificateValidation += (_, cert, chain, errors) => /* pin the mounted CA or thumbprint */;
var cacheMultiplexer = await ConnectionMultiplexer.ConnectAsync(opts);
```

plus mounting the issuing CA into the app pod. Roughly 1 h of application work and 0.5 h of chart
work. Until then `dragonfly.local.tls.enabled` stays off and the chart `fail`s loudly rather than
crash-looping if anyone sets it without a password.

**What is actually mitigating the cache today:** `dragonfly-lockdown` (ingress on `:6379` from the
app pod only) and now a 48-character password. The traffic is still cleartext on a pod-to-pod link
inside one node.

---

## 3. S-5 phase 2 — row-level security

### State, stated plainly first

**RLS is implemented, rehearsed, and NOT enabled on this cluster.** `pg_policies` still has zero
rows. `postgres.rls.enabled` defaults to `false` and `verify-deployment.sh` V-25 reports it as an
open finding rather than passing:

```
--    V-25      postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)
```

This is not caution for its own sake. The policies are **fail-closed by design**: a connection
that has not set `rediensiam.org_id` sees zero rows in every tenant table. For an IdP that is a
total outage, not a degraded mode. The application does not set that variable today and cannot,
because the EF half is being written by another agent in `src/`. Enabling this now would take the
IdP down inside one `helm upgrade`.

The alternative — treat "unset" as "unscoped" — was considered and rejected. It produces a control
that is off whenever anyone forgets, which is the exact failure S-5 exists to fix.

### What was built

| File | Role |
|---|---|
| `deploy/rediensiam/files/rls.sql` | the policies. Idempotent, re-runnable, self-verifying |
| `deploy/rediensiam/templates/rls.yaml` | ConfigMap + post-install/post-upgrade hook Job that applies it as the table owner |
| `values.yaml` `postgres.rls.{enabled,database}` | the flag, default `false` |
| `network-policies.yaml` | `postgres-lockdown` admits `app: <release>-rls` on `:5432` |
| `verify-deployment.sh` V-25 | fails if the flag is on and the hook Job has not succeeded |

**Why a hook Job and not `init.sh`:** `init.sh` runs at initdb, before EF has created a single
table. Policies must attach *after* the migrations, on *every* upgrade — which is also what lets
the coverage gate (below) catch a migration that adds an unprotected table.

**Why it runs as `iam_app` and never as the superuser:** `CREATE POLICY` and
`FORCE ROW LEVEL SECURITY` are owner privileges, and `iam_app` already owns all 21 tables.

**`FORCE`, not just `ENABLE`.** This is the subtlety that would have made the whole thing
decorative. A table owner is exempt from its own RLS unless the table is `FORCE`d — and `iam_app`
*is* the owner. `ALTER TABLE … ENABLE ROW LEVEL SECURITY` alone would have produced 19 policies
that have no effect on the only role that connects: a control that verifies as present and does
nothing.

### The session contract — what the application MUST do

This is the part that turns RLS from a control into an outage if it is wrong.

```
SET rediensiam.org_id = '<org uuid>'   → that organisation only
SET rediensiam.org_id = 'system'       → unscoped
unset, empty, or malformed             → nothing is visible, in every table
```

1. **`SET`, not `SET LOCAL`.** Most EF Core reads run outside an explicit transaction, where
   `SET LOCAL` silently does nothing — it does not error, it just has no effect, which is the worst
   possible behaviour for a security control. Session scope is correct here.

2. **Set it on connection open, not on `DbContext` construction.** An EF Core
   `DbConnectionInterceptor` overriding `ConnectionOpenedAsync`, reading the scoped tenant context
   that `GatewayAuthMiddleware` populates. `NpgsqlConnection.Open()` is called on every rent from
   the pool, so this fires for every request.

3. **Do NOT set `No Reset On Close=true` in the DSN.** Npgsql issues `DISCARD ALL` when a
   connection returns to the pool, which is what clears the setting. Disable that and one tenant's
   scope leaks into whichever request rents the connection next — a cross-tenant read caused by a
   performance flag.

4. **`'system'` is required for more paths than it first appears**, and this is the honest limit of
   the control. Login looks a user up by e-mail *before* any tenant is known. Also: bootstrap
   (`Program.cs:280-297`), the audit retention sweep (`AuditLogRetentionService.cs:35-54`),
   SuperAdmin listings (`SystemAdminController`), and EF migrations. In practice a substantial
   share of requests will run unscoped, so RLS protects *tenant-scoped API traffic* — it does not
   make the login path tenant-safe. Anyone reporting S-5 as closed should say that out loud.

5. **One operator action, once, as superuser:**
   ```sql
   ALTER ROLE iam_backup BYPASSRLS;
   ```
   Not optional. Proven below.

### Coverage

19 of 21 tables. `Instances` and `__EFMigrationsHistory` are deployment-global and listed
explicitly as such.

- Direct `OrgId`: `organisations` (on `Id`), `user_lists`, `org_roles`, `org_smtp_configs`,
  `projects`, `webhooks`, `service_account_roles`, `audit_log`
- Via `user_lists`: `users`, `service_accounts`
- Via `users`: `backup_codes`, `email_tokens`, `user_social_accounts`, `webauthn_credentials`
- Via `projects`: `roles`, `saml_idp_configs`, `user_project_roles`
- Via `service_accounts` / `webhooks`: `personal_access_tokens`, `webhook_deliveries`

Each policy is `FOR ALL … USING (…) WITH CHECK (…)`. The `WITH CHECK` half is not decoration: without
it a scoped connection can still `INSERT` a row into another tenant, it just cannot read it back.

**`user_lists.OrgId IS NULL` is the `__system__` list.** `NULL = rls_org()` is `NULL`, so it is
invisible to every tenant and reachable only when unscoped. That is exactly the bug class the
codebase documents in a comment at `ServiceAccountController.cs:29-33` — a `Guid.Empty`/`null`
conflation that granted access to the deployment's most privileged service accounts — closed in the
schema instead of by convention.

**The coverage gate.** `rls.sql` aborts if it finds a table in `public` that is neither policied
nor listed as global:

```
ERROR:  public.some_future_ef_table has no RLS policy and is not listed as deployment-global
        — add it to rls.sql or to global_tables
```

An EF migration that adds a tenant table therefore fails the deploy instead of shipping something
RLS does not cover and nothing reports on. This is the auditable-artefact property S-5 asked for.

### Rehearsal — real schema, synthetic tenants, scratch server

The live database has **zero organisations** (one bootstrap superadmin in the `__system__` list), so
a live rehearsal would have proved nothing. Instead: the actual nightly backup, restored into a
throwaway container, seeded with two tenants.

```
$ # dump pulled from the backup PVC, restored into docker postgres:16-alpine
  restore exit=0  ERROR lines: 1
  ERROR:  permission denied to grant privileges as role "iam"     # the known, documented one (T-03)
  21 tables

$ psql -U iam_app -f rls.sql
NOTICE:  RLS applied to 19 tables
```

Structural verification, all 19 rows `t / t / 1`:

```
       table_name       | rls_enabled | rls_forced | policies
------------------------+-------------+------------+----------
 audit_log              | t           | t          |        1
 backup_codes           | t           | t          |        1
 …
 webhooks               | t           | t          |        1
(19 rows)
```

Functional:

```
(1) unset — fail-closed
    orgs=0  users=0  projects=0  roles=0  lists=0

(2) SET rediensiam.org_id = '<Org A>'
    orgs=1  users=1  projects=1  roles=1  lists=1
    organisations → Org A / org-a          users → alice@a.test

(3) SET rediensiam.org_id = '<Org B>'
    organisations → org-b                  users → bob@b.test

(4) SET rediensiam.org_id = 'not-a-uuid'                    → 0 rows, no error
    SET rediensiam.org_id = '00000000-…-000000000000'       → 0 rows

(5) SET rediensiam.org_id = 'system'
    orgs=2  users=3  lists=3  system_lists=1     ← the __system__ list, invisible to (2) and (3)
```

Write side, scoped to Org A:

```
(6) INSERT INTO projects (… "OrgId" = <Org B> …)
    ERROR:  new row violates row-level security policy for table "projects"
(7) UPDATE projects SET "Name"='pwned' WHERE "OrgId" = <Org B>     → UPDATE 0
(8) DELETE FROM users WHERE "Email"='bob@b.test'                   → DELETE 0
(9) Org B intact: projects → Proj B,  users → bob@b.test
```

**Cost.** The scope accessors are plain SQL, so the planner inlines them and evaluates the nested
sub-select once rather than per row:

```
Seq Scan on users
  Filter: ("Active" AND ((current_setting('rediensiam.org_id',true) = 'system') OR (hashed SubPlan 2)))
  SubPlan 2 -> Seq Scan on user_lists ul   (uses IX_user_lists_OrgId at scale)
```

A `plpgsql` function with an `EXCEPTION` block would have opened a subtransaction per row; that is
why `rls_org()` guards the UUID cast with a regex and returns `NULL` instead of raising.

### The backup interaction — the thing that would have broken silently

`pg_dump` sets `row_security = off`, which **errors** for a role that cannot bypass RLS. Proven on
the rehearsal server:

```
$ pg_dumpall -U iam_backup --clean --if-exists --no-role-passwords
pg_dump: error: query failed: ERROR:  query would be affected by row-level security policy for table "audit_log"
pg_dumpall: error: pg_dump failed on database "rediensiam", exiting
exit=1

$ psql -U postgres -c "ALTER ROLE iam_backup BYPASSRLS"
$ pg_dumpall -U iam_backup --clean --if-exists --no-role-passwords
exit=0   bytes=133661   (both tenants' rows present)
```

It fails loudly rather than writing a partial dump — `backup.yaml`'s `set -o pipefail` propagates
the non-zero exit and the CronJob fails. But **enabling RLS without that one `ALTER ROLE` stops the
nightly backup**, and T-03 is the finding about exactly that kind of control looking present and
working never.

### The hook Job's plumbing, validated live

The Job parses the app's Npgsql DSN in shell, which is non-trivial and worth proving separately
from the SQL. It was run against the **live** database with `rls.sql` swapped for a read-only probe
— no policy was created:

```
applying RLS to rediensiam on rediensiam-postgres:5432 as iam_app (sslmode=require)
rediensiam-postgres:5432 - accepting connections
 connected_as |     db     | tls | policies_now
--------------+------------+-----+--------------
 iam_app      | rediensiam | t   |            0
```

DSN parsed (host, port, user, database, sslmode), NetworkPolicy admitted it, connected as the table
owner over TLS, and confirmed the live policy count is zero. Job and ConfigMap deleted afterwards.

### Enablement runbook

Do **not** run this until the application half is deployed and `SET rediensiam.org_id` is issued on
every connection.

```bash
# 0. Precondition, checked not assumed. Deploy the app build that sets the session
#    variable, then confirm it actually does — from the app pod, under load:
#      SELECT setting FROM pg_settings WHERE name = 'rediensiam.org_id';
#    An empty result on a connection the app is using means STOP.

# 1. The one superuser action. Without it the nightly backup stops working.
kubectl exec -n default rediensiam-postgres-0 -- \
  env PGPASSWORD="$PW_SUPER" psql -U iam -h 127.0.0.1 -d postgres \
  -c "ALTER ROLE iam_backup BYPASSRLS"

# 2. Take a dump first. The policies are reversible; a mistake made under them may not be.
kubectl create job --from=cronjob/rediensiam-backup rls-pre-enable -n default

# 3. Enable. The hook Job applies files/rls.sql after the app is Ready.
#      rediensiam.postgres.rls.enabled: true      → redeploy
kubectl logs -n default job/rediensiam-rls        # must end with "RLS applied to 19 tables"

# 4. Verify (both queries below), then exercise a real tenant request end to end.

# 5. Prove the backup still works BEFORE the next nightly run.
kubectl create job --from=cronjob/rediensiam-backup rls-post-enable -n default
```

**Rollback**, one statement per table, no data touched:

```sql
DO $$ DECLARE r record; BEGIN
  FOR r IN SELECT DISTINCT c.relname FROM pg_policy p
           JOIN pg_class c ON c.oid = p.polrelid WHERE p.polname = 'rediensiam_tenant'
  LOOP
    EXECUTE format('ALTER TABLE public.%I NO FORCE ROW LEVEL SECURITY', r.relname);
    EXECUTE format('ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', r.relname);
  END LOOP;
END $$;
```

Set `postgres.rls.enabled: false` in the same change, or the next upgrade re-applies the policies.

### Verification queries

**Structural** — every row must read `t / t / 1`. Any `f` is an unprotected tenant table, including
one a future EF migration added. (Also the tail of `rls.sql` itself, so the hook Job prints it.)

```sql
SELECT c.relname AS table_name, c.relrowsecurity AS rls_enabled,
       c.relforcerowsecurity AS rls_forced, count(p.polname) AS policies
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
LEFT JOIN pg_policy p ON p.polrelid = c.oid
WHERE n.nspname = 'public' AND c.relkind = 'r'
  AND c.relname NOT IN ('Instances', '__EFMigrationsHistory')
GROUP BY 1, 2, 3
ORDER BY rls_enabled, rls_forced, 1;
```

**Functional** — the one that actually proves isolation. Run as `iam_app`, substituting two real
org UUIDs. `a_only` and `b_only` must each be 1, `fail_closed` must be 0.

```sql
SET rediensiam.org_id = '<org A uuid>';
SELECT count(*) AS a_only FROM organisations;
SET rediensiam.org_id = '<org B uuid>';
SELECT count(*) AS b_only FROM organisations;
RESET rediensiam.org_id;
SELECT count(*) AS fail_closed FROM organisations;
SET rediensiam.org_id = 'system';
SELECT count(*) AS all_orgs FROM organisations;
```

---

## 4. Required application changes (not made here — `src/` is another agent's scope)

| # | Change | Blocks | Effort |
|---|---|---|---|
| A-1 | `DbConnectionInterceptor` issuing `SET rediensiam.org_id = '<uuid>' \| 'system'` in `ConnectionOpenedAsync`, from the scoped tenant context | **all of §3** | ~30 lines |
| A-2 | Never set `No Reset On Close=true` on the Npgsql DSN | cross-tenant leak via pooled connections | 0, a prohibition |
| A-3 | `ConfigurationOptions.Parse` + `CertificateValidation` callback at `Program.cs:53`, instead of `ConnectAsync(string)` | **Dragonfly TLS**, §2 | ~1 h + 0.5 h chart |
| A-4 | Audit every `IgnoreQueryFilters()` against the `'system'` list in §3 item 4 — the two must name the same paths | RLS correctness | review |

A-1 and A-4 are the same piece of work as the EF global query filters. If the EF filters ship
without A-1, RLS must stay off; the two are one change, not two.

---

## 5. Gate output

### `helm lint`

```
########## helm lint values.yaml + values.dev.yaml ##########
==> Linting .
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed

########## helm lint values.yaml + values.prod.yaml ##########
==> Linting .
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed
```

### `helm template`

```
########## helm template values.yaml + values.dev.yaml ##########
exit=0  45 documents, 1839 lines
(no stderr)
YAML parses: 45 objects

########## helm template values.yaml + values.prod.yaml ##########
exit=0  47 documents, 1886 lines
(no stderr)
YAML parses: 47 objects
```

(was 43 / 46 at step 15c; +2 each is the Postgres TLS `Certificate` and the `selfsigned`
ClusterIssuer now that TLS is on in both env files.)

### `./deploy/verify-deployment.sh --dev`

```
═══════════════════════════════════════════════════════════════
 RediensIAM control verification — dev — 2026-07-31T15:49:53+02:00
 namespace default · release rediensiam · public host iam.localhost
═══════════════════════════════════════════════════════════════
  PASS  V-01      registry bound to 127.0.0.1 (loopback only)
  PASS  V-02      no rediensiam ClusterRole grants access to Secrets
  PASS  V-03      hydra-maester is not deployed
  PASS  V-04/admin/ public host refuses /admin/ (403)
  PASS  V-04/org  public host refuses /org (403)
  PASS  V-04/project public host refuses /project (403)
  PASS  V-04/service-accounts public host refuses /service-accounts (403)
  --    V-05      dev is deliberately cleartext (iam.localhost cannot be certified)
  PASS  V-06      Hydra :4444 discovery answers 200
  PASS  V-07      image pinned by digest (sha256:2c32df17539a20db636b9736b8ebc420e7e29a8015589697944b68cdaad8269d)
  PASS  V-08      imagePullPolicy=IfNotPresent
  PASS  V-09      pod seccompProfile=RuntimeDefault
  PASS  V-10      container runAsNonRoot=true
  PASS  V-11      container allowPrivilegeEscalation=false
  PASS  V-12      container readOnlyRootFilesystem=true
  PASS  V-13      container drops ALL capabilities
  PASS  V-14      automountServiceAccountToken=false
  PASS  V-15      rediensiam-default-deny-ingress exists
  PASS  V-15/hydra rediensiam-hydra-lockdown exists
  PASS  V-15/keto rediensiam-keto-lockdown exists
  PASS  V-15/postgres rediensiam-postgres-lockdown exists
  PASS  V-15/dragonfly rediensiam-dragonfly-lockdown exists
  PASS  V-16      admin service is NodePort (dev, expected)
  PASS  V-17      CSP carries script-src, base-uri, form-action, frame-ancestors, object-src
  PASS  V-18      CSP names no external font host
  --    V-19      values.yaml pins no image digest — cannot check for drift
  PASS  V-20      pg_hba.conf grants no 'trust' (all methods are scram-sha-256)
  PASS  V-21      no component connects as superuser 'iam' (users:iam_app iam_hydra iam_keto)
  PASS  V-22      backup CronJob last succeeded 2026-07-31T13:27:47Z
  PASS  V-23/server Postgres runs with ssl=on
  PASS  V-23/hba  pg_hba.conf admits TLS only (hostssl; local socket unaffected)
  PASS  V-23/dsn  app, hydra and keto DSNs all request TLS
  PASS  V-24      cache requires a password (48 chars)
  --    V-25      postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)
───────────────────────────────────────────────────────────────
 31 passed · 0 failed · 3 skipped
 All asserted controls are live.
EXIT=0
```

```
$ kubectl get pods -n default
rediensiam-7fc6978cc9-bfpvs             1/1   Running   0   16m
rediensiam-dragonfly-768d4c89df-4h2cz   1/1   Running   0   11m
rediensiam-hydra-75b7fc79b4-9v4tq       1/1   Running   0   24m
rediensiam-keto-754dc4c55d-792qd        1/1   Running   0   24m
rediensiam-postgres-0                   1/1   Running   0   59m
```

---

## 6. What is left, with its cost

| Item | Why still open | Cost |
|---|---|---|
| **RLS is not enabled** | fail-closed policies with no application half is an outage; `src/` is another agent's scope | app A-1 + A-4, then §3's runbook (~1 h) |
| **Dragonfly TLS** | `ConnectAsync(string)` cannot trust a self-signed cert; no connection-string knob exists | app A-3, ~1 h + 0.5 h chart |
| Postgres `verify-full` | issuer is `selfsigned`; `require` is the honest ceiling | +4 h — a real CA and root distribution to three containers |
| CNPG `Cluster` manifest untested | CNPG deliberately not installed here | ~2 h to stand one up and verify the role/database ordering |
| CNPG mode has no `postgres-lockdown` equivalent | the chart does not own those pods | operator must write one; ~0.5 h |
| V-20/21/23 skip under CNPG | they `kubectl exec` into `rediensiam-postgres-0` | ~1 h to make them CNPG-aware |
| Backup still on the same node as the data | single-node k3s, no second failure domain | unchanged from 15c; CNPG mode fixes it by construction |
| `iam` is still SUPERUSER, guarded only by a k8s Secret | a cluster needs one | unchanged from 15c — S-10/RBAC |
| Hydra system secret still `CHANGE_ME_…`; `bootstrapPassword` still `Admin1234!` | pre-existing R-06 residuals | unchanged; `10-secrets-management.md` §4 |
| Dedicated namespace | no cross-namespace PVC move | unchanged from 15c |
| Registry auth/TLS, admin cert, WAF, IDS | unchanged from `09 §8` | as costed there |

**Prod has still not been deployed from this branch.** `values.prod.yaml` now carries
`postgres.local.tls.{enabled,requireSsl}: true`, which is correct and self-consistent for a *fresh*
install (deploy.sh writes matching DSNs). On an *existing* prod database it is a migration, not an
upgrade — `pg_hba.conf` is written by initdb and a chart upgrade will not rewrite it. Run §2's live
steps before the first prod upgrade that carries this flag.

### Credential note

No secret was written to a tracked file. `deploy/rediensiam/values.secret.yaml` is gitignored
(`.gitignore:25`) and mode 600. Every command in this document that touched a credential read it
out of that file into a shell variable and masked it on output; DSNs are shown here with
`Password=***` / `password=***`. The scratch rehearsal container and the local copy of the backup
dump were destroyed (`docker rm -f`, `shred -u`) after use.
