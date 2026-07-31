# Step 10 — Secrets Management

Scope: `deploy/` and repo-root tooling. Nothing under `src/`, `frontend/`, `sdk/` or `tests/` was
edited. Two files under `tests/` had their **mode** changed and nothing else — see §3.3; no content
changed, nothing tracked changed, the 1198-test baseline is untouched.

Builds on step 9. Nothing step 9 did was reverted.

---

## 0. The one thing to read if you read nothing else

**Three live SonarQube tokens were exposed at rest on this workstation. Two of them have been
removed from the file. That does not un-leak them.** A token is revoked when the SonarQube server
stops accepting it, not when the local copy is deleted. All three must be revoked and reissued by
a human — §2 and §7.1.

The good news, verified rather than assumed: they were **never committed**. `git log --all --
.sonar.env` is empty, no file matching a secret-file pattern was ever tracked, and the only
`sqp_` hits in history are the audit report's own prose describing the finding (commit `0144e1c`).
So the fix is revocation for at-rest exposure, not history rewriting.

---

## 1. R-06 — hard-coded dev credentials (CVSS 8.9)

### 1.1 What "fixed" means, and what it does not

The finding is a **default-credential** risk, not a leak. `values.secret.yaml` is gitignored and
absent from history. What makes it 8.9 is that the values were the *same on every machine that
ever copied the file*: `Password=changeme`, `bootstrapPassword: "Admin1234!"`, a literal HKDF root
key, and `CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`. An attacker who reaches a dev cluster does not
need to steal anything — they already know the super-admin password and the key that decrypts every
TOTP secret, webhook secret and per-org SMTP password.

So the fix has to remove the *predictability*, not the file.

### 1.2 What was implemented: generate per install, for both environments

`deploy/deploy.sh` §1b was rewritten. `--prod` already generated its own secrets
(`openssl rand`); that generator is now a function, `write_secrets_file`, and **`--dev` uses it
too**. A fresh clone that has never run the script has no secrets file; the one it gets is unique
to the machine that ran it.

Dev is **not prompted**. It generates a bootstrap password and prints it once:

```
──── [1b/4] Secrets ─────────────────────────────
  Generated /…/deploy/rediensiam/values.secret.yaml (mode 600)

  ┌─ DEV BOOTSTRAP ADMIN — unique to this machine, shown once ──────────
  │  email:    admin@dev.local
  │  password: NiBEIgc9R4A5iby7LmoMUzDHZIPqKH8Aa1!
  └─ also in /…/values.secret.yaml; re-roll with --dev --rotate-secrets
```

`./deploy/deploy.sh --dev` on a fresh clone is still exactly one command, and the operator invents
nothing.

Two fixes rode along in the same function, both real:

- **The Argon2 pepper was generated and then thrown away.** `deploy.sh:127` computed
  `ARGON_PEPPER=$(openssl rand -hex 32)` and the heredoc never referenced it, so
  `rediensiam.security.argon2Pepper` stayed `""` and **prod ran with no server-side pepper at all**
  — `PasswordService.cs:13-15` falls back to an empty pepper silently. It is now written to the
  file and reaches `Security__Argon2Pepper`.
- **The operator-chosen prod password was interpolated into a double-quoted YAML scalar.** A
  password containing `"`, `\` or `$` produced a broken or wrong value. It now goes into a
  single-quoted scalar with `'` doubled. Verified against `p"a$s\w'd\`x` — see §6.3.

### 1.3 Fail-closed, gated by environment

A `KNOWN_DEFAULTS` regex (`changeme|CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS|Admin1234!`) is checked
against an existing secrets file on every run:

| environment | behaviour |
|---|---|
| `--prod` | **hard failure**, non-zero exit, deploy does not happen |
| `--dev`  | loud warning naming the one-command remedy; deploy proceeds |

Prod fails closed because there is no scenario in which shipping a publicly-known credential to
production is the operator's intent. Dev only warns because rule 5 — dev must still work with one
command — and because on an existing checkout a hard failure would break the working dev cluster
of the person doing the remediation. Both paths print the same fix.

### 1.4 `--rotate-secrets`, and why it is dev-only

```bash
./deploy/deploy.sh --dev --rotate-secrets
```

Deletes the secrets file, uninstalls the release, deletes `data-rediensiam-postgres-0`, then
regenerates. It refuses on `--prod` with a pointer to §4.

The refusal is not caution, it is arithmetic. Rotating the DB password, the HKDF root and the Hydra
system secret in one step invalidates everything derived from them simultaneously: the Postgres
data directory keeps the **old** password (`POSTGRES_PASSWORD` is an initdb-time value, ignored on
an existing volume), every AES-GCM ciphertext in the database becomes undecryptable, and every
Hydra token and consent session dies. The app has no key-versioned ciphertext and no re-encrypt
pass, so on real data the only coherent form of "rotate everything at once" is a full state reset.
Dev data is disposable; prod data is not. Prod rotation is per-secret and ordered — §4.

### 1.5 Why the other options lose

**Fail closed in non-dev only.** Does not fix the finding. The exposure scenario named in the scan
is *a dev values file reaching a routable host* — a shared dev cluster or a mistaken `--dev` deploy.
Gating on environment leaves the risk exactly where it actually lives. It is also unenforceable
from where I am allowed to work: the app-side check would go in `Program.cs`, and `src/` is frozen.
Kept as the *secondary* control in §1.3, not as the fix.

**Force a rotation on first login.** The right long-term control, and out of reach this step. There
is no `MustChangePassword` on the user entity — verified by grep, zero hits for
`MustChange|ForcePasswordChange|RequirePasswordChange` across `src/` — so it needs a column, a
migration, a branch in the login flow, and a set-password route. All in `src/`, frozen and verified
at 1198 passing. It is also *partial*: it addresses one of the four values and leaves the HKDF root
key, the DB password and the Hydra system secret — the three with the worst blast radius — exactly
as predictable as before. Recommended for a later step; not a substitute for generation.

**Prompt the operator for a dev password.** Rejected for the reason the brief gives: a dev password
that must be invented before anything runs is a password that ends up in a shell history or a
README, which is the same finding with more steps.

### 1.6 Honest status

R-06 is closed **structurally**: this repository can no longer produce a default credential in
either environment. It is *not* yet closed **on this workstation** — the existing
`deploy/rediensiam/values.secret.yaml` still holds `changeme`, `Admin1234!` and the `CHANGE_ME…`
Hydra secret, and rotating it destroys the running dev database. That is a deliberate operator
decision, not something a deploy script should make silently. One command: §7.2.

---

## 2. R-08 — live SonarQube tokens in cleartext (CVSS 6.1)

### 2.1 Git history: clean, verified

```
$ git log --all --oneline -- .sonar.env
(empty)
$ git ls-files --error-unmatch .sonar.env
error: pathspec '.sonar.env' did not match any file(s) known to git
$ git log --all -S 'sqp_' --oneline
0144e1c docs: security audit reports        ← the audit report's prose, not a token
```

The single `-S 'sqp_'` hit is `.security-hardening/01-vulnerability-scan.md` describing this very
finding. No token value was ever committed. **Therefore the fix is rotation, not history
rewriting** — no `filter-repo`, no force-push, nothing for downstream clones.

### 2.2 What changed

- **`.sonar.env` is now `600`.** It was `664` — every local account and every process running as
  this user could read it.
- **Two of the three entries were removed.** `SONAR_TOKEN_ADMIN` and `SONAR_TOKEN_LOGIN` name
  projects that `docs/ARCHITECTURE.md:227` says were deleted server-side, and nothing reads them:
  `sonar-scan.sh` consumes only `SONAR_TOKEN`, falling back to `SONAR_TOKEN_API`. They were two
  extra copies of a credential at rest with no consumer. `SONAR_TOKEN_API` was kept because the
  scanner needs it until the operator rotates.
  The rewrite was done under `umask 077` via a temp file, so the plaintext never existed
  world-readable, not even briefly.
- **`sonar-scan.sh` repairs the mode on load.** The save path already did `chmod 600`; a file
  written *before* that existed did not, and sourcing it is the one moment the script is guaranteed
  to be looking at the file:

  ```bash
  mode=$(stat -c %a "$ENV_FILE" 2>/dev/null || echo 600)
  if [[ "$mode" != "600" ]]; then
    echo "warning: $ENV_FILE was mode $mode — tightening to 600 (R-08)" >&2
    chmod 600 "$ENV_FILE"
  fi
  ```
- **The save path now sets `umask` before the redirect** rather than `chmod`-ing after, closing the
  window in which the token sits on disk under the caller's umask.

### 2.3 Stated plainly

**A file edit does not un-leak a token.** Deleting `SONAR_TOKEN_ADMIN` and `SONAR_TOKEN_LOGIN` from
`.sonar.env` removed two copies from this disk; it did nothing to the SonarQube server, which will
still accept all three until a human revokes them. Anything that read them during the exposure
window — any local process, any backup, any editor swap file, any shell that sourced the file —
still has them. Revocation is the fix. §7.1.

---

## 3. R-07 — generated prod secrets file permissions (CVSS 5.5)

### 3.1 Verifying step 9's `umask 077` end to end

Step 9 added `umask 077` before the heredoc. **It covers the creation path and only the creation
path.** Two gaps, both real:

1. **`umask` affects file *creation*, not existing files.** `cat > "${SECRETS_FILE}"` on a file that
   already exists truncates it and preserves its mode. The block was guarded by
   `[ ! -f "${SECRETS_FILE}" ]` so this could not fire *there* — but it means any file created by an
   older version of the script keeps its `-rw-r--r--` forever, and the script never looked at it
   again. That is the "path where the file is rewritten later" the brief asks about: not a rewrite
   inside `deploy.sh`, but every subsequent *reuse* of a file whose mode was set once, wrongly.
2. **The bare `umask 077` leaked into the rest of the script.** It was not scoped, so it silently
   narrowed the mode of anything created after it for the remainder of the run. Harmless today
   (nothing else is created), a latent surprise tomorrow.

### 3.2 What changed

- The `umask` is now scoped to a subshell around the heredoc: `( umask 077; cat > "${file}" <<EOF … )`.
- `chmod 600` runs **unconditionally** after the write, so the mode does not depend on the caller's
  umask at all.
- The **reuse** path — file already exists, both environments — now also runs `chmod 600` and prints
  the resulting mode. This is what closes gap 1.

### 3.3 The rest of the working-tree secret inventory

The scan's table S-1…S-6 listed six files. All are now `600`:

| ID | file | before | after |
|---|---|---|---|
| S-1 | `.sonar.env` | 664 | 600 |
| S-2 | `deploy/rediensiam/values.secret.yaml` | 664 | 600 |
| S-3 | `deploy/rediensiam/values.prod.secret.yaml` | n/a (absent) | 600 on creation, enforced |
| S-4 | `tests/e2e/.env` | 664 | 600 |
| S-5 | `tests/e2e/.auth/admin-session.json` | 664 | 600 |
| S-6 | `deploy/rediensiam/values.dev.yaml.bak` | 664, untracked | **deleted** (I-04) |

S-4 and S-5 live under `tests/`, which I was told not to touch. I changed their **mode** and
nothing else: no content edit, no tracked file, no effect on any test. Flagging it rather than
burying it — if that crosses the line, `chmod 664` on both restores the prior state exactly.

---

## 4. Secret rotation

Step 3 §7.3 item 6 is the finding behind this section: the three highest-sensitivity categories have
no rotation implementation, and that is what makes chain **C-3** unrecoverable — "the only viable
recovery is a global signing-key rotation… plus manual TOTP re-enrolment across the whole user
base. No code path exists for either."

That is still true for one of the seven categories below, and the honest thing is to say which.

| # | secret | rotatable without data loss? | implemented here |
|---|---|---|---|
| 4.1 | Hydra system secret | **yes** — native, non-destructive | chart supports it; runbook below |
| 4.2 | Database credentials | yes, with a brief ordered window | runbook |
| 4.3 | PATs | yes — the API already does it | runbook |
| 4.4 | Hydra client secrets | yes | runbook |
| 4.5 | SonarQube token | yes | runbook |
| 4.6 | Argon2 pepper | **no** — invalidates every password hash | runbook + honest cost |
| 4.7 | HKDF root (`encryptionKey`) | **no** — this is C-3 | runbook + honest cost |
| — | dev, all of the above at once | yes, by discarding state | **`deploy.sh --dev --rotate-secrets`** |

### 4.1 Hydra system secret — the one that rotates cleanly

Hydra's `secrets.system` is a list, and the semantics are exactly what a rotation needs: **only the
first entry encrypts; every entry decrypts.** Existing data is not re-encrypted, so the old key must
stay in the list until everything encrypted under it has expired.

The chart already passes a list, so no template change was needed. The generator now carries a
comment stating the semantics so the next person does not replace the list instead of prepending to
it.

```bash
# 1. generate the new key
NEW=$(openssl rand -hex 32)

# 2. PREPEND it. Do not replace — dropping the old key invalidates every token,
#    consent session and login session encrypted under it, immediately.
#    In values.prod.secret.yaml:
#      hydra:
#        hydra:
#          config:
#            secrets:
#              system:
#                - "<NEW>"          # first entry = the encryption key
#                - "<OLD>"          # decrypt-only, keep until step 4

# 3. roll it out
./deploy/deploy.sh --prod

# 4. verify Hydra came up and is minting tokens under the new key
kubectl logs -n default deploy/rediensiam-hydra --tail=50 | grep -iv 'level=debug'
curl -s https://auth.rediens.net/.well-known/openid-configuration | head -c 200

# 5. WAIT OUT the longest thing encrypted under the old key. With the TTLs set in
#    values.yaml that is refresh_token: 168h — seven days. Then drop the old entry
#    from the list and redeploy.
```

**Caveat, and it is the one that bites.** The chart does not set `secrets.cookie`. Ory's
documentation is explicit that `secrets.system` is used as the fallback for cookie encryption when
`secrets.cookie` is unset, so rotating `system` also rotates the login/consent cookie key. Because
the old key stays in the list for decryption during the window, in-flight flows survive; they do
**not** survive if you skip step 2 and replace instead of prepend. If you want the two rotations
independent, set `hydra.hydra.config.secrets.cookie` explicitly *first*, on its own deploy, before
ever touching `system`.

### 4.2 Database credentials

`iam` is a single Postgres role shared by the app, Hydra and Keto — which is architecture review
§5.3's open item and the reason this rotation is a three-DSN change rather than one.

```bash
NEWPW=$(openssl rand -hex 20)

# 1. change the role. Postgres applies this immediately to NEW connections;
#    existing pooled connections keep working, which is what makes the window survivable.
kubectl exec -n default rediensiam-postgres-0 -- \
  psql -U iam -d postgres -c "ALTER ROLE iam WITH PASSWORD '$NEWPW';"

# 2. update ALL FOUR places in values.prod.secret.yaml, in one edit:
#      rediensiam.secrets.databaseUrl      Host=…;Password=<NEWPW>
#      rediensiam.postgres.local.password  <NEWPW>
#      hydra.hydra.config.dsn              postgres://iam:<NEWPW>@…/hydra?sslmode=disable
#      keto.keto.config.dsn                postgres://iam:<NEWPW>@…/keto?sslmode=disable

# 3. redeploy — rolls app, Hydra and Keto onto the new credential
./deploy/deploy.sh --prod

# 4. verify all three reconnected
kubectl get pods -n default -l app.kubernetes.io/instance=rediensiam
kubectl exec -n default rediensiam-postgres-0 -- \
  psql -U iam -d postgres -c "select application_name, state from pg_stat_activity where usename='iam';"
```

`rediensiam.postgres.local.password` feeds `POSTGRES_PASSWORD`, which Postgres reads **only at
initdb**. On an existing volume it is ignored — the `ALTER ROLE` in step 1 is what actually changes
the credential. Keeping the value in sync matters only for a future re-init; skipping step 1 and
editing values alone rotates nothing.

### 4.3 PATs

Already fully supported. `Cache:PatTtlMinutes` is clamped to ≤15 (`AppConfig.cs:25`), so a
revocation takes effect within 15 minutes at worst; `Security:MaxPatLifetimeDays` is clamped to
≤730 with a default of 365 (`AppConfig.cs:64`), so no PAT is permanent by construction.

```bash
ADMIN=https://auth.ts.rediens.net       # Tailscale-only admin ingress
TOK=<management access token>
SA=<service-account uuid>

# 1. list what exists
curl -s -H "Authorization: Bearer $TOK" "$ADMIN/service-accounts/$SA/pat" | jq .

# 2. issue the replacement — the raw token is returned EXACTLY ONCE
curl -s -X POST -H "Authorization: Bearer $TOK" -H 'Content-Type: application/json' \
  -d '{"name":"ci-2026-07","expires_at":"2026-10-30T00:00:00Z"}' \
  "$ADMIN/service-accounts/$SA/pat" | jq -r .token

# 3. deploy the new token to its consumer, confirm traffic, THEN revoke the old one
curl -s -X DELETE -H "Authorization: Bearer $TOK" \
  "$ADMIN/service-accounts/$SA/pat/<old-pat-uuid>"
```

Both mutations are audited (`sa.pat.created`, `sa.pat.revoked`).

### 4.4 Hydra client secrets

No rotate endpoint exists in the app — `HydraService` has create/update-scope/delete but no
secret rotation — so this goes through Hydra's Admin API. `:4445` is ClusterIP and locked down by
NetworkPolicy since step 9, so port-forward:

```bash
kubectl port-forward -n default svc/rediensiam-hydra-admin 4445:4445 &

CLIENT=<client_id>
NEWSEC=$(openssl rand -hex 32)

# read current config first — PUT replaces the whole object
curl -s "http://127.0.0.1:4445/admin/clients/$CLIENT" | jq . > /tmp/client.json

# patch just the secret (JSON Patch, so nothing else is disturbed)
curl -s -X PATCH "http://127.0.0.1:4445/admin/clients/$CLIENT" \
  -H 'Content-Type: application/json' \
  -d "[{\"op\":\"replace\",\"path\":\"/client_secret\",\"value\":\"$NEWSEC\"}]" | jq .

kill %1
```

There is **no dual-secret window** in OAuth2 client credentials — the moment the new secret lands,
the old one stops working. Deploy the new secret to the consumer in the same maintenance step.
Public clients (`token_endpoint_auth_method=none`), which is what `client_admin_system` is, have no
secret to rotate.

### 4.5 SonarQube token

```bash
# 1. revoke server-side. This is the only step that matters — do it FIRST.
#    UI: <sonar-host>/account/security  →  Revoke on each token
#    or:
curl -s -u "<admin-token>:" -X POST "$SONAR_HOST/api/user_tokens/revoke" --data-urlencode "name=<token-name>"

# 2. issue one replacement (one token, not three — the other two named deleted projects)
curl -s -u "<admin-token>:" -X POST "$SONAR_HOST/api/user_tokens/generate" \
  --data-urlencode "name=rediensiam-scan-2026-07" | jq -r .token

# 3. store it — sonar-scan.sh reads SONAR_TOKEN, falling back to SONAR_TOKEN_API
( umask 077; printf 'SONAR_TOKEN=%s\n' '<new-token>' > .sonar.env )
chmod 600 .sonar.env

# 4. confirm the old one is dead
curl -s -o /dev/null -w '%{http_code}\n' -u "<old-token>:" "$SONAR_HOST/api/authentication/validate"
```

### 4.6 Argon2 pepper — rotatable, but every password hash dies

`PasswordService.cs:118-130`: when a pepper is set, the password is `HMACSHA256(pepper, password)`
before Argon2. The pepper is **not** stored per-hash, so a changed pepper makes every existing
`$argon2id$…` hash unverifiable. There is no compatibility path — this is not an oversight to route
around, it is what a pepper is.

Backup codes are the exception and they show the pattern the rest of the system should copy:
`HashBackupCode` emits `sha256:{keyId}:{hex}` where keyId is `p` or `0`
(`PasswordService.cs:50-60`), so a hash produced under a different key is *rejected* rather than
silently mis-verified.

```
Rotating the pepper on live data means, in order:
  1. announce a password reset to every user of every tenant;
  2. deploy the new pepper;
  3. every login fails; every user goes through the forgot-password flow.
```

**Do this only in response to a pepper compromise**, and note that a pepper compromise alone does
not let an attacker log in — it lets them run a faster offline attack against a stolen hash dump.
Weigh that against a forced global password reset before pulling the trigger.

**To make it rotatable properly** (not done here — `src/`): store the keyId in the hash prefix the
way `HashBackupCode` already does, keep the old pepper as a decrypt-only fallback, and re-hash on
next successful login. That converts a global outage into a silent migration. Roughly a day.

### 4.7 HKDF root `encryptionKey` — this is C-3, and it is still unrecoverable

`AppConfig.cs:100-103` derives five independent subkeys by HKDF-SHA256 from one 32-byte root:
`rediensiam-totp-secret-v1`, `-webhook-secret-v1`, `-smtp-password-v1`, `-theme-secret-v1`,
`-device-fingerprint-v1`. `TotpEncryptionService` writes raw AES-GCM with **no key id and no
version byte**. There is no way to tell, from a ciphertext, which key produced it.

Rotating the root therefore destroys, atomically and without warning:

| data | consequence | recovery |
|---|---|---|
| TOTP secrets | every MFA user locked out | manual re-enrolment, every user, every tenant |
| Webhook signing secrets | every tenant's webhook signatures fail | `POST /webhooks/{id}/rotate-secret` per webhook, then update every receiver |
| Per-org SMTP passwords | outbound mail dies per org | org admin re-enters each one |
| Theme secrets | tenant login themes fail to decrypt | re-enter |
| Device fingerprints | every device reads as new | self-heals; users get one new-device notification |

Webhooks are the one category with a real rotation endpoint already
(`WebhookController.cs:120`) — a working model for what the others need.

**The runbook is honest about what it is: a disaster procedure, not a maintenance procedure.**

```bash
# ONLY on confirmed compromise of the root key. Budget a maintenance window and
# an all-tenant notification BEFORE step 1.
# 1. notify every tenant admin: MFA re-enrolment required, webhook secrets rotating,
#    SMTP credentials must be re-entered.
# 2. NEW=$(openssl rand -hex 32); set rediensiam.secrets.encryptionKey in values.prod.secret.yaml
# 3. ./deploy/deploy.sh --prod
# 4. clear the now-undecryptable ciphertexts so the app fails loudly, not silently:
#      users.totp_secret_enc, webhooks.secret_enc, org_smtp.password_enc, themes' secret columns
#    (exact column names: check the EF model before running any UPDATE)
# 5. re-run webhook secret rotation per webhook via POST /webhooks/{id}/rotate-secret
# 6. support burden: every MFA user re-enrols.
```

**The structural fix, unchanged from architecture review §7.3 item 6 and still not implemented:**
prefix ciphertexts with a key id (copy the backup-code format), keep the previous root as a
decrypt-only fallback, and add a background re-encrypt pass. Until that exists, C-3's recovery plan
remains "re-enrol every MFA user in every tenant by hand". Roughly two days in `src/`, and it is the
single highest-value item left in this whole pipeline for operational survivability.

### 4.8 Suggested cadence

| secret | cadence | on compromise |
|---|---|---|
| PATs | ≤365d, enforced by the clamp | immediate, no user impact |
| Hydra client secrets | annual | immediate; brief consumer outage |
| DB credentials | annual | immediate; §4.2 is a rolling change |
| Hydra system secret | annual, with the 7-day overlap | immediate; §4.1 |
| SonarQube token | annual | immediate; no production impact |
| Argon2 pepper | **never on schedule** | only on pepper compromise; §4.6 |
| HKDF root | **never on schedule** | only on root compromise; §4.7 |

The bottom two rows are the point. A secret you cannot rotate on schedule is a secret you must
protect on the assumption you will never get to rotate it — which is precisely why §5 and step 9's
network work matter more here than they would in a system with a working re-key path.

---

## 5. Least-privilege service identity

### 5.1 The finding: a controller with cluster-wide read of every Secret, for nothing

`hydra-maester` was rendering and **is running right now**:

```
$ kubectl get pods -n default | grep maester
rediensiam-hydra-maester-779695b457-2wss7   1/1   Running
$ kubectl get clusterrolebinding | grep rediensiam
rediensiam-hydra-maester-role-binding   ClusterRole/rediensiam-hydra-maester-role   2d6h
```

Its ClusterRole:

```yaml
rules:
  - apiGroups: ["hydra.ory.sh"]
    resources: ["oauth2clients", "oauth2clients/status"]
    verbs: ["get","list","watch","create","update","patch","delete"]
  - apiGroups: [""]
    resources: ["secrets"]
    verbs: ["list","watch","create"]      # ← cluster-scoped, every namespace
```

`list` on Secrets returns the full objects, `data` included. That is a **cluster-wide read of every
Secret in the cluster**, and the pod mounted a token to use it (`automountServiceAccountToken: true`).
On this cluster that is 58 Secrets across 2 namespaces — 52 in `default`, which also holds eight
unrelated `yandee-*` workloads. Compromise of the maester pod reads the IAM release's secrets *and*
every neighbour's.

For a controller that reconciles `OAuth2Client` custom resources — of which **this deployment has
zero**. Every Hydra client is created over the Admin REST API from the app:
`HydraService.CreateOAuth2ClientAsync`, called from `OrgController.cs:111`,
`SystemAdminController.cs:536,951`, `ManagedApiController.cs:117`, plus
`EnsureAdminSpaClientAsync` at startup. No `OAuth2Client` manifest exists anywhere in the repo.

**Changed:** `hydra.maester.enabled: false` in `values.yaml`. This removes the Deployment, the
ServiceAccount, the ClusterRole, the ClusterRoleBinding, the namespaced Role and the RoleBinding.

**Cluster-scoped RBAC created by this chart is now zero.** `grep -c "kind: ClusterRole"` on the
rendered output: `0`, both environments. It was 1 ClusterRole + 1 ClusterRoleBinding.

### 5.2 Keto mounted a token it does not need, because of a subchart bug

Keto rendered `automountServiceAccountToken: true` despite the subchart's own default being `false`.
The template:

```
ternary .Values.deployment.automountServiceAccountToken
        .Values.automountServiceAccountToken
        (not (empty .Values.deployment.automountServiceAccountToken))
```

`empty false` is `true`, so `not` is `false`, so a `deployment.automountServiceAccountToken: false`
can **never** be selected — the boolean-false default is unreachable by construction. Falling
through picks a top-level `.Values.automountServiceAccountToken` that the keto chart does not
define, which renders empty. Setting **both** keys is what actually produces `false`, and that is
what `values.yaml` now does. Keto talks to Postgres and to nothing else; it has no use for a
Kubernetes API credential.

### 5.3 Posture after this step

| workload | ServiceAccount | token mounted | API permissions |
|---|---|---|---|
| `rediensiam` (app) | default | **no** | none |
| `rediensiam-postgres` | default | **no** | none |
| `rediensiam-dragonfly` | default | **no** | none |
| `rediensiam-hydra` | `rediensiam-hydra` | **no** | none |
| `rediensiam-keto` | `rediensiam-keto` | **no** (was yes) | none |
| `rediensiam-hydra-maester` | — | — | **removed entirely** |

Verified on the rendered manifests, both environments — every pod spec carries
`automountServiceAccountToken: false`:

```
$ grep -n "serviceAccountName\|automountServiceAccountToken" <rendered prod>
275:automountServiceAccountToken: false        # SA object: rediensiam-hydra
289:automountServiceAccountToken: false        # SA object: rediensiam-keto
691:      serviceAccountName: rediensiam-hydra
692:      automountServiceAccountToken: false
850:      serviceAccountName: rediensiam-keto
851:      automountServiceAccountToken: false
956:      automountServiceAccountToken: false  # rediensiam
1113:      automountServiceAccountToken: false # postgres
1166:      automountServiceAccountToken: false # dragonfly
1376:automountServiceAccountToken: true        # SA object only — see below
1394:automountServiceAccountToken: false
1412:automountServiceAccountToken: false
```

**No new ServiceAccount or Role was created by this step.** The app needs no Kubernetes API access
— it reaches Postgres, Hydra and Keto over the network — so the correct RBAC for it is none, which
is what it has. Adding a dedicated SA with an empty Role would be ceremony that grants nothing and
implies a permission model that does not exist.

**One residual, deliberately left.** Line 1376 is the `rediensiam-hydra-cronjob-janitor`
ServiceAccount object, created as a Helm pre-install hook with `automountServiceAccountToken: true`.
The janitor CronJob itself is disabled and does not render, and the SA has **no Role or RoleBinding
of any kind** — a token minted from it grants nothing beyond `system:authenticated` discovery. The
subchart creates it unconditionally with no value to gate it; suppressing it means forking the Ory
chart. Not worth a fork for a zero-permission identity. Recorded so it is not rediscovered as a
finding.

---

## 6. Validation — real output

### 6.1 `helm lint`

```
$ helm lint rediensiam -f rediensiam/values.yaml -f rediensiam/values.dev.yaml -f rediensiam/values.secret.yaml
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed

$ helm lint rediensiam -f rediensiam/values.yaml -f rediensiam/values.prod.yaml -f <prod secrets>
==> Linting rediensiam
[INFO] Chart.yaml: icon is recommended

1 chart(s) linted, 0 chart(s) failed
```

The prod secrets file used for validation was generated by `deploy.sh`'s own `write_secrets_file`
into the scratchpad with an obvious placeholder password. No secret value was written into any
tracked file.

### 6.2 `helm template`

Both render, exit 0.

```
$ helm template rediensiam rediensiam -f values.yaml -f values.dev.yaml -f values.secret.yaml
exit=0  39 documents
      4 kind: ConfigMap          1 kind: PodDisruptionBudget
      4 kind: Deployment         3 kind: Secret
      1 kind: Ingress           10 kind: Service
      2 kind: Middleware         5 kind: ServiceAccount
      6 kind: NetworkPolicy      1 kind: StatefulSet
      2 kind: Pod

$ helm template rediensiam rediensiam -f values.yaml -f values.prod.yaml -f <prod secrets>
exit=0  42 documents
      1 kind: ClusterIssuer      2 kind: Pod
      4 kind: ConfigMap          1 kind: PodDisruptionBudget
      4 kind: Deployment         3 kind: Secret
      2 kind: Ingress           10 kind: Service
      3 kind: Middleware         5 kind: ServiceAccount
      6 kind: NetworkPolicy      1 kind: StatefulSet
```

Diff against step 9's recorded output (prod, 48 documents): **−1 Deployment, −1 ServiceAccount,
−1 ClusterRole, −1 ClusterRoleBinding, −1 Role, −1 RoleBinding**. All six are hydra-maester. Nothing
else moved.

### 6.3 Script checks

```
$ bash -n deploy/deploy.sh      → clean
$ bash -n sonar-scan.sh         → clean
```

`shellcheck` is not installed on this machine, so neither script was statically analysed beyond
syntax — same caveat as step 9.

**Self-check on `write_secrets_file`**, the one piece of non-trivial logic added. Driven with a
deliberately hostile password containing `" $ \ ' \``:

```
$ write_secrets_file ./out.yaml 'a@b.c' 'p"a$s\w'"'"'d`x'
$ python3 -c "yaml.safe_load(...)  assert round-trip, len(encryptionKey)==64, pepper set, mode==600"
write_secrets_file OK
```

**Behavioural check on the R-06 guard and the R-07 mode repair**, all three paths, run against the
extracted §1b block:

```
### A. dev, no file (fresh clone)
  Generated /tmp/…/s.yaml (mode 600)
  ┌─ DEV BOOTSTRAP ADMIN — unique to this machine, shown once ──────────
  │  email:    admin@dev.local
  │  password: <generated, unique per run>
  -> mode: 600

### B. dev, pre-existing 644 holding a shipped default
  Using /tmp/…/s.yaml (mode 600)
  WARNING (R-06): this file still holds credentials this repo shipped as defaults.
                  Every copy of the repo knows them. Re-roll with:
                    ./deploy/deploy.sh --dev --rotate-secrets
  -> mode: 600                       ← 644 repaired on the reuse path (R-07 gap 1)

### C. prod, pre-existing 644 holding a shipped default
  ERROR (R-06): … still holds a credential this repo shipped as a default.
                Refusing to deploy it. …
  -> mode: 600                       ← repaired even on the refusal path
```

### 6.4 Server-side schema validation

Rendered dev manifests, dry-run against the live k3s API:

```
$ kubectl apply --dry-run=server -f <rendered dev>
deployment.apps/rediensiam-hydra configured (server dry run)
deployment.apps/rediensiam-keto configured (server dry run)
deployment.apps/rediensiam configured (server dry run)
deployment.apps/rediensiam-dragonfly configured (server dry run)
statefulset.apps/rediensiam-postgres configured (server dry run)
ingress.networking.k8s.io/rediensiam-public configured (server dry run)
middleware.traefik.io/rediensiam-ratelimit created (server dry run)
middleware.traefik.io/rediensiam-bodylimit created (server dry run)
serviceaccount/rediensiam-hydra-cronjob-janitor configured (server dry run)
serviceaccount/rediensiam-hydra-job configured (server dry run)
serviceaccount/rediensiam-keto-job configured (server dry run)
secret/rediensiam-hydra configured (server dry run)
secret/rediensiam-keto configured (server dry run)
pod/rediensiam-hydra-test-connection created (server dry run)
pod/rediensiam-keto-test-connection created (server dry run)
…no errors; the remaining output is last-applied-configuration warnings on pre-existing resources
```

### 6.5 Not verified

**The chart was not deployed.** `helm lint`, `helm template` and a server-side dry-run all pass;
none of them prove the cluster still works. Three things a real `./deploy/deploy.sh --dev` would
confirm and reading cannot:

1. **Keto still starts without a mounted API token.** Expected — it only talks to Postgres — but
   this is the change most likely to surprise.
2. **Removing hydra-maester breaks nothing.** Expected, since no `OAuth2Client` CR exists, but the
   maester pod has been running for 2d6h and a `helm upgrade` will delete it and its CRD.
3. **The generated dev bootstrap password logs in.** `EnsureBootstrapAdminAsync` hashes it directly
   without running `PasswordPolicyService`, so policy is not a risk, but the round trip is unproven.

Run `./deploy/deploy.sh --dev` and check those three before treating this step as done.

---

## 7. Operator action required

Nothing below can be done by a script. Ordered by urgency.

### 7.1 Revoke and reissue the SonarQube tokens — **do this first**

Three `sqp_` tokens sat world-readable on this workstation. **Two have been removed from
`.sonar.env`; all three remain valid on the SonarQube server until a human revokes them.** A file
edit does not un-leak a token.

They were never committed — verified, §2.1 — so there is nothing to purge from history. Revocation
is the entire fix.

```bash
# SONAR_HOST is http://192.168.1.97:9000 per sonar-scan.sh:9
# UI:  $SONAR_HOST/account/security  →  Revoke, on ALL THREE:
#        the token behind SONAR_TOKEN_API     (still in .sonar.env, still live)
#        the token behind SONAR_TOKEN_ADMIN   (removed from the file, still live)
#        the token behind SONAR_TOKEN_LOGIN   (removed from the file, still live)
# Then issue ONE replacement and store it per §4.5 step 3.
```

Also review SonarQube's audit log for use of those tokens from any address other than this
workstation during the exposure window. The exposure was local-only as far as the evidence shows,
but "as far as the evidence shows" is not the same as "did not happen".

### 7.2 Clear the shipped default credentials from this workstation's dev secrets

`deploy/rediensiam/values.secret.yaml` still holds `changeme`, `Admin1234!` and
`CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`. Every copy of this repository knows them. `--dev` now warns
on every run until this is done.

```bash
./deploy/deploy.sh --dev --rotate-secrets
```

Discards the dev Postgres volume and regenerates everything. Dev data is disposable; if this
particular dev database is not, treat it as prod and follow §4.

### 7.3 Enable k3s secret encryption at rest

Kubernetes Secrets are stored **unencrypted** in k3s's datastore unless `--secrets-encryption` is
set. Every secret this chart creates — the HKDF root, the DB password, the Hydra system secret, the
bootstrap password — is currently sitting in plaintext in the datastore file. This is one flag, free,
and needs no new component:

```bash
sudo k3s secrets-encrypt status        # I could not run this: needs sudo
# if disabled, on the server node:
#   add --secrets-encryption to the k3s server args (/etc/systemd/system/k3s.service
#   or /etc/rancher/k3s/config.yaml), then:
sudo systemctl restart k3s
sudo k3s secrets-encrypt status        # expect: Encryption Status: Enabled
sudo k3s secrets-encrypt reencrypt     # rewrites existing secrets under the new key
```

This is the highest-value item in this section after §7.1 and it is fifteen minutes of work.

### 7.4 Decide on §4.7 — the key-id migration

Until ciphertexts carry a key id, chain C-3 has no recovery path that is not "re-enrol every MFA
user in every tenant by hand". It is ~2 days in `src/` and it is the difference between a rotatable
system and one that cannot survive a key compromise. Not a step-10 change; it needs to be somebody's
ticket.

### 7.5 Verify the deploy

Per §6.5. The chart was not deployed in this step.

---

## 8. A vault, honestly judged

The pipeline suggests HashiCorp Vault or AWS Secrets Manager. **Neither is proportionate here, and
neither is being installed.**

### 8.1 What this environment actually is

Single-node k3s. No cloud provider — AWS Secrets Manager, GCP Secret Manager and Azure Key Vault are
not options, they would each require adopting a cloud account and an IAM integration for one
consumer. One operator. Secrets files that are gitignored, never committed, and now mode 600. Nine
secret values total.

### 8.2 Vault

Vault on this cluster would mean: a StatefulSet with its own storage backend, an unseal-key custody
procedure, and either the Agent Injector or the Secrets Store CSI driver to get values into pods.

The blocking problem is the seal. **Vault must be manually unsealed after every restart** unless you
configure auto-unseal — which requires a KMS, which requires the cloud provider that does not exist,
which leaves transit-unseal-from-a-second-Vault, i.e. two Vaults. So on this node, every reboot
becomes: unseal Vault by hand, and only then does the IAM system get its database password.

That puts a manually-gated dependency in front of the authentication system for the entire estate.
Vault would not be reducing the blast radius of a compromise so much as adding a new way for
everything to be down. For nine values on one machine, that trade is not close.

Vault becomes correct when there are dynamic database credentials with short TTLs, multiple
consumers with distinct policies, or an audit requirement for per-access secret logging. None of
those is true today. The first one that becomes true is the signal to revisit.

### 8.3 sealed-secrets, SOPS, External Secrets

- **External Secrets Operator** is a *bridge* to a backend. There is no backend. It would be an
  operator pointed at nothing.
- **sealed-secrets** solves "I want encrypted secrets in Git". These secrets are not in Git and are
  not supposed to be. It would also introduce a controller-held private key whose loss makes every
  SealedSecret unrecoverable — a new single point of failure to protect a problem that does not
  exist here.
- **SOPS + age** is the closest fit and the *right first upgrade* — but the problem it solves is
  sharing secrets across people or machines. Today there is one operator and one machine, so it
  would add an `age` key to protect (with the same custody question) and a decrypt step to
  `deploy.sh`, in exchange for encrypting a file that no second party ever sees.

### 8.4 The honest answer

**The chart's existing secret handling plus the rotation discipline in §4 is adequate at this
scale**, now that the two things that actually made it inadequate are fixed: the values are no
longer predictable (§1) and the files are no longer world-readable (§3).

The controls that already carry the weight: secrets are env-only and explicitly excluded from the
mutable DB configuration layer (`InstanceConfiguration.cs:17-19`), mounted via `secretKeyRef`
rather than baked into images, never written to a log anywhere in `src/` (verified by inspection in
step 1), and — from step 9 — reachable only through NetworkPolicies that scope every backing service
to the release.

**Adopt SOPS + age the day a second operator or a second machine appears.** That is the trigger, and
it is a two-hour change: `age-keygen`, `sops -e` the values files, one `sops -d` in `deploy.sh`. It
is not worth doing before the trigger, and doing it before the trigger would be the same mistake as
installing Vault, only cheaper.

**The single highest-value secrets-at-rest improvement available right now is not a vault. It is
`--secrets-encryption` on k3s (§7.3)** — one flag, no new component, and it addresses the fact that
every one of these secrets is currently plaintext in the cluster datastore regardless of how
carefully the files that produced them are handled.

---

## 9. Files changed

```
deploy/deploy.sh                     §1b rewritten: per-install generation for BOTH environments,
                                     write_secrets_file(), --rotate-secrets, KNOWN_DEFAULTS guard
                                     (fail-closed prod / warn dev), scoped umask + unconditional
                                     chmod 600 on create AND reuse, pepper actually written,
                                     single-quoted YAML for the operator-chosen password
deploy/rediensiam/values.yaml        hydra.maester.enabled=false (removes the cluster-wide
                                     secrets-read ClusterRole and a mounted token);
                                     keto automountServiceAccountToken=false (both keys);
                                     Hydra secrets.system rotation semantics documented
sonar-scan.sh                        mode check + repair on load; umask before the save redirect
.sonar.env                           (untracked) 664→600; two dead tokens removed
deploy/rediensiam/values.secret.yaml (untracked) 664→600
tests/e2e/.env                       (untracked) 664→600 — mode only, see §3.3
tests/e2e/.auth/admin-session.json   (untracked) 664→600 — mode only, see §3.3
deploy/rediensiam/values.dev.yaml.bak deleted (I-04)
```

No template under `deploy/rediensiam/templates/` was modified. No secret value was written into any
tracked file. Nothing under `src/`, `frontend/` or `sdk/` was touched; under `tests/` only two file
modes changed.

### 9.1 One change deliberately not made

A `fail` guard in `templates/secret.yaml` rejecting an empty or wrong-length `encryptionKey` would
be a nice environment-independent backstop for someone running `helm` by hand. It is not there
because rule 4 requires `helm lint`/`helm template` to pass for `values.yaml + values.dev.yaml` and
`values.yaml + values.prod.yaml`, and such a guard would make exactly those two combinations fail
when the secrets file is omitted. The realistic accident it would catch — helm run without secrets —
already fails loudly, just one layer later, when `Convert.FromHexString("")` throws at startup.
Recorded rather than silently skipped.

---

## 10. What is left, with its cost

| Item | Why still open | Cost |
|---|---|---|
| **Revoke the three SonarQube tokens** | only a human can | 10 min, §7.1 |
| **Clear this workstation's dev defaults** | destroys the dev DB; operator's call | 1 command, §7.2 |
| **k3s `--secrets-encryption`** | needs root on the server node | 15 min, §7.3 |
| Key-id prefix + re-encrypt pass (C-3) | `src/`, frozen this step | ~2 days, §4.7 |
| Keyed Argon2 pepper with re-hash-on-login | `src/`, frozen this step | ~1 day, §4.6 |
| Forced rotation of the bootstrap password on first login | needs a schema column and a login-flow branch in `src/` | ~0.5 day, §1.5 |
| Separate Postgres roles per component | architecture review §5.3; makes §4.2 three independent rotations instead of one coupled change | 0.5 day |
| SOPS + age | no second operator or machine yet | 2h **when** that changes, §8.4 |
| Vault / cloud secret manager | disproportionate; see §8.2 | not recommended |
| `rediensiam-hydra-cronjob-janitor` SA automounts | zero-permission identity; suppressing it means forking the Ory chart | not worth it, §5.3 |
| **Nothing was deployed** | no `deploy.sh` run in this step | §6.5 |
