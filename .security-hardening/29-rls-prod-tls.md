# Step 29 — enabling row-level security on a running system, and cache TLS in the production values

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` (from `v0.2.0`, `ef208be`) · **Scope:** `deploy/`, `docs/`
**Specs:** `18-cnpg-tls-rls.md` §3 (design, rehearsal, runbook) · `21-rls-app-support.md` (the application half) · `23-cache-hardening.md` (the dev cutover)
**Suite:** **1346 passing, 0 failing, 0 skipped** — unchanged, no test was added or modified
**Deployment:** `./deploy/verify-deployment.sh --dev` → **35 passed · 0 failed · 2 skipped**, 5 pods Running
**Not committed.** `src/`, `tests/`, `frontend/` and `sdk/` were not touched.

---

## Summary

| # | Item | Outcome |
|---|---|---|
| 1 | S-5 phase 2 — RLS | **ON and live in dev.** 19 tables `ENABLE` + `FORCE`, one policy each. Cross-tenant read, insert, update and delete all refused at the database against two real tenants created through the application's own API. Backup still succeeds and its dump is not partial |
| 2 | The `BYPASSRLS` prerequisite | **No longer a thing to remember.** `init.sh` grants it at initdb when the flag is set; `rls.sql` refuses to create a single policy on a database where `iam_backup` cannot bypass. Both halves proven — including by removing the grant on the live database and watching `pg_dumpall` fail |
| 3 | R-15 cache half — production values | **`values.prod.yaml` now sets `dragonfly.local.tls.enabled: true`.** Verified by rendering and by reasoning in all four guard directions. **UNTESTED LIVE** — there is no production cluster. This is not "proven", and this report will not say it is |
| 4 | The key-ring pre-step | The `DEL rediensiam:dataprotection:keys` case from `23`'s residuals is now in the prod runbook (`docs/DEPLOYMENT.md`) and on the flag itself in `values.prod.yaml`, not only in a report |
| 5 | V-25 | Flipped from a skip to a real assertion, and made honest about the one thing it still cannot see |

**RLS is on in dev and stays off in prod.** That is deliberate and it is stated everywhere the flag
is described: on an already-running database this is a migration, not an upgrade, and there is no
production cluster to rehearse it on. Enabling it in `values.prod.yaml` would have been a change
nobody could test, which is the shape of thing this whole audit exists to stop shipping.

---

## 0. The honest limit, restated before anything else

**Enabling RLS does not make the authentication surface tenant-safe.** Login resolves a user by
e-mail before any tenant is known — the `Projects` row that supplies `AssignedUserListId` must
itself be read unscoped — so that path runs as `'system'`, and so do registration, password reset,
e-mail verification, the social and SAML callbacks, PAT introspection inside
`GatewayAuthMiddleware`, EF migrations, bootstrap, the audit retention sweep, the webhook dispatcher
and the SuperAdmin listings. The enumerated list is
`TenantScopeInterceptor.LegitimatelyUnscopedPaths`.

RLS protects **authenticated, org-bearing traffic**. On those paths it is now a schema-level backstop
under ~200 hand-written conjuncts, which is real defence in depth. On the unauthenticated surface
nothing changed today.

This is not a theoretical caveat, and it is worth putting a number on it. With `log_statement='all'`
for one minute of ordinary dev traffic — an OIDC authorize, a login, TOTP, consent, a token
exchange, SuperAdmin listings and one PAT-authenticated introspection — these are the scopes the
application actually set:

```
$ kubectl logs -n default rediensiam-postgres-0 --since=6m \
    | grep 'rediensiam.org_id' | sed "s/.*= '\(rediensiam.org_id\)', .* = '\([^']*\)'/\2/" | sort | uniq -c
      5 94177c59-8d98-4dd1-8a4b-1e6b6add59b8
     15 system
```

Five scoped, fifteen unscoped, on a workload that was *deliberately* exercising the tenant path.
`docs/SECURITY.md` §2 carries that table and the sentence above it, and it still says
"turning RLS on does not make the login path tenant-safe" after this change.

---

## 1. Step 0 — the precondition, checked rather than assumed

`18` §3's runbook opens with "verify the app really sets the variable, on a connection it is using.
An empty result means STOP." Two independent checks were run before anything was enabled.

**The deployed binary carries the interceptor.** (.NET stores string literals as UTF-16 in the `#US`
heap, so a plain `grep` on the DLL finds the type name but not the GUC name — hence the `tr -d`.)

```
$ kubectl exec -n default rediensiam-78fbd5bc96-9zt2g -- sh -c \
    "tr -d '\000' < /app/RediensIAM.dll | grep -ao 'rediensiam\.org_id\|LegitimatelyUnscopedPaths' | sort | uniq -c"
      1 LegitimatelyUnscopedPaths
      4 rediensiam.org_id
```

**And it actually issues it, on the wire.** A binary containing a string is not a running control.
`log_statement='all'`, then the app pod was deleted so its replacement opened fresh connections:

```
$ psql -U iam -c "ALTER SYSTEM SET log_statement = 'all'"   # ALTER SYSTEM cannot run inside a
$ psql -U iam -c "SELECT pg_reload_conf()"                  # transaction block — two statements
t
$ psql -U iam -tAc "SHOW log_statement"
all

$ kubectl delete pod -n default rediensiam-78fbd5bc96-9zt2g
$ curl -s -o /dev/null -w '%{http_code}' http://iam.localhost/health
200

$ kubectl logs -n default rediensiam-postgres-0 --since=3m | grep -i org_id | head -3
2026-07-31 17:35:23.971 UTC [26127] LOG:  execute <unnamed>: SELECT set_config($1, $2, false)
2026-07-31 17:35:23.971 UTC [26127] DETAIL:  parameters: $1 = 'rediensiam.org_id', $2 = 'system'
…  (11 in the first seconds of the new pod's life)
```

Reverted immediately afterwards (`ALTER SYSTEM RESET log_statement` → `SHOW log_statement` → `none`).
Statement logging was on for two short windows only, and Npgsql binds every value, so no credential
was ever in the SQL text.

---

## 2. The prerequisite that would have broken the backup silently

`18` §3 called `ALTER ROLE iam_backup BYPASSRLS` "not optional" and proved it on a **rehearsal**
server. It is now proven on this one, and — more usefully — it is now enforced by the chart.

### Granted, live

```
$ psql -U iam -c "SELECT rolname, rolbypassrls, rolsuper FROM pg_roles WHERE rolname LIKE 'iam%'"
  rolname   | rolbypassrls | rolsuper
------------+--------------+----------
 iam        | t            | t
 iam_app    | f            | f
 iam_backup | f            | f          ← before
 iam_hydra  | f            | f
 iam_keto   | f            | f

$ psql -U iam -tAc "ALTER ROLE iam_backup BYPASSRLS"
ALTER ROLE
 iam_backup | t            | f          ← after
```

### And then removed again, to show it is load-bearing on *this* database

This is the part `18` could only assert. With the policies live and the grant taken away, the exact
command the nightly CronJob runs:

```
$ psql -U iam -tAc "ALTER ROLE iam_backup NOBYPASSRLS"
$ kubectl exec -n default rediensiam-postgres-0 -- env PGPASSWORD=*** PGSSLMODE=require \
    sh -c 'pg_dumpall -U iam_backup -h 127.0.0.1 --clean --if-exists --no-role-passwords > /tmp/t.sql; echo "exit=$?"'
pg_dump: error: query failed: ERROR:  query would be affected by row-level security policy for table "audit_log"
pg_dump: detail: Query was: COPY public.audit_log (…) TO stdout;
pg_dumpall: error: pg_dump failed on database "rediensiam", exiting
exit=1
```

The CronJob run in the same state finished `succeeded= failed=1`. It fails loudly rather than
writing a partial dump — but a failed nightly job is still a stopped backup, and T-03 is the finding
about a control that looks present and works never.

### Two guards, so neither a fresh install nor an existing database can miss it

| Where | What it does | Covers |
|---|---|---|
| `templates/postgres.yaml` `init.sh`, gated on `postgres.rls.enabled` | `ALTER ROLE iam_backup BYPASSRLS` at initdb | a **fresh** install with the flag already on — which would otherwise come up with a backup that has never once succeeded |
| `files/rls.sql`, a `DO` block before `BEGIN`'s first policy | aborts if `iam_backup` exists and cannot bypass | an **existing** database, where `init.sh` never runs again |

The grant hands `iam_backup` nothing it did not have: `pg_read_all_data` already reads every row.
`iam_app` cannot grant it (superuser-only), which is exactly why the second guard is an abort
carrying the command rather than a fix-up. It is skipped entirely when the role does not exist, so a
CNPG deployment that names its backup role differently is not given a requirement it cannot meet.

**The guard, fired against the live database** (the exact `rls.sql` the hook applies, read back out
of the ConfigMap, run while the grant was removed):

```
$ kubectl get configmap -n default rediensiam-rls -o jsonpath='{.data.rls\.sql}' > cm-rls.sql
$ kubectl exec -i -n default rediensiam-postgres-0 -- psql -v ON_ERROR_STOP=1 -U iam_app -d rediensiam -f - < cm-rls.sql
BEGIN
psql:<stdin>:79: ERROR:  iam_backup cannot bypass RLS — enabling these policies would stop the nightly backup
HINT:  run as superuser, once, BEFORE this deploy: ALTER ROLE iam_backup BYPASSRLS;
```

Not one policy was created. The grant was then restored and the CronJob re-run (§5).

---

## 3. The enablement

### Two real tenants first, created through the application

`18` noted the live database had zero organisations, so a live rehearsal would have proved nothing.
Rather than seed SQL, the tenants were created **through the admin API by an authenticated
SuperAdmin**, over the real OIDC authorization-code + PKCE flow, so the rows are exactly what the
application writes:

```
  POST /api/manage/organizations              A -> 201  94177c59-8d98-4dd1-8a4b-1e6b6add59b8
  POST /api/manage/organizations/{A}/projects   -> 201  d317382d-f017-4c21-9f29-46096be4f635
  POST /api/manage/userlists/{listA}/users      -> 201  user-a@rls-proof.test
  POST /api/manage/organizations              B -> 201  4dfc5ff8-3674-4ffd-aae8-0d20d20c8e91
  POST /api/manage/organizations/{B}/projects   -> 201  7a2fa121-d9a2-48ed-9ea0-e6334ecbbdb5
  POST /api/manage/userlists/{listB}/users      -> 201  user-b@rls-proof.test
  POST /service-accounts                      A -> 201  4a568832-25a8-4b81-8491-23df7e760c77
  POST /service-accounts/{sa}/pat               -> 200  token issued
```

They are still there, so anyone can re-run §4 against them.

### The pre-enable dump

```
$ kubectl create job --from=cronjob/rediensiam-backup rls-pre-enable -n default
rediensiam-postgres:5432 - accepting connections
wrote /backup/rediensiam-20260731T175055Z.sql.gz (35303 bytes)
```

### The flag, and the deploy

`values.dev.yaml` gains `rediensiam.postgres.rls.enabled: true`. The state is in the values file,
not in `helm --set`, for the reason `18` §2 gives: carrying live state only on the command line is
the "deployed ≠ repository" drift step 12 exists to catch.

**The first attempt at `deploy/deploy.sh --dev` did not reach the cluster**, and it is worth
recording rather than glossing. It builds both SPAs before the image, and another agent's
then-in-progress work in `frontend/` did not compile:

```
──── [2/4] Build ────────────────────────────────
  Login SPA: 500K
> admin@0.2.0 build
> tsc -b && vite build
src/pages/org/OrgEmail.test.tsx(2,26): error TS6133: 'waitFor' is declared but its value is never read.
```

It stopped at the build, **before** touching the cluster, so nothing was half-applied. Since this
change is chart-only and needs no new image, and the local `:dev` tag already resolved to the digest
that was running (`sha256:a84fd895…`, the `v0.2.0` build), `deploy.sh`'s own helm invocation was run
with identical bytes — same chart, same three values files, same `--set` image arguments, same
digest. That produced **revision 19**, and everything in §4, §5 and §6 was proven against it.

That agent has since committed (`ed94a9d`), so **`deploy.sh --dev` was then run in full** and is what
produced the final state:

```
──── [4/4] Deploy ───────────────────────────────
Release "rediensiam" has been upgraded. Happy Helming!
NAME: rediensiam   STATUS: deployed   REVISION: 21

 Smoke tests:
   ✓  Health (200)      ✓  OIDC discovery (200)
   ✓  Login page (200)  ✓  Admin SPA (200)
```

**That second deploy is also the idempotency proof `rls.sql` needed.** The hook Job re-ran against a
database that already had all 19 policies, as it will on every future upgrade:

```
$ kubectl logs -n default job/rediensiam-rls
applying RLS to rediensiam on rediensiam-postgres:5432 as iam_app (sslmode=require)
BEGIN
psql:/sql/rls.sql:208: NOTICE:  RLS applied to 19 tables
COMMIT
… (19 rows, all t / t / 1)
```

No "already exists" error, no duplicate policy: `rls.sql` drops and re-creates rather than adding.
`§6`'s checks were re-run end to end against this newly built image and gave the same answers.

### The hook Job

```
$ kubectl logs -n default job/rediensiam-rls
applying RLS to rediensiam on rediensiam-postgres:5432 as iam_app (sslmode=require)
rediensiam-postgres:5432 - accepting connections
BEGIN
DO                     ← the new BYPASSRLS precondition block, passing
CREATE FUNCTION
CREATE FUNCTION
…
DO
psql:/sql/rls.sql:208: NOTICE:  RLS applied to 19 tables
COMMIT
       table_name       | rls_enabled | rls_forced | policies
------------------------+-------------+------------+----------
 audit_log              | t           | t          |        1
 backup_codes           | t           | t          |        1
 email_tokens           | t           | t          |        1
 org_roles              | t           | t          |        1
 org_smtp_configs       | t           | t          |        1
 organisations          | t           | t          |        1
 personal_access_tokens | t           | t          |        1
 projects               | t           | t          |        1
 roles                  | t           | t          |        1
 saml_idp_configs       | t           | t          |        1
 service_account_roles  | t           | t          |        1
 service_accounts       | t           | t          |        1
 user_lists             | t           | t          |        1
 user_project_roles     | t           | t          |        1
 user_social_accounts   | t           | t          |        1
 users                  | t           | t          |        1
 webauthn_credentials   | t           | t          |        1
 webhook_deliveries     | t           | t          |        1
 webhooks               | t           | t          |        1
(19 rows)
```

Every row `t / t / 1`. `FORCE` is the half that matters: `iam_app` owns these tables and a table
owner is exempt from its own RLS without it.

**No downtime, no restart.** The policies attach to an already-running database; the app pod was not
replaced by this upgrade (`rediensiam-78fbd5bc96-pb5nv`, 45 m uptime at the end of the step, i.e.
older than the enablement).

---

## 4. Cross-tenant isolation, verified directly at the database

Run as `iam_app` — the role the application connects as — with the scope set by hand, which is what
makes this a statement about the *database* rather than about the ORM.

### Read side

```
-- (1) unset - fail-closed --------------------------------------
 orgs | users | projects | lists | pats
------+-------+----------+-------+------
    0 |     0 |        0 |     0 |    0

-- (2) scoped to Org A ------------------------------------------
    Name     |    Slug            Email                   Name
-------------+-------------    -----------------------  --------
 RLS Proof A | rls-proof-a      user-a@rls-proof.test     Proj A

-- (3) scoped to Org B ------------------------------------------
    Name     |    Slug            Email
-------------+-------------    -----------------------
 RLS Proof B | rls-proof-b      user-b@rls-proof.test

-- (4) cross-tenant READ from A: ask for B by primary key -------
 b_rows_visible_from_a       0      SELECT … FROM organisations WHERE "Id" = <Org B>
 b_users_visible_from_a      0      users JOIN user_lists WHERE ul."OrgId" = <Org B>
 b_pats_visible_from_a       1      ← Org A's own PAT; from Org B the same query returns 0

-- (5) malformed / zero scope -----------------------------------
 orgs_malformed              0      SET rediensiam.org_id = 'not-a-uuid'   (no error, zero rows)
 orgs_zero_uuid              0      SET rediensiam.org_id = '00000000-…-000000000000'

-- (6) system scope ---------------------------------------------
 orgs | users | lists | system_lists
------+-------+-------+--------------
    2 |     3 |     3 |            1     ← the __system__ list (OrgId IS NULL), invisible to (2) and (3)
```

(4) is the one that matters. Asking for another tenant's row **by primary key**, on the same
connection, as the owner role, returns nothing.

### Write side — every statement below was rolled back

```
-- scoped to Org A --
(w1) INSERT INTO projects (…, "OrgId" = <Org B>, …)
     ERROR:  new row violates row-level security policy for table "projects"
(w2) UPDATE projects SET "Name"='pwned' WHERE "OrgId" = <Org B>          UPDATE 0
(w3) DELETE FROM users WHERE "Email"='user-b@rls-proof.test'             DELETE 0
(w4) UPDATE organisations SET "Active"=false WHERE "Id" = <Org B>        UPDATE 0

-- Org B, read as B, afterwards --
 Proj B  ·  user-b@rls-proof.test  ·  RLS Proof B | Active=t
```

(w1) is the `WITH CHECK` half. Without it a scoped connection could still *insert* a row into
another tenant — it simply could not read it back.

---

## 5. The backup, after

```
$ kubectl create job --from=cronjob/rediensiam-backup rls-post-enable -n default
rediensiam-postgres:5432 - accepting connections
wrote /backup/rediensiam-20260731T175824Z.sql.gz (38558 bytes)
--- 1 succeeded, 0 failed
```

**A dump that exits 0 is not yet a dump that contains anything.** Under RLS the interesting failure
is a *silently partial* dump, so the file was read off the backup PVC and searched for rows that
only exist inside policied tables:

```
$ # a throwaway reader pod on the same PVC, readOnly
/backup/rediensiam-20260731T113541Z.sql.gz   0     ← pre-dating the tenants
/backup/rediensiam-20260731T113901Z.sql.gz   0
/backup/rediensiam-20260731T132743Z.sql.gz   0
/backup/rediensiam-20260731T175055Z.sql.gz   6     ← pre-enable
/backup/rediensiam-20260731T175824Z.sql.gz   6     ← post-enable, under RLS
```

Same count either side. Both tenants' rows are in the dump taken with the policies in force.

A third run after the `NOBYPASSRLS` experiment of §2 was restored:

```
wrote /backup/rediensiam-20260731T180736Z.sql.gz (38563 bytes)
$ kubectl get cronjob rediensiam-backup -o jsonpath='{.status.lastSuccessfulTime}'
2026-07-31T18:07:39Z
```

---

## 6. The IdP still works — the same script before and after

The full admin OIDC authorization-code + PKCE flow, driven end to end, then the SuperAdmin API, a
tenant-scoped read and RFC 7662 introspection performed by a real service-account PAT. Run once
**before** enabling and once **after**, and the outputs are line-for-line identical:

```
1. GET  /oauth2/auth              -> 302 http://iam.localhost/login?login_challenge=…
2. POST /auth/login               -> 200 {"requires_mfa":true}
2b. POST /auth/mfa/totp/verify    -> 200
3. consent hops                   -> 303 code at http://localhost:30501/admin/callback: yes
4. POST /oauth2/token             -> 200 access_token (1248 chars)
6. GET  /api/manage/organizations -> 200 ['RLS Proof A', 'RLS Proof B']
7. GET  org A projects            -> 200 ['Proj A']
8. GET  org A users               -> 200 ['user-a@rls-proof.test']
9.  POST /api/introspect (PAT)     -> 200 active=True org_id=94177c59-… client_id=None
10. POST /api/introspect wrong aud -> 200 active=False
```

Line 9 is the one that exercises RLS as designed: the caller is a PAT belonging to Org A's service
account, so the interceptor scopes that connection to `94177c59-…` and the policies are actually in
the query plan. Line 10 is the audience binding still refusing to answer for Org B.

Two notes on how this was driven, because both are dev-only artefacts and neither is a finding:

- **The session cookie is `CookieSecurePolicy.Always`** (`Program.cs:80`) and dev is plain HTTP, so a
  normal cookie jar will not send it back. A browser on a `*.localhost` origin does, because
  localhost is a trustworthy origin. The driver tracks cookies by hand to reproduce that. Nothing on
  the server was relaxed.
- **`RequireAdminMfa` is on**, so the first login returned `{"requires_mfa_setup":true}` and a TOTP
  factor had to be enrolled to reach the console. That enrolment was **reverted afterwards** — the
  bootstrap superadmin is back to no factor and no backup codes, exactly as before, and the next
  admin login prompts for a fresh enrolment. The generated secret was shredded and is recorded
  nowhere. (Incidentally: that revert had to run as `'system'`, because the bootstrap user lives in
  the `__system__` list whose `OrgId IS NULL` makes it invisible to every tenant scope — the
  `ServiceAccountController.cs:29-33` bug class, closed in the schema.)

---

## 7. Dragonfly TLS in the production values — **untested live**

### The change

`values.prod.yaml` gains, next to the Postgres TLS block it already had:

```yaml
  dragonfly:
    local:
      tls:
        enabled: true
```

with a comment on the flag itself covering the three things prod has to plan for and dev did not:
the atomic DSN half, the unavoidable session loss, and the surviving-key-ring case.

### What was verified

**That the reader `deploy.sh` uses now sees it.** This is load-bearing — `dragonfly.local.tls.enabled`
is not a unique key (there are three `tls:` blocks in these files), so `deploy.sh` cuts the block out
by indentation before matching, and an added block at the wrong indent would have been silently
invisible:

```
$ cache_tls_in rediensiam/values.yaml       -> CACHE_TLS false
$ cache_tls_in rediensiam/values.dev.yaml   -> CACHE_TLS true
$ cache_tls_in rediensiam/values.prod.yaml  -> CACHE_TLS true     ← was false
```

**That the render produces what dev has live**, and that the three cutover guards still fire in every
direction for the prod pair (a real prod deploy layers the gitignored secrets file, so the guards —
gated on a non-empty `cacheUrl` — are judged then; here that layer is simulated with `--set-string`):

```
D. prod, tls ON  + cleartext DSN   -> execution error … cacheUrl has no `ssl=true` — Dragonfly stops
                                      answering cleartext, so both must change in the SAME upgrade
E. prod, tls ON  + ssl=true DSN    -> ok
   dragonfly args: ['--logtostderr','--requirepass=$(DRAGONFLY_PASSWORD)','--tls',
                    '--tls_cert_file=/etc/dragonfly-tls/tls.crt','--tls_key_file=/etc/dragonfly-tls/tls.key']
   app mount:      [{'name':'cache-tls-ca','mountPath':'/etc/cache-tls','readOnly':True}]
   Certificate:    t-dragonfly-tls -> t-dragonfly-tls
F. prod, tls OFF + ssl=true DSN    -> execution error … the cache serves no TLS and the app will not connect
   (with no password at all, the pre-existing guard fires first: "Dragonfly refuses to start with
    TLS and no authentication method")
```

### What was **not** verified

**There is no production cluster.** Nothing above was executed against one. This is `helm
lint`/`helm template` plus reasoning from the dev cutover in `23`. It is **not proven**, and the
following are the specific ways it could still go wrong on first contact:

| Risk | Why rendering cannot catch it |
|---|---|
| cert-manager absent or its `selfsigned` ClusterIssuer not reconciling in that cluster | the `Certificate` renders; whether a Secret appears is the cluster's answer, not the chart's |
| The reuse-path DSN edit on an existing prod secrets file | `deploy.sh` stops and prints the `sed`, which is correct — but it is a stop, i.e. an operator step in the middle of a deploy |
| The session loss (below) landing during traffic | a scheduling decision no template can make |
| A prod Dragonfly with persistence, or upgraded separately | see the key-ring pre-step |

### The cost, which is not avoidable by ordering

The Dragonfly pod flips to `--tls` immediately while the Deployment keeps the **old** app pod
serving, so that pod loses its cache for the ~30 s before it terminates. Dragonfly has no PVC, so the
restart empties the DataProtection key ring — **every session is invalidated**. `18` predicted this
and `23` observed it. In prod it is the price of the change.

### The key-ring pre-step, folded in so it cannot be missed

`23`'s residuals recorded it in a report only. It is now in three places an operator upgrading prod
actually passes through:

1. **`docs/DEPLOYMENT.md` → "Turning cache TLS on in production"** — a numbered runbook, with the
   command, the reason, and when it does *not* apply.
2. **`values.prod.yaml`, on the flag itself** — item 3 of the comment block you must read to set it.
3. **The application's own exception**, already there since `23`: `EncryptedOnlyXmlRepository` refuses
   an unprotected key with `DEL rediensiam:dataprotection:keys` in the message.

The condition, stated precisely because "always run DEL" would be wrong: the ring only survives if
Dragonfly does not restart. A cutover done in one upgrade restarts it, so the ring is gone anyway and
the pre-step is a no-op. It matters when the cache is upgraded separately, or has been given
persistence, or when a release upgrades onto the step-23 build **without** turning TLS on. In those
cases the old ring is stored in cleartext, and the new build refuses it rather than adopting it —
because a plaintext key in a shared cache can be *planted*, and it mints session cookies. Refusing is
correct; the 500 is the cost of not having read this.

No deploy-time check was added for it. The precise trigger ("this upgrade will not restart the
cache") is also true of every steady-state redeploy, so a check on it would print on every deploy of
an already-cut-over cluster — and a warning an operator learns to scroll past is worse than the three
places above.

---

## 8. `verify-deployment.sh` V-25

It flipped from a skip to a pass on its own — the assertion was already written for the flag being
on. What changed is that it now reports **evidence from the process** rather than a status field, and
says what it cannot see:

```
before:   --    V-25      postgres.rls.enabled is off — tenant isolation is application-side only (S-5 phase 2 open)
after:    PASS  V-25      RLS applied as the table owner — RLS applied to 19 tables
```

The line is read out of the hook Job's own log, which `rls.sql` prints only after its coverage gate
has passed on every table in `public`. The comment above the check now states the residual plainly:
this is a statement about the last **apply**, not about the database right now. Someone who runs the
rollback `DO` block by hand leaves V-25 passing. Closing that needs a live `pg_policies` count, which
needs a database password, and this script's discipline — V-20, V-21, V-23, V-26 — is that it never
reads one. ~1 h via a `/health/detail` field, not done.

---

## 9. Gate output

### `helm lint`

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
exit=0  48 documents, 2230 lines
(no stderr)
YAML parses: 48 objects

########## helm template values.yaml + values.prod.yaml ##########
exit=0  48 documents, 1931 lines
(no stderr)
YAML parses: 48 objects
```

Dev 46 → 48 (the RLS ConfigMap and hook Job). Prod 47 → 48 (the Dragonfly `Certificate`).

Also rendered clean, though neither is shipped: **prod with `rls.enabled=true`** (3 RLS objects, the
`BYPASSRLS` grant present in `init.sh`), and **CNPG mode with `rls.enabled=true`** (StatefulSet and
CronJob gone, hook Job still rendered — it parses the app DSN, so it works against an external
cluster unchanged).

### `./deploy/verify-deployment.sh --dev`

```
 RediensIAM control verification — dev — 2026-07-31T20:23:…+02:00
  …
  PASS  V-20      pg_hba.conf grants no 'trust' (all methods are scram-sha-256)
  PASS  V-21      no component connects as superuser 'iam' (users:iam_app iam_hydra iam_keto)
  PASS  V-22      backup CronJob last succeeded 2026-07-31T18:07:39Z
  PASS  V-23/server Postgres runs with ssl=on
  PASS  V-23/hba  pg_hba.conf admits TLS only (hostssl; local socket unaffected)
  PASS  V-23/dsn  app, hydra and keto DSNs all request TLS
  PASS  V-24      cache requires a password (48 chars)
  PASS  V-26/server Dragonfly runs with --tls (cleartext is refused, not merely unused)
  PASS  V-26/dsn  app cache DSN requests TLS (ssl=true)
  PASS  V-26/pin  app pinned the cache certificate — server certificate pinned to 1 root(s) from '/etc/cache-tls/ca.crt'.
  PASS  V-25      RLS applied as the table owner — RLS applied to 19 tables
───────────────────────────────────────────────────────────────
 35 passed · 0 failed · 2 skipped
 All asserted controls are live.
EXIT=0
```

34 → 35 passed, 3 → 2 skipped (V-25 stopped skipping), **0 failed**.

```
$ kubectl get pods -n default        # after `deploy.sh --dev`, revision 21
rediensiam-5b5f84747d-7ktnf             1/1   Running     0   2m12s
rediensiam-dragonfly-588d568655-jqpzm   1/1   Running     0   148m
rediensiam-hydra-75b7fc79b4-9v4tq       1/1   Running     0   5h1m
rediensiam-keto-754dc4c55d-792qd        1/1   Running     0   5h1m
rediensiam-postgres-0                   1/1   Running     0   5h35m
rediensiam-rls-bcj9z                    0/1   Completed   0   2m     ← the helm hook Job; V-25 reads its log
```

Identical `35 / 0 / 2` on revision 19 (chart-only, `v0.2.0` image) and on revision 21 (full
`deploy.sh` rebuild).

### Suite

```
$ dotnet test tests/RediensIAM.IntegrationTests/RediensIAM.IntegrationTests.csproj \
      -p:SonarQubeTargetsImported=true --nologo

Passed!  - Failed: 0, Passed: 1346, Skipped: 0, Total: 1346, Duration: 3 m 38 s
```

**1346, unchanged.** No test was added and none needed to change: everything in this step is chart
and documentation, and `21` had already pinned the application contract with 14 tests.

---

## 10. Files changed

| File | Change |
|---|---|
| `deploy/rediensiam/values.dev.yaml` | `postgres.rls.enabled: true`, with why the prerequisite is now enforced |
| `deploy/rediensiam/values.prod.yaml` | `dragonfly.local.tls.enabled: true`, with the three-part cutover cost including the key-ring pre-step |
| `deploy/rediensiam/values.yaml` | the `rls` comment no longer says "must stay off"; states the honest limit and where the prerequisite is enforced. Default still `false` |
| `deploy/rediensiam/templates/postgres.yaml` | `init.sh` grants `BYPASSRLS` at initdb when `rls.enabled` |
| `deploy/rediensiam/files/rls.sql` | precondition `DO` block: refuses to create a policy if `iam_backup` cannot bypass |
| `deploy/verify-deployment.sh` | V-25 reports the Job's own `RLS applied to N tables` line; comment states the apply-time-only residual |
| `docs/DEPLOYMENT.md` | "Turning RLS on" rewritten as performed; new "Turning cache TLS on in production" with the `DEL` pre-step; two Known-gaps rows corrected |
| `docs/SECURITY.md` | RLS moved to "what exists" for dev with the prod caveat; the honest limit kept and given the measured 5-vs-15 scope split; two §8 ledger rows corrected |

Nothing in `src/`, `tests/`, `frontend/` or `sdk/`.

---

## 11. What is left, with its cost

| Item | Why | Cost |
|---|---|---|
| **RLS does not protect the login path** | login resolves a user by e-mail before any tenant exists; the `Projects` row it needs must itself be read unscoped. Measured at 15 unscoped connections to 5 scoped on tenant-exercising traffic | not fixable at this layer. A tenant-bearing pre-auth route (host- or path-derived org) is a product change — days, and out of proportion |
| **RLS is off in prod** | on an existing prod database it is a migration, not an upgrade, and there is no prod cluster to rehearse on | one flag plus §3's sequence. The two guards mean the worst case is now a failed deploy with the fix in the message, not a stopped backup |
| **Dragonfly TLS in prod is untested live** | no production cluster exists | first prod deploy. Budget the session loss and read `DEPLOYMENT.md` first |
| **V-25 asserts the last apply, not the current state** | a live `pg_policies` count needs a password, and this script never reads one | ~1 h via a `/health/detail` field |
| ~~`deploy.sh --dev` blocked by a frontend build error~~ | resolved during this step — that agent committed `ed94a9d` and the full script deploy then succeeded (revision 21) | none |
| **CHANGELOG 0.2.0 still says "RLS shipped off" and "prod inherits `enabled: false`"** | both were true at the tag, and a released section should not be rewritten | an `Unreleased` section at the next release |
| **`14-finding-ledger.md` is stale on R-15 and S-5** | it is a mid-audit snapshot and `SECURITY.md` already says so in its header | leave it, or fold into the next release report |
| **The two synthetic tenants are still in the dev database** | they are the evidence for §4 and let anyone re-run it | `RLS Proof A` / `RLS Proof B`, `*@rls-proof.test`. Delete when done |
| **Pin is to the leaf, no revocation, no mTLS, cert renewal needs an app restart** | unchanged from `23` | as costed there; the `Issuer`/`ClusterIssuer` with a stable CA (~4 h) closes this and Postgres `verify-full` together |

### Credential note

No secret was written to a tracked file. Every credential in this document was read from
`rediensiam-secrets` or the gitignored `values.secret.yaml` into a shell variable and masked on
output; DSNs and passwords appear as `***`. Statement logging was enabled for two short windows and
reverted both times, and Npgsql binds every value, so no credential entered the SQL text. The
bootstrap e-mail is redacted throughout. The TOTP secret, access token and PAT that the login proof
generated were held in a mode-600 scratchpad and `shred`ed; the TOTP enrolment itself was reverted on
the database. The `NOBYPASSRLS` experiment in §2 was restored and re-proven in the same minute.
