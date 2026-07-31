# Step 15c — Infrastructure and data-layer residuals

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Scope:** `deploy/` only
**Cluster:** single-node k3s, namespace `default`
**Final state:** 5 pods Running · `./deploy/verify-deployment.sh --dev` → **27 passed · 0 failed · 2 skipped**

Findings addressed: **T-04** (Critical, chain C-4), **T-03** (High), **R-15** (Medium),
namespace isolation (`S-6` residual). Two defects were found *during* the work that no report had
recorded — both are described below with the evidence that produced them.

---

## Summary of what actually changed

| # | Finding | Outcome |
|---|---|---|
| T-04 | Postgres single SUPERUSER + `trust` | **CLOSED live and in the chart.** Applied to the *existing* database, not only to fresh installs. Migration rehearsed on a restored copy of real data first |
| T-03 | Backup never restored | **CLOSED.** Also found the backup had *never been able to run at all* — NetworkPolicy refused it. Restore proven byte-identical |
| R-15 | Postgres/Dragonfly cleartext | **LEFT GATED — deliberately.** cert-manager is not installed on this cluster. Runbook + cost below |
| S-6 | Namespace-wide default-deny | **Done** (`defaultDenyScope: namespace`). Namespace *move* deliberately not done — cost below |
| — | *New:* backup CronJob blocked by NetworkPolicy | Fixed |
| — | *New:* credential change never rolled the app pod | Fixed (`checksum/secret`) |

---

## T-04 — Postgres privilege separation

### What was there

Verified live before any change:

```
$ kubectl exec rediensiam-postgres-0 -- psql -U iam -d postgres -c "SELECT rolname,rolsuper,rolcreaterole,rolcreatedb,rolcanlogin FROM pg_roles WHERE rolcanlogin"
 rolname | rolsuper | rolcreaterole | rolcreatedb | rolcanlogin
---------+----------+---------------+-------------+-------------
 iam     | t        | t             | t           | t

$ kubectl exec rediensiam-postgres-0 -- grep -Ev '^#|^$' /var/lib/postgresql/data/pg_hba.conf
local   all   all                    trust
host    all   all   127.0.0.1/32     trust
host    all   all   ::1/128          trust
local   replication all              trust
host    replication all 127.0.0.1/32 trust
host    replication all ::1/128      trust
host all all all scram-sha-256
```

One role, superuser, no expiry, shared by app + Hydra + Keto, and `trust` on the local socket —
so `kubectl exec` into the pod was superuser **with no credential at all**. Note the first
command above needed no password: that is the finding.

### The design

`iam` stays the cluster's bootstrap superuser and is now used by **nothing at runtime**. Four
non-superuser login roles replace it:

| Role | Owns | May connect to | Attributes |
|---|---|---|---|
| `iam_app` | `rediensiam` | `rediensiam` | NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS |
| `iam_hydra` | `hydra` | `hydra` | same |
| `iam_keto` | `keto` | `keto` | same |
| `iam_backup` | — | all (read-only) | same + `pg_read_all_data` |

`CONNECT` is revoked from `PUBLIC` on all four databases and granted back only to the owner
(plus `iam_backup`). Without that revoke the split is cosmetic — `PUBLIC` holds `CONNECT` on
every new database by default, so `iam_keto` could still have opened Hydra's database.

`pg_hba.conf` `trust` → `scram-sha-256` everywhere. For fresh installs this is
`POSTGRES_INITDB_ARGS="--auth-local=scram-sha-256 --auth-host=scram-sha-256"`; initdb is the only
point at which those files are generated.

The bootstrap superuser password was also rotated. It was `changeme` — harmless while `trust`
made it irrelevant, and load-bearing the moment `trust` was removed. Rotating it was part of the
same change, not a separate one.

### Where

| File | Change |
|---|---|
| `deploy/rediensiam/templates/postgres.yaml` | `init.sh` rewritten: creates the four roles, pre-creates all three databases with the right owners, does the REVOKE/GRANT. Adds `POSTGRES_INITDB_ARGS` and the four password env vars |
| `deploy/rediensiam/templates/secret.yaml` | four new keys: `postgres-{app,hydra,keto,backup}-password` |
| `deploy/rediensiam/values.yaml` | `postgres.local.roles.{app,hydra,keto,backup}Password` |
| `deploy/deploy.sh` | generates four separate credentials; DSNs now name `iam_app` / `iam_hydra` / `iam_keto`; **guard** on the reuse path |
| `deploy/rediensiam/templates/backup.yaml` | runs as `iam_backup`, `--no-role-passwords` |

`rediensiam` used to be created by the application itself, which is the only reason `iam` needed
`CREATEDB`. Pre-creating it in `init.sh` is what lets `iam_app` drop that attribute.

### Could it be applied to the existing database? — Yes, and it was

**`init.sh` runs only against an empty data directory.** Shipping only the chart change would
have been exactly the "silently works on a new install" failure the task warns about. Two things
were done so that cannot happen:

1. **The live database was migrated** (below), so this cluster is actually fixed rather than
   fixed-in-principle.
2. **`deploy.sh` refuses to be quiet about it.** On the reuse path it checks the existing
   secrets file for `Username=iam;` / `postgres://iam:` or a missing `appPassword:` and prints a
   framed T-04 notice — **`exit 1` on `--prod`**, loud warning on `--dev`. An operator upgrading
   an old install cannot miss it.

### Migration runbook (this is what was run)

Two SQL files, reproduced in full so this is re-runnable. Neither contains a credential;
passwords arrive as psql variables, so psql quotes and escapes them.

**`migrate-roles.sql`** — run once against `postgres` as the superuser:

```sql
\set ON_ERROR_STOP on
BEGIN;
CREATE ROLE iam_app    LOGIN PASSWORD :'app_pw'    NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE iam_hydra  LOGIN PASSWORD :'hydra_pw'  NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE iam_keto   LOGIN PASSWORD :'keto_pw'   NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
CREATE ROLE iam_backup LOGIN PASSWORD :'backup_pw' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
GRANT pg_read_all_data TO iam_backup;
COMMIT;

ALTER DATABASE rediensiam OWNER TO iam_app;
ALTER DATABASE hydra      OWNER TO iam_hydra;
ALTER DATABASE keto       OWNER TO iam_keto;

REVOKE CONNECT ON DATABASE rediensiam FROM PUBLIC;
REVOKE CONNECT ON DATABASE hydra      FROM PUBLIC;
REVOKE CONNECT ON DATABASE keto       FROM PUBLIC;
REVOKE CONNECT ON DATABASE postgres   FROM PUBLIC;

GRANT CONNECT ON DATABASE rediensiam TO iam_app,   iam_backup;
GRANT CONNECT ON DATABASE hydra      TO iam_hydra, iam_backup;
GRANT CONNECT ON DATABASE keto       TO iam_keto,  iam_backup;
GRANT CONNECT ON DATABASE postgres   TO iam_backup;   -- pg_dumpall walks every database
```

`ALTER DATABASE ... OWNER` is used rather than `REASSIGN OWNED`, which also moves shared objects
and would have reassigned *all* of `iam`'s databases in one statement.

**`migrate-objects.sql`** — run once *inside each* of `rediensiam`, `hydra`, `keto`. Hydra and
Keto run `migrate sql up` on every start and the app runs EF migrations; all of them issue
`ALTER TABLE`, which requires ownership. Moving the databases without the objects inside them
comes back as a failed migration on the next restart, not now.

```sql
\set ON_ERROR_STOP on
DO $$
DECLARE
  target text := current_setting('migrate.newowner');
  r record;
BEGIN
  FOR r IN SELECT nspname FROM pg_namespace
           WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
  LOOP EXECUTE format('ALTER SCHEMA %I OWNER TO %I', r.nspname, target); END LOOP;

  FOR r IN SELECT c.relname, n.nspname, c.relkind
           FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE c.relkind IN ('r','p','S','v','m','f')
             AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
             -- a SERIAL/IDENTITY sequence belongs to its table and cannot be reassigned alone
             AND NOT EXISTS (SELECT 1 FROM pg_depend d
                             WHERE d.classid='pg_class'::regclass AND d.objid=c.oid
                               AND d.deptype IN ('a','i'))
  LOOP
    EXECUTE format('ALTER %s %I.%I OWNER TO %I',
      CASE r.relkind WHEN 'S' THEN 'SEQUENCE' WHEN 'v' THEN 'VIEW'
                     WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE'
                     ELSE 'TABLE' END, r.nspname, r.relname, target);
  END LOOP;

  FOR r IN SELECT p.oid::regprocedure AS sig, p.prokind FROM pg_proc p
           JOIN pg_namespace n ON n.oid=p.pronamespace
           WHERE n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
  LOOP EXECUTE format('ALTER %s %s OWNER TO %I',
         CASE r.prokind WHEN 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END, r.sig, target);
  END LOOP;

  FOR r IN SELECT t.oid::regtype AS tname FROM pg_type t
           JOIN pg_namespace n ON n.oid=t.typnamespace
           WHERE t.typtype IN ('e','c','d','r')
             AND n.nspname NOT LIKE 'pg\_%' AND n.nspname <> 'information_schema'
             AND NOT EXISTS (SELECT 1 FROM pg_class c WHERE c.reltype=t.oid)
  LOOP EXECUTE format('ALTER TYPE %s OWNER TO %I', r.tname, target); END LOOP;
END $$;
```

**Order of operations, exactly as executed:**

```bash
# 0. Safety net FIRST — a verified dump before touching anything (see T-03 below)
kubectl exec -n default rediensiam-postgres-0 -- pg_dumpall -U iam --clean --if-exists > pre-migration.sql

# 1. Roles, database ownership, CONNECT grants
kubectl exec -i -n default rediensiam-postgres-0 -- psql -U iam -d postgres -q \
  -v app_pw="$PW_APP" -v hydra_pw="$PW_HYDRA" -v keto_pw="$PW_KETO" -v backup_pw="$PW_BACKUP" \
  < migrate-roles.sql

# 2. Object ownership, per database. The SET must travel in the SAME stream as the DO block.
for pair in "rediensiam:iam_app" "hydra:iam_hydra" "keto:iam_keto"; do
  db=${pair%%:*}; own=${pair##*:}
  { printf "SET migrate.newowner = '%s';\n" "$own"; cat migrate-objects.sql; } \
    | kubectl exec -i -n default rediensiam-postgres-0 -- psql -U iam -d "$db" -q -v ON_ERROR_STOP=1
done

# 3. Rotate the bootstrap superuser (was `changeme`, about to become load-bearing)
printf "ALTER ROLE iam PASSWORD :'sp';\n" \
  | kubectl exec -i -n default rediensiam-postgres-0 -- psql -U iam -d postgres -q -v ON_ERROR_STOP=1 -v sp="$PW_SUPER"

# 4. Remove `trust`, reload (no restart, no downtime)
kubectl exec -n default rediensiam-postgres-0 -- cp /var/lib/postgresql/data/pg_hba.conf /var/lib/postgresql/data/pg_hba.conf.pre-t04
kubectl exec -n default rediensiam-postgres-0 -- sed -i -E 's/[[:space:]]trust[[:space:]]*$/ scram-sha-256/' /var/lib/postgresql/data/pg_hba.conf
kubectl exec -n default rediensiam-postgres-0 -- psql -U iam -d postgres -tAc "SELECT pg_reload_conf()"

# 5. Put the new DSNs in the (gitignored) secrets file, then redeploy
helm upgrade rediensiam deploy/rediensiam --namespace default \
  -f deploy/rediensiam/values.yaml -f deploy/rediensiam/values.dev.yaml -f deploy/rediensiam/values.secret.yaml \
  --set rediensiam.image.digest="$DIGEST" ... --wait --timeout 8m
```

**Rollback:** step 4 is reversible from `pg_hba.conf.pre-t04` + `pg_reload_conf()`. Steps 1–3 are
reversible by pointing the DSNs back at `iam` and redeploying — the old role is untouched
throughout. Step 0's dump covers everything else.

### Two mistakes the rehearsal and the checks caught

Both are recorded because "it printed ok" is what this ledger exists to distrust.

1. **`psql -c` plus stdin.** The first attempt at step 2 was
   `psql -q -c "SET migrate.newowner='…'" < migrate-objects.sql`. psql executes `-c` and exits
   **without ever reading stdin**, so the DO block never ran — while my own script happily
   echoed `ok`. Caught by querying afterwards rather than trusting the exit code:

   ```
   rediensiam: objects owned by iam = 83
   hydra:      objects owned by iam = 73
   keto:       objects owned by iam = 14
   ```

   After the corrected form (`{ printf 'SET …'; cat file; } | psql`): `0`, `0`, `0`.

2. **Table-owned sequences.** The rehearsal (against a restored copy of real data, not against
   live) failed with
   `ERROR: cannot change owner of sequence "hydra_client_pk_seq" / Sequence is linked to table "hydra_client"`.
   A `SERIAL`/`IDENTITY` sequence follows its table and cannot be reassigned on its own. The
   `pg_depend` exclusion in `migrate-objects.sql` above is the fix. Had this been run straight
   against live, Hydra would have been left half-migrated.

### Verification — live, after the change

```
$ kubectl exec -n default rediensiam-postgres-0 -- psql -U iam -d postgres -c "SELECT rolname,rolsuper,rolcreatedb,rolcreaterole FROM pg_roles WHERE rolcanlogin"
Password for user iam:            <-- `trust` is gone; this now demands a credential

  rolname   | rolsuper | rolcreatedb | rolcreaterole
------------+----------+-------------+---------------
 iam        | t        | t           | t
 iam_app    | f        | f           | f
 iam_backup | f        | f           | f
 iam_hydra  | f        | f           | f
 iam_keto   | f        | f           | f
```

```
pg_hba.conf after:
local   all         all                  scram-sha-256
host    all         all   127.0.0.1/32   scram-sha-256
host    all         all   ::1/128        scram-sha-256
local   replication all                  scram-sha-256
host    replication all   127.0.0.1/32   scram-sha-256
host    replication all   ::1/128        scram-sha-256
host all all all scram-sha-256
```

Old credential no longer works:

```
$ kubectl exec ... env PGPASSWORD=changeme psql -U iam -h 127.0.0.1 -d postgres -tAc "select 1"
psql: error: FATAL:  password authentication failed for user "iam"
```

Cross-database isolation, live:

```
$ ... PGPASSWORD=$PW_KETO psql -U iam_keto -h 127.0.0.1 -d hydra
psql: error: FATAL:  permission denied for database "hydra"
$ ... PGPASSWORD=$PW_HYDRA psql -U iam_hydra -h 127.0.0.1 -d rediensiam
psql: error: FATAL:  permission denied for database "rediensiam"
```

Privilege escalation, on the migrated database:

```
iam_app CREATE ROLE evil SUPERUSER  → ERROR: permission denied to create role
iam_app COPY … FROM PROGRAM 'id'    → ERROR: permission denied to COPY to or from an external program
iam_app SELECT FROM pg_authid       → ERROR: permission denied for table pg_authid
```

And each component is genuinely connected as its own role (`pg_stat_activity`, live):

```
  usename  |  datname   | count
-----------+------------+-------
 iam       | postgres   |     1     <- my psql session, not a component
 iam_app   | rediensiam |     1
 iam_hydra | hydra      |     1
 iam_keto  | keto       |     1
```

Hydra's automigration ran successfully as `iam_hydra` (`Successfully applied migrations!`), Keto's
as `iam_keto`, and the app started and served as `iam_app`.

**What T-04 does not fix.** `iam` is still SUPERUSER — a cluster needs one, and removing the
attribute from the only superuser is unrecoverable. It now requires a password that exists only
in the k8s Secret and the gitignored secrets file. Anyone who can read that Secret is still
superuser; that is the S-10/RBAC problem, not this one.

---

## T-03 — the backup

### The backup had never worked, not merely never been restored

The ledger recorded `LAST SCHEDULE <none>` and "no restore has ever been tested". Triggering it
manually showed something worse:

```
$ kubectl create job --from=cronjob/rediensiam-backup rediensiam-backup-manual-02 -n default
$ kubectl logs -n default -l job-name=rediensiam-backup-manual-02
pg_dumpall: error: connection to server at "rediensiam-postgres" (10.42.0.198), port 5432 failed: Connection refused
	Is the server running on that host and accepting TCP/IP connections?
wrote /backup/rediensiam-20260731T111703Z.sql.gz (20 bytes)
FATAL: dump is implausibly small — treating as failure
```

`rediensiam-postgres-lockdown` admits `:5432` from an explicit list of pod labels — the app,
Hydra, Keto. The backup Job carried none of them, so **every run would have been refused at the
network layer**. Because the CronJob had never fired, nothing had ever surfaced it. The backup
was not an untested control; it was a non-functioning one that looked present.

Two further defects in the same file:

- **The size guard ran after the rename.** `pg_dumpall | gzip` returns *gzip's* exit status, so a
  refused connection produced a 20-byte file, exit 0, `mv` to the real backup name, and only then
  the size check. Six such files were on the PVC — corrupt dumps wearing valid backup names.
- **No `pipefail`**, which is why the pipeline reported success in the first place.

### Fixes

| File | Change |
|---|---|
| `templates/backup.yaml` | pod label `app: <release>-backup`; `set -o pipefail`; size guard moved **before** the `mv`; `pg_isready` wait loop; `PGUSER=iam_backup`; `--no-role-passwords` |
| `templates/network-policies.yaml` | `postgres-lockdown` admits `app: <release>-backup` on `:5432` |

`--no-role-passwords` does double duty: reading `pg_authid` is the only part of `pg_dumpall` that
needs superuser, so dropping it is what allows the non-superuser `iam_backup` to run — and it
keeps every credential hash in the cluster out of a file that sits on disk for 14 days.

The `pg_isready` wait was added after a real observation, not speculatively: the 11:39 scheduled
run showed `restartCount=1`, a leftover `.in-progress-20260731T113900Z.sql.gz`, and success one
second later. NetworkPolicy is programmed asynchronously as a pod starts, so a dump that dials
immediately can be refused for the first second of its life. `backoffLimit: 2` means two such
races in a row is a silently missed nightly backup.

### The restore test

Restored into a **throwaway container**, never into the live server — a `pg_dumpall --clean`
restored into the live cluster would `DROP` the live databases.

```bash
# 1. Pull the dump the CronJob actually wrote off the PVC
kubectl run bkcat --rm -i --restart=Never -n default --image=postgres:16-alpine --quiet \
  --overrides='{"spec":{"containers":[{"name":"bkcat","image":"postgres:16-alpine",
    "command":["sh","-c","cat /backup/rediensiam-20260731T113541Z.sql.gz"],"stdin":true,
    "volumeMounts":[{"name":"b","mountPath":"/backup"}]}],
    "volumes":[{"name":"b","persistentVolumeClaim":{"claimName":"rediensiam-backup"}}]}}' \
  > cronjob-dump.sql.gz

gunzip -t cronjob-dump.sql.gz && gunzip -c cronjob-dump.sql.gz > cronjob-dump.sql

# 2. Fresh scratch server, superuser named something OTHER than `iam`
docker run -d --name iam-restore-verify -e POSTGRES_USER=postgres -e POSTGRES_PASSWORD=scratch postgres:16-alpine
until docker exec iam-restore-verify pg_isready -U postgres; do sleep 2; done

# 3. Restore
docker exec -i -e PGPASSWORD=scratch iam-restore-verify psql -U postgres -d postgres < cronjob-dump.sql

# 4. Re-grant the one thing that cannot restore (see below)
docker exec -e PGPASSWORD=scratch iam-restore-verify psql -U postgres -d postgres -c "GRANT pg_read_all_data TO iam_backup"
```

Real output:

```
pulled 21239 bytes
gzip integrity: OK
uncompressed: 118833 bytes, 41 CREATE TABLE
psql exit=0  ERROR lines: 1
```

Data fidelity — full-table md5 of every row, live versus restored:

```
  live     users md5:        50e358677027033fe866e87172aff8e1
  restored users md5:        50e358677027033fe866e87172aff8e1
  live     hydra_client md5: e7cecd8530bf943375173710a3bb7269
  restored hydra_client md5: e7cecd8530bf943375173710a3bb7269
  live     keto tuples md5:  a29d120e78ac8b437061b06fadbfd43d
  restored keto tuples md5:  a29d120e78ac8b437061b06fadbfd43d
```

Byte-identical across all three databases. The privilege model restores too — `PUBLIC` gets only
`T` (TEMP), not `c` (CONNECT):

```
hydra      owner=iam_hydra acl={iam_hydra=CTc/iam_hydra,=T/iam_hydra,iam_backup=c/iam_hydra}
keto       owner=iam_keto  acl={iam_keto=CTc/iam_keto,=T/iam_keto,iam_backup=c/iam_keto}
rediensiam owner=iam_app   acl={iam_app=CTc/iam_app,=T/iam_app,iam_backup=c/iam_app}
```

**The one error, and why it matters.** Dump line 58:

```sql
GRANT pg_read_all_data TO iam_backup WITH INHERIT TRUE GRANTED BY iam;
```

fails with `ERROR: permission denied to grant privileges as role "iam"` when restoring as any
superuser other than `iam`. Pre-granting `iam` membership does not help — the dump's own
`DROP ROLE`/`CREATE ROLE iam` discards it. The data is unaffected, but the consequence is sharp:

```
$ pg_dumpall -U iam_backup … on the restored cluster
pg_dump: error: query failed: ERROR:  permission denied for table hydra_client
exit=1

$ psql -c "GRANT pg_read_all_data TO iam_backup"    # the one-line fix
$ pg_dumpall -U iam_backup … exit=0
```

**A restored cluster silently stops backing itself up until that grant is re-applied.** That is
precisely the class of thing an untested backup hides, and it is now step 4 of the runbook above.

### The CronJob controller now actually fires

`LAST SCHEDULE <none>` was the original symptom, and a manually created Job does not clear it.
The schedule was temporarily set to `*/1 * * * *` to make the controller itself run one, then
restored to the chart value `0 3 * * *` (verified: no drift):

```
SuccessfulCreate  cronjob/rediensiam-backup  Created job rediensiam-backup-29758299
Completed         job/rediensiam-backup-29758299  Job completed
SawCompletedJob   cronjob/rediensiam-backup  condition: Complete

lastScheduleTime:   2026-07-31T11:39:00Z
lastSuccessfulTime: 2026-07-31T11:39:04Z
```

PVC state at the end — the six 20-byte corrupt files were deleted, two real dumps remain:

```
-rw-r--r-- 1 postgres postgres 21239 Jul 31 11:35 rediensiam-20260731T113541Z.sql.gz
-rw-r--r-- 1 postgres postgres 21233 Jul 31 11:39 rediensiam-20260731T113901Z.sql.gz
```

### Still true, and still not fixed

The dump lands on the same node and the same disk as the data it protects. It covers a dropped
table, a bad migration, a tenant deleting itself. It does not cover the disk dying. Copying the
PVC off-node is the only thing that closes that, and this cluster has no second failure domain.
Unchanged from `backup.yaml`'s own header comment — stated here so it is not mistaken for closed.

---

## R-15 — Postgres and Dragonfly cleartext: left gated, deliberately

**cert-manager is not installed on this cluster.** Checked three ways:

```
$ kubectl get crd | grep -i cert          → (nothing)
$ kubectl api-resources | grep cert-manager → NO cert-manager api resources
$ kubectl get pods -A | grep -i cert      → NO cert-manager pods
```

`templates/postgres.yaml` renders a `cert-manager.io/v1 Certificate` when
`postgres.local.tls.enabled` is set. On this cluster that manifest would be **rejected by the API
server** — no such kind — and the deploy would fail. Enabling it now would be shipping a control
that breaks at 3am, which is the thing the task explicitly asked me not to do. Both stay gated.

### Enablement runbook (when cert-manager exists)

Postgres is the safe one: with `ssl=on` the server still *accepts* cleartext clients, so the
server side can be enabled with no outage and clients cut over one at a time.

```bash
# 1. Prerequisite — install cert-manager (NOT installed by this chart)
helm repo add jetstack https://charts.jetstack.io && helm repo update
helm install cert-manager jetstack/cert-manager -n cert-manager --create-namespace --set crds.enabled=true
kubectl -n cert-manager rollout status deploy/cert-manager-webhook

# 2. Server side only. Safe: Postgres with ssl=on still accepts non-TLS clients.
#    rediensiam.postgres.local.tls.enabled: true   → redeploy
#    Confirm: kubectl exec rediensiam-postgres-0 -- psql -U iam -c "SHOW ssl"   → on

# 3. Cut the DSNs over ONE AT A TIME, verifying each before the next.
#    In values.secret.yaml, sslmode=disable → sslmode=require, for:
#      rediensiam.secrets.databaseUrl   (append ;SSL Mode=Require;Trust Server Certificate=true)
#      hydra.hydra.config.dsn
#      keto.keto.config.dsn
#    Redeploy after each; confirm with:
#      SELECT usename, ssl FROM pg_stat_ssl JOIN pg_stat_activity USING (pid);

# 4. Verify no cleartext client remains, THEN close the door if you want to:
#    add `hostssl` / remove `host` lines from pg_hba.conf.
```

**Ceiling:** the chart's Certificate is issued by the `selfsigned` ClusterIssuer, so `require`
(encryption, no server authentication) is the honest maximum. `verify-full` needs a real CA whose
root is distributed to the app, Hydra and Keto containers.

Dragonfly is **not** the same shape and must not be treated as such: with `--tls` it *stops*
accepting cleartext, so the `cacheUrl` secret must gain `ssl=true` in the **same** change or the
app loses its cache instantly. It is an atomic cutover, not a staged one.

| Item | Cost | Blocker |
|---|---|---|
| cert-manager install | 0.5 h + 3 pods and a CRD set to own forever | none, it is just not there |
| Postgres `sslmode=require` | 1 h, staged, no outage | cert-manager |
| Postgres `verify-full` | +4 h | a real CA + root distribution to three containers |
| Dragonfly TLS | 1 h, **hard atomic cutover** | cert-manager |

---

## Namespace isolation — namespace-wide deny done, namespace move declined

### What changed

`default` no longer holds the nine `yandee-*` pods. Verified:

```
$ kubectl get all -n default
… only the 5 rediensiam pods, their services/deployments, and service/kubernetes
$ helm list -A
rediensiam default … | traefik kube-system … | traefik-crd kube-system …
```

The blocker step 9 cited is gone, so `networkPolicy.defaultDenyScope: namespace` was added
(default `namespace`; set it back to `release` when sharing a namespace). Live:

```
$ kubectl get networkpolicy rediensiam-default-deny-ingress -n default -o jsonpath='{.spec.podSelector}'
{}
$ kubectl get networkpolicy -n default
rediensiam-default-deny-ingress   <none>   (i.e. all pods)
```

**Being precise about what this buys.** Every RediensIAM pod is *already* selected by one of the
five explicit lockdown policies, so a namespace-wide deny changes nothing for the pods that exist
today. It is not the control `09` implied. What it buys is the *next* pod — a debug shell, a
co-tenant workload, a future Job — which now starts closed rather than starting open and waiting
for someone to notice. Real, but defense-in-depth, and worth saying plainly rather than banking
it as a fix for `S-6`.

### The namespace move was declined — here is the cost

`09` called moving RediensIAM to its own namespace the top runbook item. I did not do it:

- Kubernetes has **no** cross-namespace PVC move. `data-rediensiam-postgres-0` and
  `rediensiam-backup` are local-path PVCs bound to this node. Moving means uninstall, recreate,
  restore from dump — i.e. deliberate downtime plus a full restore of a live IdP.
- It cannot be an upgrade. `deploy.sh` hardcodes `NAMESPACE=default`; `helm upgrade` cannot change
  a release's namespace. It is uninstall + install.
- The real gain is an RBAC/quota/blast-radius boundary — genuine, but it is a *management-plane*
  boundary, and the network-layer half is what the default-deny above already provides.

**Cost: ~1 h of work plus a full stop-restore-start of the IdP, and it must happen at install
time.** The right moment is the next fresh install (or the first prod install, which has not
happened yet from this branch) — not as a migration on a running system whose backup restore path
was proven working only today. Recommend it be done as part of the first `--prod` deploy, with
`NAMESPACE` lifted out of `deploy.sh` into a flag at the same time.

---

## Two defects found during the work that no report had recorded

### 1. A credential change never rolled the app pod

After the T-04 migration, `helm upgrade` reported `STATUS: deployed`, `REVISION: 2` — and:

```
$ kubectl get pods -n default
rediensiam-685b67c778-gmbsf   1/1   Running   0   32m     <-- 32 minutes old
$ kubectl get deploy rediensiam -n default -o jsonpath='{.spec.template.metadata.annotations}'
                                                          <-- empty
```

Every credential reaches the app through `secretKeyRef`, which Kubernetes resolves **once at pod
start**. Changing only the Secret leaves the pod template byte-identical, so no rollout happens
and the app keeps serving with the old password. `deploy.sh` has always masked this because it
also passes a fresh image digest on every run — so this only shows up on exactly the operation
where it matters most: a credential rotation without a rebuild.

Fixed in `templates/deployment.yaml`:

```yaml
    metadata:
      annotations:
        checksum/secret: {{ include (print $.Template.BasePath "/secret.yaml") . | sha256sum }}
```

Redeployed; the app rolled to a new pod and `pg_stat_activity` then showed `iam_app`.

This also matters for `10-secrets-management.md`'s rotation procedures generally — any of them
that rotate a Secret without changing the image were not actually taking effect on the app pod.

### 2. `verify-deployment.sh` asserted neither T-03 nor T-04

The script is the answer to "deployed ≠ source", and it had no assertion covering either finding —
so both could regress invisibly. Three checks added, all credential-free:

- **V-20** — `pg_hba.conf` grants no `trust` (read directly from the pod).
- **V-21** — no component DSN connects as `iam`. Parses only the *username* out of
  `rediensiam-secrets.database-url` and the `rediensiam-hydra` / `rediensiam-keto` `dsn` keys;
  never prints a password.
- **V-22** — the backup CronJob has a `lastSuccessfulTime`. `LAST SCHEDULE <none>` now fails the
  run instead of sitting there looking like a control.

---

## Gate output

### `helm lint` — both combinations `deploy.sh` uses

```
########## helm lint values.yaml + values.dev.yaml ##########
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed

########## helm lint values.yaml + values.prod.yaml ##########
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed
```

### `helm template`

```
########## helm template values.yaml + values.dev.yaml ##########
exit=0  43 documents, 1738 lines
(no stderr)
########## helm template values.yaml + values.prod.yaml ##########
exit=0  46 documents, 1793 lines
(no stderr)
```

### `./deploy/verify-deployment.sh --dev`

```
═══════════════════════════════════════════════════════════════
 RediensIAM control verification — dev — 2026-07-31T13:46:15+02:00
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
  PASS  V-22      backup CronJob last succeeded 2026-07-31T11:39:04Z
───────────────────────────────────────────────────────────────
 27 passed · 0 failed · 2 skipped
 All asserted controls are live.
EXIT=0
```

Pods at the end (the `backup-…` pod is a retained completed Job, not a service):

```
rediensiam-65ddbf7745-8vkmx            1/1   Running     0   11m
rediensiam-backup-29758299-rdht7       0/1   Completed   1   7m15s
rediensiam-dragonfly-58857dc76-fjxpx   1/1   Running     0   47m
rediensiam-hydra-c7f79b4d8-k2wfc       1/1   Running     0   15m
rediensiam-keto-79ff57b5c9-gnsm6       1/1   Running     0   15m
rediensiam-postgres-0                  1/1   Running     0   15m
```

---

## What is left, with its cost

| Item | Why still open | Cost |
|---|---|---|
| `iam` is still SUPERUSER, guarded only by a k8s Secret | a cluster needs one superuser; whoever can read the Secret is it | needs RBAC on Secrets + external secret storage — see S-10 |
| Backup lives on the same node as the data | single-node k3s, no second failure domain | needs off-node storage; ~2 h once one exists |
| Postgres / Dragonfly TLS (R-15) | cert-manager not installed | 0.5 h install + 1 h + 1 h; +4 h for `verify-full` |
| Dedicated namespace | no cross-namespace PVC move; uninstall+restore only | ~1 h **plus IdP downtime**; do it at the next fresh install |
| Hydra system secret is still `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS` | pre-existing R-06 residual; rotating it invalidates every live token and consent session | out of scope here; `10-secrets-management.md` §4 has the ordered procedure |
| `bootstrapPassword` is still `Admin1234!` | same file, same R-06 residual, dev only | `./deploy/deploy.sh --dev --rotate-secrets` (discards dev DB state) |
| Registry auth/TLS, admin cert, WAF, IDS | unchanged from `09 §8` | as costed there |

**Prod has not been deployed from this branch.** Everything above is verified on dev. The
`--prod` path is template-verified and now carries a hard `exit 1` if it meets a pre-T-04 secrets
file, but it has not been run.

### Credential note

Two DSN passwords (`iam_hydra`, `iam_keto`) were briefly echoed into a tool transcript by a
redaction pattern that did not cover `postgres://user:pass@` URLs. Both roles were re-keyed with
`ALTER ROLE ... PASSWORD` before the deploy, and the secrets file updated to match; the exposed
values were never the deployed ones. No secret was written to a tracked file —
`deploy/rediensiam/values.secret.yaml` is gitignored (`.gitignore:25`) and mode 600.
