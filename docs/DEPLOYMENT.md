# Deploying RediensIAM

From a bare cluster to a working identity provider.

The runbooks behind this guide live in `SECURITY-AUDIT-LOG.md`. That is an audit trail, not an
install guide — this file is the install guide, and it points back at them wherever a procedure
is too long or too situational to inline.

Before you deploy this anywhere that matters, read [`SECURITY.md`](SECURITY.md) — in particular
§8, which lists what is deliberately still open.

---

## The scripts

| Script | What it does | Changes anything? |
|---|---|---|
| `deploy/preflight.sh --dev\|--prod` | Asks whether this host and this cluster can run the chart | No, except `--install-cert-manager` |
| `deploy/setup.sh --dev` | Zero to running. Preflight → build → deploy → verify → credentials | Yes |
| `deploy/setup.sh --prod` | Interviews you for the decisions nothing can default, then the same | Yes |
| `deploy/deploy.sh --dev\|--prod` | Just the build and the deploy | Yes |
| `deploy/verify-deployment.sh --dev\|--prod` | Asserts the security controls are live **in the cluster** | No |
| `deploy/reset-dev.sh` | Dev clean slate | Yes — destroys data |

`setup.sh` is the entry point. The others are the stages, runnable on their own.

---

## Requirements

On the machine you deploy **from**:

| Tool | Version | Why |
|---|---|---|
| kubectl | matching the cluster | everything |
| Helm | 3.12+ | the chart |
| Docker | 20+ | builds the app image and hosts the local registry |
| Node.js | 20+ | `deploy.sh` builds the login and admin SPAs before the image |
| openssl, curl | any | credential generation, smoke checks |

In the **cluster**:

| Thing | Why | If missing |
|---|---|---|
| A default StorageClass | Postgres and the backup claim volumes with no class | pods stay `Pending` forever with no error |
| An IngressClass matching `rediensiam.ingress.className` (default `traefik`) | the public and admin ingresses | ingress is created and routes nothing |
| Traefik's `middlewares.traefik.io` CRD | the rate limit, the 1 MiB body cap, and the **P-04 management-API deny** on the public host | `helm upgrade` fails at apply, after the image has been built |
| cert-manager | Postgres server TLS (on in both shipped environments), the admin ingress cert, ACME | the `Certificate` objects are rejected |
| A pod CIDR covered by `rediensiam.app.trustedProxies` | `App__TrustedProxies`; the app **refuses to start** on an empty value | every IP-based control reads the ingress pod's address instead of the client's |

`preflight.sh` checks all of these and names the fix. It does not guess at any of them.

cert-manager is the only one it will install for you:

```bash
./deploy/preflight.sh --dev --install-cert-manager
```

### Before the first install on a new cluster — two minutes

Confirm the CNI actually enforces NetworkPolicy. Every policy in this chart is decorative if it
does not, and Hydra's admin API is then open to the whole cluster
(`SECURITY-AUDIT-LOG.md` step 09 §6.8):

```bash
kubectl run np-test --image=busybox --restart=Never -- sleep 3600
kubectl exec np-test -- wget -qO- --timeout=3 http://rediensiam-hydra-admin:4445/admin/clients
# expected: timeout. If it returns JSON, stop and fix the CNI.
kubectl delete pod np-test
```

---

## Development

```bash
./deploy/setup.sh --dev
```

That is the whole thing. It runs preflight, builds both SPAs and the image, pushes to a
loopback-only registry, deploys the chart pinned to the image **digest**, runs
`verify-deployment.sh`, and prints the bootstrap credentials.

There is nothing to fill in first. In particular **do not invent passwords**: every credential —
the four Postgres role passwords, the cache password, the HKDF root encryption key, the Argon2
pepper, the Hydra system secret, the bootstrap admin password — is generated per machine into
`deploy/rediensiam/values.secret.yaml` (mode 600, gitignored). A dev password an operator has to
invent is a password that ends up in a shell history.

What you get:

```
Login          http://iam.localhost/login
Admin console  http://admin.iam.localhost/console/
OIDC discovery http://iam.localhost/.well-known/openid-configuration
```

The console is on a **subdomain of the issuer's host**, not on `localhost`. That is not cosmetic:
`localhost` and `iam.localhost` are different sites as a browser counts them, so Hydra's
`SameSite=Strict` session cookie was never sent and every reload asked for the password again. The
`:30501` NodePort still exists as a troubleshooting door, but it answers only to a request carrying
`Host: admin.iam.localhost` — a bare `http://localhost:30501` is refused with a 400, which is host
filtering working.

`iam.localhost` needs to resolve to the ingress. On most systems `localhost` subdomains resolve
automatically; if not, add it to `/etc/hosts`.

Dev is deliberately **cleartext on the ingress** — `iam.localhost` cannot be certified by any CA,
and Traefik would fall back to its own default certificate. This is the one place R-02 is not
fixed, and it is gated to dev so prod cannot inherit it. Postgres TLS *is* on in dev
(`values.dev.yaml`), which is why dev needs cert-manager.

### Starting over

```bash
./deploy/reset-dev.sh              # lists what it destroys, then asks
./deploy/reset-dev.sh --dry-run    # list only
./deploy/reset-dev.sh --registry   # also drop the local registry and its image layers
```

It destroys the Helm release, **every PVC belonging to it** — including orphans left by earlier
installs — and the generated credentials. StatefulSet PVCs survive `helm uninstall`, which is how
a cluster ends up with a volume that still holds the *old* database password; this is what clears
them. It refuses to run against a release whose issuer is not a localhost name.

---

## Production

```bash
./deploy/setup.sh --prod --plan     # interview only, writes the answers, deploys nothing
./deploy/setup.sh --prod            # interview (or reuse), then deploy
```

The interview is not a formality. Each question below is one the audit deliberately left to a
human, and the script fails rather than defaulting any of them. Answers are written to
`deploy/rediensiam/values.prod.override.yaml` (gitignored, no secrets in it), which layers after
`values.prod.yaml` and before the secrets file.

### What it asks, and what it refuses

**Hostnames.** The public host serves login, registration and the OIDC endpoints. The admin host
serves the console and the management API. They must differ — the ingress denies `/admin`,
`/org`, `/project` and `/service-accounts` on the public host (P-04), and one hostname for both
means no separation. It rejects `localhost` and `.local` names for the public host.

**TLS for the public host.** `acme` (Let's Encrypt HTTP-01) or `existing` (name a ClusterIssuer
you already run). ACME requires an account email — the chart *fails at template time* without
one — and public DNS for the hostname plus port 80 open to this node. If the name does not
resolve from the deploy host, the script says so and makes you type an acknowledgement. It cannot
create a DNS record for you.

`existing` is verified: if the ClusterIssuer is not in the cluster, the script stops.

**TLS for the admin host.** `existing` or `selfsigned`. `selfsigned` is the chart default and a
known defect: it trains operators to click through a certificate warning on the most privileged
UI in the system. Choosing it requires a typed acknowledgement. The two ways out are an internal
CA ClusterIssuer with its root distributed to operator devices, or ACME **DNS-01** —
`SECURITY-AUDIT-LOG.md` step 09 §6.3. HTTP-01 cannot certify a Tailscale MagicDNS name;
the challenge is fetched from the public internet.

**Database backend.** `builtin` (the chart's PostgreSQL StatefulSet) or `cnpg` (an external
CloudNativePG Cluster you operate).

Choosing `cnpg` is checked: the operator must be installed and the named `Cluster` must already
exist. Two things then become yours, and the script makes you acknowledge both, because nothing
in the chart will warn you later:

- `rediensiam-postgres-lockdown` does not render. The NetworkPolicy that keeps `:5432` reachable
  only from the app, Hydra and Keto is gone and you must write the CNPG-side equivalent.
- The nightly backup CronJob becomes a no-op. If you have not configured `.spec.backup` and an
  `ObjectStore` on the Cluster, **you have no backup at all**.

The `Cluster` manifest that reproduces the four-role split — including the ordering trap that
`postInitSQL` runs before the application database exists — is in
`SECURITY-AUDIT-LOG.md` step 18 §1. It has not been run against a real cluster; treat it
as a starting point to verify.

Note that `verify-deployment.sh` V-20, V-21 and V-23 **skip** under CNPG: they read
`rediensiam-postgres-0` directly. Making them CNPG-aware is about an hour and is not done.

**`PG_HOST` is yours to set under CNPG.** The three connection strings the script generates — the
application's, Hydra's and Keto's — default to `rediensiam-postgres`, the StatefulSet the chart
installs. An external Cluster answers on its read-write service instead, so export the host before
running the script:

```bash
PG_HOST="<cluster-name>-rw.<namespace>.svc" ./deploy/setup.sh --prod
```

Leaving it unset against a CNPG Cluster generates DSNs pointing at a Service that does not exist,
and the failure surfaces as a pod that cannot reach its database rather than as a configuration
error.

**Backups.** With `builtin` you choose the schedule, the volume size and how many dumps to keep —
and then the script asks where the **off-node** copy goes and will not accept a blank answer. The
nightly `pg_dumpall` lands on a PVC on the same node and the same disk as the database it
protects. That covers a bad migration or a dropped table. It does not cover losing the node.
Answering `none` requires a typed acknowledgement. The answer is recorded as a comment in the
override file; the chart does not automate it.

A CronJob is not a backup until a dump has been restored. The restore test —
against a **throwaway** container, never the live server, and with the one non-obvious
`GRANT pg_read_all_data TO iam_backup` that a restore silently loses — is
`SECURITY-AUDIT-LOG.md` step 15c §T-03.

**Row-level security.** The script leaves it **off** and does not ask. The policies are
fail-closed: a connection that has not issued `SET rediensiam.org_id` sees zero rows in every
tenant table, which for an IdP is a total outage. It can only be enabled after an application
build that sets that variable on every pooled connection is deployed *and verified against a live
connection*. On a first install that build is not running yet. See "Turning RLS on" below.

**SMTP.** Optional. Blank means only organisations with their own SMTP server can send password
resets. The SMTP *password* is a secret and goes into the generated secrets file, not the
override.

### After the interview

`setup.sh --prod` runs preflight, asks for the bootstrap admin email and password (the one
credential it does not generate), builds, deploys, and verifies.

Then, four things it cannot do for you:

1. **Verify the CNI enforces NetworkPolicy** (above).
2. **Enable k3s secret encryption at rest.** Needs root on the server node; without it every
   Secret is plaintext in the datastore. Fifteen minutes —
   `SECURITY-AUDIT-LOG.md` step 10 §7.3.
3. **Prove the backup restores** — §T-03 above.
4. **Move `values.prod.secret.yaml` off this machine.** It holds the HKDF root key and the Argon2
   pepper as well as the passwords.

### Installing into its own namespace

The chart's default-deny NetworkPolicy is namespace-scoped, which is an outage for any neighbour
in the namespace that has no policy of its own. `preflight.sh` fails if it finds foreign pods.
A dedicated namespace is the better answer and **must be chosen at install time** — Helm cannot
move a release between namespaces and the PVCs cannot follow it:

```bash
kubectl create namespace rediensiam
NAMESPACE=rediensiam ./deploy/setup.sh --prod
```

The alternative, if you must share a namespace, is
`rediensiam.networkPolicy.defaultDenyScope: release`.

---

## Day 2

### Turning on Postgres TLS on an existing database

Both shipped environments already set `postgres.local.tls.enabled` and `requireSsl`. On a **fresh**
install that is self-consistent and needs nothing. On an **existing** database it is a migration,
not an upgrade: `pg_hba.conf` is written by initdb, lives on the PVC, and a chart upgrade will not
rewrite it. Run the live steps in `SECURITY-AUDIT-LOG.md` step 18 §2 *before* the first
upgrade that carries the flag.

Order, and it is enforced by the chart as a `helm template` error rather than a 3am connection
refusal:

1. `postgres.local.tls.enabled` — the server *offers* TLS. Safe alone; existing clients unaffected.
2. Every DSN gains `sslmode=require` (libpq) / `SSL Mode=Require;Trust Server Certificate=true`
   (Npgsql), one component at a time, verifying each.
3. `requireSsl` — `pg_hba.conf` admits `hostssl` only, so the *server* refuses cleartext.

Reversing any pair is a total outage. Turning `requireSsl` back off on a migrated database is
also an outage, not a rollback — restore `pg_hba.conf.pre-r15` and `pg_reload_conf()` instead.

`Trust Server Certificate=true` is the honest ceiling while the issuer is `selfsigned`:
encryption, no server authentication. `verify-full` needs a real CA whose root reaches all three
containers — about four hours, described in the same section.

### Turning RLS on

**Done in dev**, on the live cluster, with every command and its real output in
`SECURITY-AUDIT-LOG.md` step 29. `values.dev.yaml` carries the flag. Prod does not: on an
already-running database this is a migration, not an upgrade.

Two of the five steps below are now enforced rather than remembered — `init.sh` grants `BYPASSRLS`
at initdb, and `files/rls.sql` refuses to create a single policy on a database where `iam_backup`
cannot bypass. So step 2 can no longer be skipped silently; it can only fail the deploy. Do it
anyway, in the right order, so the deploy does not fail at all.

**On a database created before 2026-08-01, step 2 is not optional.** That grant used to be
conditional on `postgres.rls.enabled` being true *at initdb*, and `init.sh` only ever runs at
initdb — while `setup.sh --prod` forces RLS off on a first install and does not ask. So **no
database `setup.sh --prod` could produce ever had the grant**, and enabling RLS on one could only
ever fail. Observed on the first prod-profile install, SECURITY-AUDIT-LOG.md step 33 §2.6`: the
post-upgrade Job aborting with `iam_backup cannot bypass RLS`. The fail-closed abort did its job —
the deploy stopped instead of the backup — but the trap was unconditional. The grant is
unconditional now; existing databases still need it applied by hand.

1. Verify the app really sets the variable, on a connection it is using. From the database, with
   `log_statement='all'` for one minute:
   `SELECT set_config($1, $2, false)` with `$1 = 'rediensiam.org_id'` must appear for every
   connection the app opens. Nothing there means the deployed image predates
   `TenantScopeInterceptor` — stop.
2. `ALTER ROLE iam_backup BYPASSRLS`, as superuser, **before** enabling. `pg_dump` sets
   `row_security = off`, which *errors* for a role that cannot bypass, so without this the nightly
   backup aborts on the first policied table.
3. Take a dump: `kubectl create job --from=cronjob/rediensiam-backup rls-pre-enable`.
4. `rediensiam.postgres.rls.enabled: true`, redeploy. `kubectl logs job/rediensiam-rls` must end
   with `RLS applied to 19 tables` and a table in which every row reads `t / t / 1`.
5. Prove the backup still runs, **before** the next nightly, and confirm the dump is not silently
   partial — `gzip -dc` it and grep for a row you know is there.

Full runbook, both verification queries and the rollback SQL:
`SECURITY-AUDIT-LOG.md` step 18 §3. What it does *not* protect:
[`SECURITY.md`](SECURITY.md#2-tenant-isolation-and-its-honest-limit).

### Turning cache TLS on in production

production cluster.** It has been observed working under the prod profile on a from-scratch install
in a scratch namespace (`SECURITY-AUDIT-LOG.md` step 33 §3): cleartext refused by the
server, TLS accepted against the mounted CA, the same connection rejected against the OS trust
store, and the app reading and writing through the tunnel. What follows — the *cutover* on a cache
that is already running and already holds a key ring — is still reasoned from the dev cutover, not
observed anywhere.

It is a hard cutover, and the cost is not avoidable by ordering:

   same upgrade. `deploy.sh` writes it from this flag on a fresh install and stops with the exact
   either direction. You cannot split them by accident.
   restart empties the DataProtection key ring. There is no version of this that is invisible.
   ring first:

   ```
   DEL rediensiam:dataprotection:keys
   ```

   The old ring is stored **unprotected**, and `EncryptedOnlyXmlRepository` refuses an unprotected
   key rather than adopting one — a plaintext key in a shared cache can be *planted*, and it mints
   session cookies. Skip this and the session path 500s with the remedy in the exception message.
   anyway. Point 2 has the same effect for a cutover done in one upgrade; this step matters when the
   cache is upgraded separately, or has been given persistence.

Then: `./deploy/verify-deployment.sh --prod` must show `V-26/server`, `V-26/dsn` and `V-26/pin` all
passing. `V-26/pin` reads the running pod's log, so it is the one that cannot be satisfied by a
manifest alone.

### Rotating credentials

| Secret | Runbook | Shape |
|---|---|---|
| HKDF root encryption key | SECURITY-AUDIT-LOG.md step 16 §7.1` | Two keys configured → roll all replicas → sweep → `total_pending == 0` → **only then** drop the old key. Dropping it early is unrecoverable. |
| Argon2 pepper | SECURITY-AUDIT-LOG.md step 16 §7.3` | Prepend. No sweep and no completion signal — accounts re-pepper on next login, so finishing it is a policy decision about dormant users. |
| Hydra system secret | SECURITY-AUDIT-LOG.md step 10 §4.1` | Prepend, never replace. Keep the old entry for at least `refresh_token` TTL (7 days here) before trimming. |
| Database roles | SECURITY-AUDIT-LOG.md step 10 §4.2` | `ALTER ROLE` first — `POSTGRES_PASSWORD` is read only at initdb, so editing values alone rotates nothing. |
| Hydra client secrets | SECURITY-AUDIT-LOG.md step 10 §4.4` | No dual-secret window in OAuth2. Deploy the new secret to the consumer in the same step. |
| PATs | SECURITY-AUDIT-LOG.md step 10 §4.3` | Issue → deploy → confirm traffic → revoke. |

Before rotating anything cryptographic, read `SECURITY-AUDIT-LOG.md` step 16 §8 — it is
the table of which rollbacks are safe. Two of them are not.

Dev has one shortcut, and it is a full state reset rather than a rotation:

```bash
./deploy/reset-dev.sh && ./deploy/setup.sh --dev
```

### Upgrading

```bash
./deploy/setup.sh --dev --upgrade    # also refreshes the Hydra/Keto subcharts
./deploy/setup.sh --prod
```

`verify-deployment.sh` runs at the end of every one. Run it on a schedule too — it is what
catches drift between what the repository claims and what the cluster does.

---

## Known gaps

Carried forward deliberately; each has a runbook and a cost.

| Gap | Where | Cost to close |
|---|---|---|
| Admin console served with a self-signed certificate | `09 §6.3` | internal CA, or ACME DNS-01 |
| Client address flattened to one proxy IP | ingress | `externalTrafficPolicy: Local` — see below |
| Local registry has no authentication and no TLS | `09 §6.2` | 2h; **required** if k3s is not on this host — bind and auth move together, never one without the other |
| ACME / Let's Encrypt has **never been executed** | `33 §4` | The `letsencrypt` ClusterIssuer has never been applied to any cluster; HTTP-01 needs public DNS and port 80 reachable from the internet, and the prod-profile install used a self-signed ClusterIssuer instead. Whether a publicly trusted certificate can be issued here is unknown |
| RLS off **in prod only** | `18 §3` / `29` / `33 §2.6` | Live and verified in dev: 19 tables `ENABLE` + `FORCE`, cross-tenant read/insert/update/delete refused at the database, backup still succeeding. Also turned on once on a from-scratch prod-profile install (V-25, 19 tables). `values.prod.yaml` does not override the `false` default, because on an existing prod database this is a migration, not an upgrade. **A database initdb'd before 2026-08-01 still needs `ALTER ROLE iam_backup BYPASSRLS` applied by hand** — the grant used to be conditional on RLS already being on, which no `setup.sh --prod` install ever was. RLS *does* now cover the tenant login path; the admin console, the token-keyed endpoints and the SAML ACS remain unscoped — [`SECURITY.md`](SECURITY.md#what-is-scoped-and-what-still-is-not) |
| No WAF | `09 §6.6` | load the Traefik plugin **before** attaching the middleware, or Traefik answers 503 for the whole router |
| No IDS/IPS | `09 §6.7` | Falco; needs an alert destination *and* a named owner |
| k3s secrets not encrypted at rest | `10 §7.3` | 15 min, root on the server node |
| Backup lands in the same failure domain as the data | `15c §T-03` | off-node copy, or CNPG WAL archiving |
| Secrets live in a mode-600 file | `10 §8.4` | adopt SOPS + age the day a second operator or a second machine appears |

**No production cluster has ever run this.** The prod profile has been installed once, into a
scratch namespace on the single-node dev cluster, and destroyed — see
`SECURITY-AUDIT-LOG.md` step 33, whose §8 lists in full what that does *not* prove.
The paths it could not touch are precisely the ones this guide warns about in prose: Postgres
already holds a key ring. Both remain reasoned about, not observed.

Three things that install changed and that affect an existing installation, including dev:

- **The PGDATA move is a migration.** A fresh volume root is owned by uid 0, `fsGroup` sets the
  group and never the owner, and `initdb` cannot chmod a directory it does not own — so a
  first-ever install crash-looped forever. `PGDATA` is now
  `/var/lib/postgresql/data/pgdata`, a subdirectory the entrypoint creates as uid 70. An
  installation created before that keeps its data directory at the mount root, and the
  `pgdata-location-guard` init container **will stop the next deploy on purpose** rather than let
  Postgres `initdb` an empty cluster beside the real data and report success. It prints the
  commands; it is one `mv` with the StatefulSet scaled to zero. Doing nothing is safe — a running
  pod is unaffected.
- **The self-signed issuer is renamed.** It was a cluster-scoped `ClusterIssuer` with the fixed name
  `selfsigned`, which meant two releases could not coexist in one cluster and `helm uninstall` of
  either deleted the issuer the other renewed against. It is now a namespaced
  is invalidated once. Same price the cache TLS cutover already documented.
- **`helm --wait` is gone.** It waited for the backup PVC to reach `Bound`, and on a
  `WaitForFirstConsumer` StorageClass — k3s local-path's default — that does not happen until the
  nightly CronJob fires. A first prod install burned 30 minutes across three retries and then
  failed. `deploy.sh` now runs `kubectl rollout status` on the workloads that actually render.

---

### The client's address behind the ingress

Every per-IP control the deployment has — the sign-in lockout, the audit trail — reads
`RemoteIpAddress`. The application already does its half correctly: `X-Forwarded-For` is honoured
only from the CIDRs in `App__TrustedProxies` (`rediensiam.app.trustedProxies` in the chart), and it
**refuses to start in Production** if that value is missing, because trusting RFC1918 by default
would let any pod in the cluster spoof the header.

The half the application cannot do is make the proxy see the real client. On k3s it does not by
default: ServiceLB (klipper-lb) sits in front of Traefik with `externalTrafficPolicy: Cluster`,
which SNATs the source to its own pod IP before Traefik reads the packet. Every external caller
then arrives as one address, and the lockout stops being a defence — five wrong passwords from
anywhere lock sign-in for **every user at once**, for fifteen minutes.

```bash
helm upgrade traefik traefik/traefik -n kube-system --reuse-values \
  --set service.spec.externalTrafficPolicy=Local
```

`deploy/cluster/traefik-source-ip.yaml` carries this, with the `HelmChartConfig` equivalent for a
k3s-managed Traefik and the two caveats that matter: `Local` restricts traffic to nodes running a
Traefik pod, and it cannot preserve the source of a request that originates *on* the node — a
developer curling `iam.localhost` from the cluster machine is a hairpin through kube-proxy, which
SNATs regardless. Verify from another machine, and read the address back out of the audit row.
The reasoning in full is in [SECURITY.md §6](SECURITY.md).


## When something goes wrong

**Preflight fails.** Read the `→` line — it names the fix. Nothing was built or deployed.

**`helm upgrade` fails.** `helm history rediensiam -n <ns>` shows what happened; `helm rollback`
reverts. `deploy.sh` already retries three times and rolls back between attempts.

**Pods `Pending` forever.** Almost always no default StorageClass. `kubectl describe pvc -n <ns>`.

**The app pod crash-loops on startup.** Check `App__TrustedProxies` — the image runs as
Production and `Program.cs` refuses to start on an empty value rather than silently trusting
RFC1918. Preflight checks this against the cluster's real pod CIDR.

**Nothing reaches Postgres after a TLS change.** The DSNs and the server disagree. `sslmode=require`
against a server without TLS fails closed, and so does a cleartext DSN against `hostssl`. The
chart turns both into template errors — if you got past that, `pg_hba.conf` on the PVC is ahead
of your values.

**Verify reports a failure.** Until it passes, the corresponding claim in `SECURITY-AUDIT-LOG.md`
is true of files on disk and false of the running system. That is the entire reason the script
exists.
