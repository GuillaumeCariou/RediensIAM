# 33 — The production profile, actually deployed

**Date:** 2026-08-01 · **Branch:** `security/hardening-2026-07-30` (working tree at `3fcaf5c` + the
`deploy/` changes below) · **Cluster:** single-node k3s v1.34.5, Traefik 3.6.9, cert-manager v1.21.1
· **Scratch namespace:** `rediensiam-prodtest` (created, used, destroyed).

## What this is, and what it is not

`values.yaml + values.prod.yaml` had never been applied to a cluster. Every claim about the
production profile in this repository was template-verified and preflight-verified, and the README
said so in those words. This step ran it.

**This does not prove production works.** It proves the prod profile is internally coherent — that
the chart, the two scripts and the values files agree with each other well enough to produce a
running system with the prod-only controls live. See [§8](#8-what-this-does-not-prove) for the
explicit list of what a real production cluster would still have to establish on its own.

Finding it broken was the expected outcome, and it was broken: **six defects, all in `deploy/`,
five of which make a first-ever install fail outright or report a control it did not measure.**
All six are fixed and the fixes are exercised, not reasoned about.

---

## 1. Result

| | |
|---|---|
| Install | `NAMESPACE=rediensiam-prodtest ./deploy/setup.sh --prod` → helm `STATUS: deployed`, 5/5 pods Ready |
| `./deploy/verify-deployment.sh --prod` | **36 passed · 1 failed · 1 skipped** ([§6](#6-the---prod-verification-run)) |
| Dev, after | `./deploy/verify-deployment.sh --dev` → **36 passed · 0 failed · 2 skipped**, 5 pods |
| Scratch namespace | destroyed; the one cluster-scoped object created was destroyed with it ([§7](#7-teardown)) |

Before the fixes, `setup.sh --prod` could not complete at all. It failed in three different places
in sequence — each one only visible once the previous was fixed.

---

## 2. What broke, and what changed

### 2.1 `ClusterIssuer/selfsigned` — a fixed-name cluster-scoped object, so the chart can only be installed once per cluster

`helm install` refused before applying anything:

```
Error: INSTALLATION FAILED: Unable to continue with install: ClusterIssuer "selfsigned" in
namespace "" exists and cannot be imported into the current release: invalid ownership metadata;
annotation validation error: key "meta.helm.sh/release-namespace" must equal
"rediensiam-prodtest": current value is "default"
```

`templates/cert-manager-issuer.yaml` rendered a **ClusterIssuer literally named `selfsigned`**,
unconditionally, whenever the admin ingress, Postgres TLS or cache TLS was on — which in prod is
always. Three consequences, and only the first is about scratch namespaces:

- a cluster that already has an issuer called `selfsigned` — cert-manager's own documentation
  example name — fails `helm install` outright, before anything is applied;
- two RediensIAM releases cannot coexist in one cluster, in any namespaces;
- `helm uninstall` of either release **deletes the issuer the other one still renews against**.

Nothing needed it to be cluster-scoped. The only Certificates it signs — Postgres, Dragonfly and
the admin ingress — are all in the release's own namespace.

**Fixed:** it is now a namespaced `Issuer` named `{{ .Release.Name }}-selfsigned`
(`rediensiam.selfSignedIssuer` in `_helpers.tpl`). `postgres.yaml` and `dragonfly.yaml` reference
it by that name with `kind: Issuer`. `ingress.admin.clusterIssuer` now defaults to `""`, meaning
"the chart's own Issuer"; a non-empty value still names a real ClusterIssuer you run, and
`admin-ingress.yaml` emits `cert-manager.io/issuer` or `cert-manager.io/cluster-issuer`
accordingly. `setup.sh`'s `selfsigned` answer writes `clusterIssuer: ""`.

Not changed: the ACME issuer is still a ClusterIssuer with a fixed default name (`letsencrypt`),
but that name is already an operator-settable value (`certManager.acme.issuerName`), so the
collision there is a decision rather than a trap.

### 2.2 PostgreSQL could not initialise on a fresh volume — `CrashLoopBackOff` on every first install

```
chmod: /var/lib/postgresql/data: Operation not permitted
initdb: error: could not change permissions of directory "/var/lib/postgresql/data":
        Operation not permitted
```

Root cause, measured rather than guessed — a throwaway pod with the same PVC and the same
`securityContext`:

```
uid=70(postgres) gid=70(postgres) groups=70(postgres)
drwxrwsrwx    2 0        70            4096 /data
```

A freshly provisioned volume root is owned by **uid 0**. `fsGroup: 70` sets the group and never the
owner, and `initdb` chmods `PGDATA` to `0700` — which uid 70 may not do to a directory it does not
own. The pod crash-looped forever.

Every existing installation predates the non-root `securityContext`, so its data directory is
already uid-70-owned; dev's is `drwx------ postgres postgres`. That is the only reason this was
never seen.

**Fixed:** `PGDATA=/var/lib/postgresql/data/pgdata` — a subdirectory the entrypoint creates *as*
uid 70, so it owns it. `init.sh` already used `$PGDATA`, so the `hostssl` rewrite followed for
free. `verify-deployment.sh` V-20/V-23 read `pg_hba.conf` from whichever location the pod actually
uses (a stale path there returns empty, and both assertions treat empty as a breach).

**Migration, and it is not optional** — see [§9](#9-follow-ups).  An `initContainer`
(`pgdata-location-guard`) refuses to start if the volume holds a data directory at the mount root,
because otherwise Postgres would `initdb` an empty cluster beside the real data and report success.
It printed `pgdata guard: no data directory at the mount root — ok` on every install here.

### 2.3 `helm --wait` deadlocked on a PVC that cannot bind until 03:00 — a first install could never finish

```
Error: context deadline exceeded
  helm failed (attempt 1/3)
```

Reproduced twice, ten minutes apart, with every pod already `1/1 Running` and exactly one object
not ready:

```
NAME                STATUS    STORAGECLASS
rediensiam-backup   Pending   local-path
  Normal  WaitForFirstConsumer  waiting for first consumer to be created before binding
```

`helm --wait` waits for every PVC in the release to reach `Bound`. `<release>-backup` has no
consumer until the nightly CronJob fires. On a StorageClass with
`volumeBindingMode: WaitForFirstConsumer` — k3s local-path's default, and most cloud defaults —
that is 03:00. `helm_deploy` retries three times, so a first prod install burned 30 minutes and
then failed. Existing installs never see it: their backup PVC bound the first night and stayed
bound.

**Fixed:** `deploy.sh` no longer passes `--wait`. `wait_workloads()` runs `kubectl rollout status`
on the StatefulSet and the four Deployments that actually render (each guarded by a `kubectl get`,
so external-Postgres and external-cache modes skip theirs). The final install waited ~40 seconds
and printed `deployment "rediensiam-keto" successfully rolled out`.

Dropping `--wait` moves Helm's post-install hooks earlier — they now run alongside the app rollout
rather than after it. The only hook is the RLS Job, and it is the one thing that genuinely needed
the ordering. So `rls.yaml` now asserts it instead of assuming it: the Job polls for
`__EFMigrationsHistory` (60 × 5 s) and fails loudly if the app has not migrated, in the same spirit
as the `pg_isready` loop it already had for the NetworkPolicy race.

### 2.4 A smoke probe that could not connect aborted the whole deploy

The install succeeded — `STATUS: deployed`, five pods Running — and then:

```
 Smoke tests:

ERROR: deploy.sh failed
       the cluster may be half-changed. 'helm history rediensiam -n rediensiam-prodtest' shows
       what happened; 'helm rollback' reverts.
```

`check()` is written to *report* a failed probe (`✗ … got 000`). But under `set -euo pipefail`,
`code=$(curl … 2>/dev/null)` is a failed command substitution in an assignment when curl exits 7
(connection refused) or 28 (timeout), and that kills the script. The app Service had just gained
its endpoint. A healthy install told the operator the cluster might be half-changed.

**Fixed:** `… || true`. curl still writes `000` to stdout first, so the `✗` branch reports it as
designed.

### 2.5 `verify-deployment.sh --prod` measured the wrong hostname — and V-04 passed because of it

`preflight.sh` and `deploy.sh` both layer `values.prod.override.yaml`, the file `setup.sh --prod`
writes with the operator's answers. `verify-deployment.sh` did not. Every real prod install has a
different public hostname from the committed default, so the first `--prod` run reported:

```
 namespace rediensiam-prodtest · release rediensiam · public host auth.rediens.net
  PASS  V-04/admin/ public host refuses /admin/ (404)
  PASS  V-04/org  public host refuses /org (404)
  PASS  V-04/project public host refuses /project (404)
  PASS  V-04/service-accounts public host refuses /service-accounts (404)
  FAIL  V-05      public ingress serves auth.rediens.net without TLS
  FAIL  V-17      no Content-Security-Policy header on the login page
```

V-05 and V-17 are false negatives for a host that does not exist on this cluster. **V-04 is worse:
it passed.** Traefik answers 404 to every path on a Host it has no router for, V-04 counts 404 as
a refusal, and so the P-04 management-API assertion read green while measuring nothing at all.
This is the same class of defect the script was written to catch, inside the script.

**Fixed:** two changes. The override file is read and layered last. And V-04 now runs a positive
control first — `/login` on the public host must answer 2xx/3xx, or the four deny probes are
reported as inconclusive rather than as passes:

```
  PASS  V-04/host public host auth.prodtest.rediens.net is served here (/login 200)
  PASS  V-04/admin/ public host refuses /admin/ (403)
```

403 is the `ipAllowList` middleware refusing. 404 was Traefik shrugging.

### 2.6 RLS could never be enabled on any database `setup.sh --prod` had created

`helm upgrade --set rediensiam.postgres.rls.enabled=true` on the freshly installed prod release:

```
psql:/sql/rls.sql:79: ERROR:  iam_backup cannot bypass RLS — enabling these policies would stop
                              the nightly backup
HINT:  run as superuser, once, BEFORE this deploy: ALTER ROLE iam_backup BYPASSRLS;
```

`init.sh` granted `BYPASSRLS` only `{{- if .Values.rediensiam.postgres.rls.enabled }}`, and
`init.sh` only ever runs at initdb — while `setup.sh --prod` **forces RLS off on a first install
and does not even ask** (`RLS=false`, with a paragraph explaining why). So no prod database could
ever come out with the grant, and `values.yaml`'s claim that it "is no longer a thing to remember"
was true of no installation `setup.sh` could produce.

The fail-closed abort worked exactly as designed — the deploy stopped instead of the backup. But
the trap was unconditional.

**Fixed:** the grant is unconditional. It hands `iam_backup` nothing it did not already have —
`pg_read_all_data`, granted two lines above, reads every row RLS or no RLS. `values.yaml`'s comment
now says what is actually true, including that databases created before today may still lack it.

**Verified after the fix**, on a from-scratch prod install with RLS then turned on:

```
 personal_access_tokens | t | t | 1
 …
(19 rows)
  PASS  V-25      RLS applied as the table owner — RLS applied to 19 tables
```

---

## 3. Dragonfly TLS — what the server refuses

All from inside the Dragonfly pod (its egress policy allows DNS only, so the loopback address and
`--sni` stand in for the Service name; the certificate is the same one).

**Cleartext is refused, not merely unused.** A raw RESP `PING` over TCP:

```
$ printf "PING\r\n" | nc -q1 127.0.0.1 6379
-ERR Bad TLS header, double check if you enabled TLS for your client.

$ redis-cli -h 127.0.0.1 -p 6379 -a "$DRAGONFLY_PASSWORD" PING
Warning: AUTH failed
Error: Server closed the connection
```

**TLS with the mounted CA works:**

```
$ redis-cli --tls --cacert /etc/dragonfly-tls/ca.crt --sni rediensiam-dragonfly \
            -h 127.0.0.1 -p 6379 -a "$DRAGONFLY_PASSWORD" PING
PONG
```

**The pin is real** — the same connection against the OS trust store is rejected, so V-26/pin is
not satisfiable by a future "fix" that reaches for `TrustServerCertificate`:

```
$ redis-cli --tls --cacert /etc/ssl/certs/ca-certificates.crt … PING
Could not connect to Redis at 127.0.0.1:6379: SSL_connect failed: certificate verify failed
```

**The app reads and writes through the tunnel** — the only key in the cache is the one it wrote,
and the app logged the pin from the running process, not from a manifest:

```
$ redis-cli --tls --cacert /etc/dragonfly-tls/ca.crt … KEYS '*'
rediensiam:dataprotection:keys

$ kubectl logs …/rediensiam
Cache TLS: server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
```

Deployment args, for completeness:
`["--logtostderr","--requirepass=$(DRAGONFLY_PASSWORD)","--tls","--tls_cert_file=…","--tls_key_file=…"]`

## 3b. The DataProtection key ring is encrypted, and survives a pod restart

The ring, read out of the cache:

```
<key id="a14aa587-2c8f-4162-949c-cfcde724912e" version="1">…
  <encryptedSecret decryptorType="RediensIAM.Config.RootKeyXmlDecryptor, RediensIAM, …">
    <rediensiamEncryptedKey xmlns="">sltGhqmRZ5SyuVHiWkM1FpMM3AeZYX4DnDjgw+jSbAQ…
```

Every `<key>` element's secret is inside `<rediensiamEncryptedKey>`. Grepping the same element for
plaintext key material (`<masterKey`, `<value>`) returns **0** matches.

Restart, by deleting the pod outright:

```
before: pod=rediensiam-758cf864fc-lwjcn llen=1 id=a14aa587-2c8f-4162-949c-cfcde724912e
after : pod=rediensiam-758cf864fc-s6894 llen=1 id=a14aa587-2c8f-4162-949c-cfcde724912e
        Cache TLS: server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
```

Same key id, list length still 1. The new pod **adopted** the existing key rather than minting a
second one, which is only possible if `RootKeyXmlDecryptor` decrypted it and
`EncryptedOnlyXmlRepository` accepted it. `/health` through the public ingress: 200.

---

## 4. Prod-only paths exercised

All probes with `--resolve`; the hostnames are not in DNS and nothing on this host was edited.

| Path | Result |
|---|---|
| **`redirectScheme` :80 → :443** | `GET http://auth.prodtest.rediens.net/login` → `301 https://auth.prodtest.rediens.net/login` |
| **`adminOnlyPaths` deny on the public host** | `/admin/` `/org` `/project` `/service-accounts` → **403** each (the `ipAllowList` middleware, not a 404) |
| **Public host still serves what it should** | `/login` 200 · `/health` 200 · `/.well-known/openid-configuration` 200 |
| **Admin ingress instead of a NodePort** | `https://admin.prodtest.rediens.net/admin/` → **200**; `/health` → 200. `rediensiam-admin` Service is `ClusterIP` (V-16). No NodePort exists. |
| Admin host on :80 | 404 — the admin ingress binds `websecure` only, so there is no plaintext listener to redirect from |
| **cert-manager issuing against the configured issuer** | 4/4 Certificates `Ready`. Public cert from the ClusterIssuer named in the override; Postgres, Dragonfly and admin certs from the release's own namespaced Issuer. `openssl s_client` against Traefik returned the cert-manager certs with `DNS:auth.prodtest.rediens.net` / `DNS:admin.prodtest.rediens.net` — not Traefik's default cert. |
| **RLS** | **Prod values leave it off**, and `setup.sh --prod` forces it off without asking. Verified separately that it *can* now be enabled — see [§2.6](#26-rls-could-never-be-enabled-on-any-database-setupsh---prod-had-created). |
| **Nightly backup** | Ran it by hand: `wrote /backup/rediensiam-20260801T073554Z.sql.gz (17226 bytes)` — `pg_dumpall` as `iam_backup` against a `hostssl`-only Postgres. Proves the CronJob's credential, network path and TLS posture. It does **not** prove restore. |
| **NetworkPolicy is actually enforced by the CNI** | The check `setup.sh` says it cannot do. From an unlabelled `busybox` in the namespace: `hydra-admin:4445` refused, `keto-write:4467` refused, `dragonfly:6379` refused, app `:5001` refused. App `:5000` answered (400, Host validation) — which the policy explicitly permits for in-cluster relying parties. |

### Could not exercise

- **ACME / Let's Encrypt (`certManager.acme.enabled`)** — needs public DNS for the hostname and
  port 80 reachable from the internet. The public cert was issued by a self-signed ClusterIssuer I
  created for the test instead. The HTTP-01 solver path in `cert-manager-issuer.yaml` is still
  unexecuted.
- **Tailscale/headscale reachability of the admin host** — no mesh here; the admin ingress was
  reached over the LAN with a `Host:` header. What was proven is that the ingress, the certificate
  and the ClusterIP-only Service work. Who can reach that hostname is a Tailscale property, not a
  chart property.
- **A second node, a real StorageClass, or any HA behaviour** — one node, `local-path`.
- **CloudNativePG mode** (`postgres.local.enabled: false`) — the operator is not installed. Still
  untested end to end.
- **Backup restore** — see [§9](#9-follow-ups).

---

## 5. What the install actually is

`NAMESPACE=rediensiam-prodtest ./deploy/setup.sh --prod`, driven through a pty. The interview ran
for real and wrote `values.prod.override.yaml`; answers were `auth.prodtest.rediens.net` /
`admin.prodtest.rediens.net`, an existing ClusterIssuer for the public host, the chart's own issuer
for the admin host, the built-in Postgres, no off-node backup, no SMTP.

`preflight.sh --prod` reported **19 ok · 0 failed · 3 warnings** (no secrets file yet; hostname does
not resolve; the backup is a dump on the same node). It did not catch defects 2.1–2.4: it renders
the chart but never dry-runs it against the API server, so the ClusterIssuer ownership conflict was
invisible to it. One narrower gap worth naming: `preflight.sh`'s fresh-install placeholder values
set `databaseUrl` and the Dragonfly password but **not** `cacheUrl`, and all three R-15 cache
guards in `dragonfly.yaml` are gated on a non-empty `cacheUrl` — so the cache TLS cutover guards
are the one pair preflight never renders. Left as-is; `deploy.sh` and the chart both still catch it.

Two things about the artefact, stated so nobody over-reads the result:

- **The image is not the committed tree.** Three other agents were editing `src/` throughout; the
  image built for this test carries their in-flight changes to `AuthController`, `SamlService`,
  `TenantScopeInterceptor`, `Program.cs` and others. What was under test is `deploy/`, not the app.
- `setup.sh --prod` **can** target another namespace via `NAMESPACE=`, which it exports as both
  `NAMESPACE` and `NS`, so all three sub-scripts follow. It does not create the namespace
  (`preflight.sh` fails with the exact `kubectl create namespace` command, which is fine), and it
  does not record the namespace anywhere — the override file persists every other answer, so a
  later `./deploy/setup.sh --prod` without `NAMESPACE=` set would silently reuse the answers
  against `default`. Not fixed; flagged in [§9](#9-follow-ups).

---

## 6. The `--prod` verification run

`NS=rediensiam-prodtest ./deploy/verify-deployment.sh --prod`, against the final install:

```
 RediensIAM control verification — prod — 2026-08-01T09:57:25+02:00
 namespace rediensiam-prodtest · release rediensiam · public host auth.prodtest.rediens.net
  PASS  V-01      registry bound to 127.0.0.1 (loopback only)
  PASS  V-02      no rediensiam ClusterRole grants access to Secrets
  PASS  V-03      hydra-maester is not deployed
  PASS  V-04/host public host auth.prodtest.rediens.net is served here (/login 200)
  PASS  V-04/admin/ public host refuses /admin/ (403)
  PASS  V-04/org  public host refuses /org (403)
  PASS  V-04/project public host refuses /project (403)
  PASS  V-04/service-accounts public host refuses /service-accounts (403)
  PASS  V-05      public ingress has a TLS block for auth.prodtest.rediens.net
  PASS  V-06      Hydra :4444 discovery answers 200
  PASS  V-07      image pinned by digest (sha256:0c278d10643126047fcae01b02a5bcd4be5de887c67a115e43ac7126e3aa3caf)
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
  PASS  V-16      admin service is ClusterIP
  PASS  V-17      CSP carries script-src, base-uri, form-action, frame-ancestors, object-src
  PASS  V-18      CSP names no external font host
  --    V-19      values.yaml pins no image digest — cannot check for drift
  PASS  V-20      pg_hba.conf grants no 'trust' (all methods are scram-sha-256)
  PASS  V-21      no component connects as superuser 'iam' (users:iam_app iam_hydra iam_keto)
  FAIL  V-22      backup CronJob has never run (no lastScheduleTime) — an untested backup is a hypothesis
  PASS  V-23/server Postgres runs with ssl=on
  PASS  V-23/hba  pg_hba.conf admits TLS only (hostssl; local socket unaffected)
  PASS  V-23/dsn  app, hydra and keto DSNs all request TLS
  PASS  V-24      cache requires a password (48 chars)
  PASS  V-26/server Dragonfly runs with --tls (cleartext is refused, not merely unused)
  PASS  V-26/dsn  app cache DSN requests TLS (ssl=true)
  PASS  V-26/pin  app pinned the cache certificate — server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
  PASS  V-25      RLS applied as the table owner — RLS applied to 19 tables
───────────────────────────────────────────────────────────────
 36 passed · 1 failed · 1 skipped
```

### The one failure, and why it cannot pass here

**V-22 — the backup CronJob has never run.** Not a scratch-namespace artefact and not fixable by
this step: the schedule is `0 3 * * *` and the namespace lived for about an hour on a Saturday
morning. `.status.lastScheduleTime` is set by the CronJob controller, and a manually created Job
(`kubectl create job --from=cronjob/…`) does not set it, so triggering the backup by hand — which I
did, successfully, see [§4](#4-prod-only-paths-exercised) — cannot turn this green either. **The
assertion is correct and should stay failing**: on a real prod install it goes green the morning
after go-live, and if it does not, that is exactly the T-03 finding it exists to surface.

### The skip

**V-19** — `values.yaml` pins no image digest, so there is no drift baseline to compare against.
`deploy.sh` supplies the digest with `--set` at deploy time (V-07 passed on it). This skip is
identical in dev.

### Assertions that pass here but mean less than they look

- **V-01** (registry on loopback) and **V-02** (no cluster-scoped Secret access) are properties of
  the *host* and the *cluster*, not of the namespace. They would read the same for any namespace,
  including one where nothing was installed.
- **V-05** checks that the Ingress object carries a `tls:` block for the public host. It does not
  check that a browser gets a *trusted* certificate. Here the issuer was self-signed by design; on
  a real public host it must be a publicly trusted CA, and nothing in this script would notice the
  difference.
- **V-17/V-18** read the CSP from the login page served over the ingress. That is the same code
  path as dev; the prod-specific part (it is reached over TLS, after a `redirectScheme` hop) is what
  was new here.

---

## 7. Teardown

```
$ kubectl delete namespace rediensiam-prodtest      # namespace "rediensiam-prodtest" deleted
$ kubectl delete clusterissuer prodtest-selfsigned  # deleted
$ rm -f deploy/rediensiam/values.prod.override.yaml deploy/rediensiam/values.prod.secret.yaml
$ docker rmi localhost:5000/rediensiam:prod
```

**Cluster-scoped objects touched — one, declared and restored.** `ClusterIssuer/prodtest-selfsigned`
was created so the prodtest public ingress had a real, existing issuer of its own rather than
borrowing the dev release's. It is gone. `kubectl get clusterissuer` now returns only `selfsigned`,
the dev release's, exactly as before.

No secret was written to a tracked file. `values.prod.secret.yaml` (mode 600, gitignored) held a
scratch bootstrap password and generated credentials; it and the gitignored
`values.prod.override.yaml` were deleted with the namespace.

**Dev, after everything:**

```
$ ./deploy/verify-deployment.sh --dev
 36 passed · 0 failed · 2 skipped
 All asserted controls are live.
```

Five pods Running in `default`, plus the completed RLS hook. (36 rather than 35 because V-04 gained
the positive-control assertion from [§2.5](#25-verify-deploymentsh---prod-measured-the-wrong-hostname--and-v-04-passed-because-of-it).)

**One disclosure.** I never issued a command naming the `default` namespace. I did twice run
`pkill -f "helm upgrade --install rediensiam"` to stop a stalled prodtest install, and that pattern
is namespace-agnostic — another agent was deploying dev in the same window, and `helm history
rediensiam -n default` shows revisions 22–27 between 08:46 and 09:02 including a failure and a
rollback. I cannot rule out that one of those kills contributed. Dev ended `deployed` at revision
27 and verifies clean; noting it because "I didn't touch it" would be a stronger claim than the
evidence supports.

---

## 8. What this does **not** prove

The profile is coherent. That is all this establishes. A real production cluster still has to
prove, and none of it follows from anything above:

1. **That a publicly trusted certificate can be issued.** ACME HTTP-01 was never executed. Public
   DNS, port 80 reachable from the internet, and Let's Encrypt's rate limits are all untested. The
   `letsencrypt` ClusterIssuer in `cert-manager-issuer.yaml` has never been applied to a cluster.
2. **That the admin surface is actually private.** Here it was reached over the LAN with a `Host:`
   header. In prod its confidentiality is a Tailscale/headscale property. The chart contributes
   ClusterIP + an ingress; it cannot contribute reachability.
3. **That the data survives anything.** One node, one disk, `local-path`. No restore was performed;
   `pg_dumpall` producing a 17 KB file is not a recovery point. No off-node copy exists.
4. **That it holds up under load, or over time.** One request at a time, one hour of uptime. No
   certificate renewal, no log rotation, no PVC filling, no OOM, no token expiry, no refresh
   rotation, no `helm upgrade` across a schema migration on a populated database.
5. **That an upgrade path works.** Every install here was from scratch, three times. The paths this
   step could not touch are precisely the ones `values.prod.yaml` warns about in prose: Postgres
   `requireSsl` against an existing `pg_hba.conf`, and the Dragonfly TLS cutover against a cache
   that already holds an unprotected key ring. Both remain reasoned-about, not observed.
6. **That the application is production-ready.** The image under test carried three other agents'
   uncommitted work. Nothing here is a statement about `src/`.
7. **That the cluster is hardened.** k3s secret encryption at rest, the CNI's own configuration,
   node hardening and the k3s API surface are all outside this chart and were not assessed. (The
   CNI *does* enforce NetworkPolicy — [§4](#4-prod-only-paths-exercised) — which is the one
   cluster-level assumption the chart's controls depend on.)

---

## 9. Follow-ups

**Blocking, before the next deploy of any existing installation — including dev.**

- **The PGDATA move is a migration.** `pgdata-location-guard` will stop the next
  `./deploy/deploy.sh --dev` on the existing dev database, on purpose. It is one `mv` with the
  StatefulSet scaled to zero; the guard prints the commands. Doing nothing is safe — the currently
  running dev pod is unaffected. Not automating it is deliberate: a partially completed automatic
  move of a data directory is worse than a stopped rollout.
- **The self-signed issuer is renamed.** `helm upgrade` on dev will delete
  `ClusterIssuer/selfsigned` and create `Issuer/rediensiam-selfsigned`, re-issuing the Postgres and
  Dragonfly certificates. The Dragonfly pod restarts, which empties the DataProtection key ring —
  every dev session is invalidated once. Same price the cache TLS cutover already documented.
- **Existing databases still need `ALTER ROLE iam_backup BYPASSRLS` by hand** if RLS is to be
  enabled and they were initdb'd before today with RLS off. `files/rls.sql` fails the deploy rather
  than the backup, so this cannot go unnoticed.

**Not blocking.**

- `setup.sh --prod` should record the namespace in the override file. Every other answer persists;
  the namespace does not, so a second run without `NAMESPACE=` set would target `default` with
  prod's answers.
- `preflight.sh` should add a `helm install --dry-run=server`. It is the only cheap check that
  would have caught the ClusterIssuer ownership conflict, and it catches the whole class
  (admission webhooks, ownership, quota) rather than one instance.
- `preflight.sh`'s fresh-install placeholders should include a `cacheUrl`, so the three R-15 cache
  guards in `dragonfly.yaml` are exercised at preflight time rather than only at deploy time.
- **`src/` follow-up, out of scope here:** V-25 asserts that the last RLS *apply* succeeded, not
  that the policies are in force now. Closing that needs a live `pg_policies` count, which needs a
  database credential — and this script's discipline is that it never reads one. The existing note
  in `verify-deployment.sh` proposes a `/health/detail` field; that remains the right shape and it
  is an application change.
- The nightly backup writes beside the database it protects. Unchanged, and still the largest gap
  in this profile.
