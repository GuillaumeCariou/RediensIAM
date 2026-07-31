# Deploying RediensIAM

From a bare cluster to a working identity provider.

The runbooks behind this guide live in `.security-hardening/`. That is an audit trail, not an
install guide — this file is the install guide, and it points back at them wherever a procedure
is too long or too situational to inline.

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
(`.security-hardening/09-infra-security.md §6.8`):

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
Admin console  http://localhost:30501/admin/
OIDC discovery http://iam.localhost/.well-known/openid-configuration
```

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
`.security-hardening/09-infra-security.md §6.3`. HTTP-01 cannot certify a Tailscale MagicDNS name;
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
`.security-hardening/18-cnpg-tls-rls.md §1`. It has not been run against a real cluster; treat it
as a starting point to verify.

Note that `verify-deployment.sh` V-20, V-21 and V-23 **skip** under CNPG: they read
`rediensiam-postgres-0` directly. Making them CNPG-aware is about an hour and is not done.

**Backups.** With `builtin` you choose the schedule, the volume size and how many dumps to keep —
and then the script asks where the **off-node** copy goes and will not accept a blank answer. The
nightly `pg_dumpall` lands on a PVC on the same node and the same disk as the database it
protects. That covers a bad migration or a dropped table. It does not cover losing the node.
Answering `none` requires a typed acknowledgement. The answer is recorded as a comment in the
override file; the chart does not automate it.

A CronJob is not a backup until a dump has been restored. The restore test —
against a **throwaway** container, never the live server, and with the one non-obvious
`GRANT pg_read_all_data TO iam_backup` that a restore silently loses — is
`.security-hardening/15c-infra-residuals.md §T-03`.

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
   `.security-hardening/10-secrets-management.md §7.3`.
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
rewrite it. Run the live steps in `.security-hardening/18-cnpg-tls-rls.md §2` *before* the first
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

Gated on application change A-1 (`.security-hardening/18-cnpg-tls-rls.md §4`), which landed in
`.security-hardening/21-rls-app-support.md` — so the gate is now "deploy a build that contains it,
then verify", not "wait for the code". Then:

1. Verify the app really sets the variable, on a connection it is using:
   `SELECT setting FROM pg_settings WHERE name = 'rediensiam.org_id'`. Empty means stop.
2. `ALTER ROLE iam_backup BYPASSRLS` — **before** enabling, or `pg_dump` aborts on the first RLS
   table and the nightly backup silently stops working.
3. Take a dump.
4. `rediensiam.postgres.rls.enabled: true`, redeploy, check `kubectl logs job/rediensiam-rls`.
5. Prove the backup still runs, before the next nightly.

Full runbook, both verification queries and the rollback SQL:
`.security-hardening/18-cnpg-tls-rls.md §3`.

### Rotating credentials

| Secret | Runbook | Shape |
|---|---|---|
| HKDF root encryption key | `16-key-rotation.md §7.1` | Two keys configured → roll all replicas → sweep → `total_pending == 0` → **only then** drop the old key. Dropping it early is unrecoverable. |
| Argon2 pepper | `16-key-rotation.md §7.3` | Prepend. No sweep and no completion signal — accounts re-pepper on next login, so finishing it is a policy decision about dormant users. |
| Hydra system secret | `10-secrets-management.md §4.1` | Prepend, never replace. Keep the old entry for at least `refresh_token` TTL (7 days here) before trimming. |
| Database roles | `10-secrets-management.md §4.2` | `ALTER ROLE` first — `POSTGRES_PASSWORD` is read only at initdb, so editing values alone rotates nothing. |
| Hydra client secrets | `10-secrets-management.md §4.4` | No dual-secret window in OAuth2. Deploy the new secret to the consumer in the same step. |
| PATs | `10-secrets-management.md §4.3` | Issue → deploy → confirm traffic → revoke. |

Before rotating anything cryptographic, read `.security-hardening/16-key-rotation.md §8` — it is
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
| Local registry has no authentication and no TLS | `09 §6.2` | 2h; **required** if k3s is not on this host — bind and auth move together, never one without the other |
| Dragonfly TLS off | `09 §6.5` / `18 §4` / `21` | the application side (A-3) is done; one chart change remains (mount the CA into the app pod). The cutover is atomic and user-visible either way — `cacheUrl` gains `ssl=true` in the *same* `helm upgrade` that sets `dragonfly.local.tls.enabled`, or the cache goes offline |
| RLS off | `18 §3` | the application side (A-1) is done and undeployed; enabling before that build is running is a total outage |
| No WAF | `09 §6.6` | load the Traefik plugin **before** attaching the middleware, or Traefik answers 503 for the whole router |
| No IDS/IPS | `09 §6.7` | Falco; needs an alert destination *and* a named owner |
| k3s secrets not encrypted at rest | `10 §7.3` | 15 min, root on the server node |
| Backup lands in the same failure domain as the data | `15c §T-03` | off-node copy, or CNPG WAL archiving |
| Secrets live in a mode-600 file | `10 §8.4` | adopt SOPS + age the day a second operator or a second machine appears |

**Production has never been deployed from this branch.** Every prod path in this guide is
template-verified and preflight-verified; none of it has been run against a real production
cluster.

---

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

**Verify reports a failure.** Until it passes, the corresponding claim in `.security-hardening/`
is true of files on disk and false of the running system. That is the entire reason the script
exists.
