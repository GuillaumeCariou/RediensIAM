# Step 22 — setup scripts: bare cluster to working IdP

**Date:** 2026-07-31 · **Branch:** `security/hardening-2026-07-30` · **Scope:** `deploy/`, `docs/`, `README.md`, `.gitignore`
**Not committed.** `src/`, `tests/`, `sdk/` and `frontend/` were not touched — another agent was working there (step 21).
**Cluster state at finish:** `./deploy/verify-deployment.sh --dev` → **31 passed · 0 failed · 3 skipped**, 5 pods `Running`.

---

## The problem this closes

Before the audit, `./deploy/deploy.sh --dev` was roughly the whole install. Afterwards a correct
installation involves cert-manager, a four-role Postgres split with `scram-sha-256`, an ordered
TLS cutover, image digest pinning, generated secrets, an optional CloudNativePG backend, an
optional RLS flag, key rotation and a backup CronJob — with the operator-facing half of that
spread across six reports as prose.

Six reports is a fine audit trail and a useless install guide. Worse, the *actual* install guide —
`README.md` — still told operators to hand-write `values.secret.yaml` from a template containing
`CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`, `STRONG_PASSWORD` and a single shared `iam` DSN. That
template is three chart revisions out of date and describes the exact pre-T-04 shape that
`deploy.sh` now refuses on `--prod`.

The approach here is not a checklist. Where a decision has a safe answer, the script takes it and
does not ask. Where it does not, the script asks and refuses to continue without an answer. Where
the audit deferred something to a runbook, the script names the runbook by section.

---

## What was written

| File | Lines | New? |
|---|---|---|
| `deploy/preflight.sh` | 402 | new |
| `deploy/setup.sh` | 441 | new |
| `deploy/reset-dev.sh` | 193 | new |
| `docs/DEPLOYMENT.md` | 342 | new |
| `deploy/deploy.sh` | +25 | modified |
| `README.md` | −96 / +40 | modified |
| `.gitignore` | +5 | modified |

---

## `deploy/preflight.sh --dev|--prod`

Read-only except for `--install-cert-manager`. Exit 0 ready · 1 at least one FAIL · 2 could not run.

Every check is a condition that produces a **broken-but-plausible** cluster rather than an error
at `helm upgrade` time. That is the finding-D class: a one-line config mismatch that crash-looped
deploys for three days.

**Host tools.** kubectl, helm (version-compared, 3.12+), docker (daemon reachable, not just
installed), node (20+, because `deploy.sh` builds both SPAs before the image), npm, openssl, curl.

**Cluster.**

| Check | Failure mode it prevents |
|---|---|
| API reachable, ≥1 node `Ready` | — |
| Namespace exists | `helm upgrade` fails after the image is built and pushed |
| A **default StorageClass** | Postgres and backup PVCs request no class; pods stay `Pending` forever with no error anywhere |
| The `IngressClass` named in `values.yaml` | Ingress objects are created and route nothing |
| CRD `middlewares.traefik.io` | the rate limit, the 1 MiB body cap and the **P-04 management-API deny** are Traefik `Middleware` objects; without the CRD, apply fails post-build |
| `trustedProxies` covers the node's real `.spec.podCIDR` | the app refuses to start on empty, and on a *wrong* value every IP-based control reads the ingress pod's address. Compared at /16 granularity — enough to catch k3s `10.42.x` vs kubeadm/flannel `10.244.x`, which is the mistake that actually happens |
| `defaultDenyScope: namespace` vs foreign pods in the namespace | a namespace-wide default-deny is an **outage for a neighbour** that has no policy of its own — exactly the situation step 9 had to avoid with the nine `yandee-*` pods |

**Chart render.** Runs `helm template` with the real values (and the real secrets file if it
exists; otherwise placeholders that satisfy the chart's TLS/auth guards so the same branches are
exercised). This is both a check in itself and the only honest way to answer the next question.

**cert-manager.** *Required* iff the rendered chart contains a `cert-manager.io/` object. It is
detected by rendering, not by grepping — `values.yaml` has three separate `tls:` blocks and
matching the wrong one is precisely how a check becomes a lie. Both shipped environments enable
Postgres TLS, so both need it. Presence is checked as **CRD + webhook `readyReplicas` ≥ 1**: the
CRD alone is not enough, since a webhook that is not serving rejects `Certificate` objects at
apply with a connection error. `--install-cert-manager` installs v1.21.1 from jetstack with
`crds.enabled=true` and waits.

If the render failed for a reason other than un-fetched subcharts, the cert-manager verdict is
reported as **not evaluated** rather than "not required". A security check that passes because it
never ran is the thing this audit keeps finding.

**Registry.** Reports the bind of the existing container (`deploy.sh` rebinds to loopback itself,
R-16), or — if there is no container — checks that TCP :5000 is not already held by something
else, which would otherwise fail `docker run -p 5000` *after* both SPAs have been built.

**Credentials.** Mode of the secrets file; `KNOWN_DEFAULTS` match (FAIL on prod, WARN on dev);
presence of the T-04 four-role block (`appPassword:`), whose absence means every component would
connect as the superuser `iam` — FAIL, with the migration runbook named.

**Prod only.** `publicUrl` must be https and not a localhost/`.local` name; `adminUrl` must be
set; the public host is resolved from this machine and a failure is a WARN naming the ACME
HTTP-01 consequence ("only a human can create that record"); and if a `CronJob` renders, a WARN
that the nightly dump lands in the same failure domain as the data.

---

## `deploy/setup.sh --dev|--prod`

`preflight.sh` → `deploy.sh` → `verify-deployment.sh` → tell the operator what they have.
A preflight failure stops before anything is built. A verify failure exits 1 with
"the install is running but at least one asserted control is NOT live. Do not treat this as done."

### `--dev`

One command, no questions, no manual steps, no invented passwords. It ends with the login URL, the
register URL, the admin console URL, the discovery URL, and — **only on a first install** — the
bootstrap admin email plus a pointer to the mode-600 file holding the password. On a re-run it
says "existing install — not reprinted" rather than re-printing a credential nobody asked for.

### `--prod`

Interviews the operator, writes the answers to `deploy/rediensiam/values.prod.override.yaml`
(gitignored; layered after `values.prod.yaml` and before the secrets file), shows the whole file,
and only then deploys. `--plan` stops after writing it. Re-running offers to reuse the previous
answers.

**It refuses rather than guesses, in these places:**

| Question | What it refuses |
|---|---|
| Public hostname | `localhost`, `.local`, `127.*`, a URL instead of a hostname |
| Admin hostname | equal to the public hostname — P-04 separation would be meaningless |
| Public TLS | `existing` naming a ClusterIssuer that is **not in the cluster** → stop. `acme` without an email → stop (and the chart `fail`s on it too). `acme` when the host does not resolve → typed acknowledgement `dns is not ready yet` |
| Admin TLS | `selfsigned` → typed acknowledgement `i accept the browser warning`. `existing` is verified against the cluster. The prompt states that ACME **HTTP-01 cannot** certify a Tailscale MagicDNS name |
| Database backend | `cnpg` without the CNPG CRD installed → stop. `cnpg` naming a `Cluster` that does not exist → stop. Otherwise typed acknowledgement `cnpg backup and netpol are mine`, because both `postgres-lockdown` and the backup CronJob silently stand down in that mode |
| Backups (builtin) | a blank off-node destination. `none` requires typed acknowledgement `one node one copy` |
| RLS | it does not ask. On a first install the application build that sets `rediensiam.org_id` is not running yet, and the policies are fail-closed; enabling it would be a total outage inside one `helm upgrade`. Left off, with §3 named |
| Non-interactive stdin | prod setup exits rather than defaulting anything |

The off-node backup destination is written into the override file **as a comment**. The chart does
not automate it and the script does not pretend otherwise; the value of asking is that the answer
exists and is written down.

After a prod deploy it prints the four things it cannot do: the CNI NetworkPolicy check (with the
exact `np-test` commands), `k3s secrets-encrypt`, the restore test, and moving the secrets file
off the machine.

`NAMESPACE=rediensiam ./deploy/setup.sh --prod` now works — see the `deploy.sh` changes below.
15c recommends making that choice at the first prod install, because Helm cannot move a release
between namespaces and the PVCs cannot follow.

---

## `deploy/reset-dev.sh`

Destructive, and explicit about exactly what it destroys before it asks.

It prints each PVC with its size, phase, and **what is on it in English** — "every user,
organisation, project, Hydra client, OAuth2 token, consent session, Keto relation tuple and audit
record" for the Postgres volume, "every retained nightly pg_dumpall" for the backup volume — then
requires the operator to type `destroy dev`. `--dry-run` lists only; `--yes` skips the prompt;
`--keep-secrets` preserves the credentials file; `--registry` also drops the registry container
and its image layers.

The PVC selector matches `^(data-)?<release>` across the whole namespace, so it collects
**orphans left by earlier installs**, not just what the current release owns. StatefulSet PVCs
survive `helm uninstall`; a surviving `data-rediensiam-postgres-0` still holds the *old* database
password and is how a "clean" reinstall comes back with credentials nobody has. It also clears
Helm hook Jobs (which are not part of the release manifest) and the release's Secrets, then waits
for the PVs to actually release before declaring success.

**It refuses** to run against a release whose Hydra issuer is not a localhost name, and there is
no `--force`. Tearing down a real environment is a decision, not a flag; the refusal message
gives the `helm uninstall` command and says the PVCs are the operator's call.

---

## Changes to `deploy/deploy.sh` (25 lines, additive)

1. `NAMESPACE="${NAMESPACE:-default}"` — was hardcoded. This is the 15c recommendation to lift
   the namespace out of the file, without moving anything.
2. An optional `values.<env>.override.yaml`, layered after the env file and before the secrets
   file, so operator decisions win over committed defaults and never over a credential. Guarded by
   `[ -f ]`; absent, behaviour is unchanged.
3. `PUBLIC_URL` / `ADMIN_URL` and the `requireSsl` detection now consult the override, so the
   summary URLs and the generated DSNs stay consistent with the decisions.

Nothing was removed and no existing branch was rewritten.

---

## What was actually executed, and its real output

Everything below was run on this cluster (single-node k3s, cert-manager v1.21.1, release
`rediensiam` in `default`).

**`bash -n`** on all five scripts, after every edit — clean.

**`./deploy/preflight.sh --dev`** — 19 ok · 0 failed · 1 warning. The warning is real and is
covered below.

**`./deploy/preflight.sh --prod`** — 20 ok · 0 failed · 2 warnings (no `values.prod.secret.yaml`;
backup failure-domain).

**Preflight negative paths, all executed:**

- `NS=doesnotexist ./deploy/preflight.sh --dev` → `FAIL namespace doesnotexist does not exist`,
  exit 1.
- A deliberately broken override (`postgres.local.tls.enabled: false` with `requireSsl: true`)
  → `FAIL the chart does not render with your values → Error: execution error at
  (rediensiam/templates/postgres.yaml:10:4): rediensiam.postgres.local.tls.requireSsl needs
  tls.enabled — hostssl on a server with ssl=off refuses every client`, and the cert-manager
  verdict correctly downgraded to `WARN … not evaluated`.

**`./deploy/setup.sh --prod --plan`**, driven under a pty with scripted answers → wrote a complete
override; `helm template` of `values.yaml + values.prod.yaml + override` then produced exactly the
expected objects: `ClusterIssuer/letsencrypt` with the ACME email and server, `ClusterIssuer/selfsigned`,
`Ingress/rediensiam-public` annotated `cert-manager.io/cluster-issuer: letsencrypt` with
`tls.hosts: [auth.example.test]`, `Ingress/rediensiam-admin` annotated `selfsigned` on
`admin.example.test`, one `CronJob`, one `StatefulSet`. `./deploy/preflight.sh --prod` then passed
against that override.

**Prod refusal paths, all executed:**

- `existing` + a ClusterIssuer name that is not in the cluster →
  `ERROR: ClusterIssuer 'nosuchissuer' does not exist in this cluster`.
- `cnpg` on a cluster with no CNPG operator →
  `ERROR: the CloudNativePG operator is not installed in this cluster`.
- stdin closed mid-interview → `ERROR: stdin closed while asking: Admin console hostname`.
- `./deploy/setup.sh --prod </dev/null` → `ERROR: prod setup is interactive by design`.

**A real bug found by running it.** The first pty run **hung**: `ask` and `ask_choice` looped
forever on EOF, because `read -r` returning failure was indistinguishable from an empty answer.
Fixed — both now `|| die`. This is the kind of defect that a script reviewed but never run ships
with, and it would have presented as a prod install that appears to freeze.

**`deploy/reset-dev.sh`:**

- `--dry-run` against the live dev release — correct inventory: release revision 15, five pods,
  `data-rediensiam-postgres-0` (2Gi) and `rediensiam-backup` (5Gi) with their plain-English
  contents, and the credentials file.
- Confirmation prompt with the wrong answer → `Aborted — nothing was changed.`
- **Refusal path, executed for real:** a throwaway Helm release `fakeiam` was installed into a
  scratch namespace with `issuer: https://auth.example.com`, and the script refused it:
  `REFUSING: release 'fakeiam' in namespace 'resettest' has issuer https://auth.example.com.`
- **Destructive path, executed for real** against that same throwaway release (issuer flipped to
  `http://iam.localhost`), with a test PVC and Secret: release uninstalled, PVC deleted, PVs
  released, Secret removed, and `--keep-secrets` correctly left the repo's real
  `values.secret.yaml` untouched. The scratch namespace was then deleted.

**`deploy/setup.sh --dev`, end to end on the live cluster.** Per the constraint not to rebuild
from `src/` while another agent edits it, `deploy.sh` was replaced by a stub performing the same
`helm upgrade` pinned to the digest already running
(`sha256:2c32df17539a20db636b9736b8ebc420e7e29a8015589697944b68cdaad8269d`). Preflight ran, the
chart deployed (release revision 15 → 16), `verify-deployment.sh --dev` reported **31 passed · 0
failed · 3 skipped**, and the operator summary printed the four URLs and the credentials pointer.
Five pods `Running` after.

The stages `deploy.sh` performs that the stub skipped are the two `npm run build`s, `docker build`
and `docker push`, and the digest resolution — none of which the new scripts changed.

---

## Findings

**F-1 · This workstation's dev install is running the shipped Hydra system secret.**
`deploy/rediensiam/values.secret.yaml` still contains
`hydra.hydra.config.secrets.system: ["CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS"]`, and
`helm get values` confirms the running release has it. `deploy.sh`'s `KNOWN_DEFAULTS` guard is
warn-only on dev, which is the right call — but the warning has evidently been passed over.
`preflight.sh` now repeats it at the top of every install. Not fixed here: clearing it means
`reset-dev.sh` + a full rebuild from `src/`, which was out of scope this session. **Cost to close:
one `./deploy/reset-dev.sh && ./deploy/setup.sh --dev`, which discards dev database state.**

**F-2 · The same file carries no `rediensiam.security.argon2Pepper`.**
`write_secrets_file` generates one; this file predates that and has no `security:` block, so the
dev install runs with no pepper. Same fix and same cost as F-1.

**F-3 · `README.md`'s install section was three chart revisions stale and actively harmful.**
It instructed operators to hand-write `values.secret.yaml` from a template containing
`CHANGE_ME_HYDRA_SYSTEM_SECRET_32CHARS`, `STRONG_PASSWORD`, `CHANGE_ME_64_HEX_CHARS`, and
`postgres://iam:…` DSNs — the pre-T-04 shape that `deploy.sh --prod` now exits on. F-1 is what
following that README produces. Replaced with a pointer to `docs/DEPLOYMENT.md` and the two
one-line commands, plus an explicit "do not hand-write this file, and here is why the old template
was dangerous".

---

## What still needs a human, and why

Unchanged from the source reports; consolidated into `docs/DEPLOYMENT.md` and printed by
`setup.sh --prod`.

| Thing | Why no script can do it | Where |
|---|---|---|
| A DNS record for the public hostname | it is not in this cluster | prompt + preflight WARN |
| An ACME account email | it is a person's mailbox | `setup.sh` asks; the chart `fail`s without it |
| A CA root distributed to operator devices | a trust decision plus device access | `09 §6.3` |
| The off-node backup destination | a budget and a second failure domain | `setup.sh` asks and refuses blank |
| `k3s secrets-encrypt` | root on the server node | `10 §7.3`, printed after prod install |
| The CNI NetworkPolicy check | needs a pod and 3 seconds, but it is the load-bearing assumption behind every policy in the chart | `09 §6.8`, printed both places |
| The restore test | must run against a throwaway server, and loses `GRANT pg_read_all_data TO iam_backup` in the process | `15c §T-03` |
| Enabling RLS | gated on a deployed-and-verified application build; the policies are fail-closed | `18 §3`; `setup.sh` declines to ask |
| The T-04 migration on an existing database | `init.sh` only runs against an empty data directory | `15c §T-04`; preflight FAILs on the old file shape |
| Postgres `hostssl` on an existing database | `pg_hba.conf` lives on the PVC; a chart upgrade does not rewrite it | `18 §2` |
| Key and pepper rotation | ordered, gated on `total_pending == 0`, and one direction is unrecoverable | `16 §7`, `10 §4` |

---

## Left undone, with its cost

| Item | Cost | Why not now |
|---|---|---|
| F-1 / F-2: clear the shipped Hydra secret and the missing pepper on this workstation | 10 min + dev data loss | needs a rebuild from `src/`, which another agent is editing |
| `verify-deployment.sh` V-20/V-21/V-23 skip under CNPG | ~1 h | they `kubectl exec` into `rediensiam-postgres-0`; making them CNPG-aware is a separate change, and CNPG mode is unexercised here |
| The CNPG `Cluster` manifest in `18 §1` is still untested | ~2 h + an operator | no CNPG operator on this cluster; `setup.sh` refuses `cnpg` rather than pretending |
| `setup.sh --prod` has never been run against a real production cluster | — | prod has never been deployed from this branch. Every prod path here is template-verified, preflight-verified and refusal-path-tested; none of it has issued a certificate or served a request |
| Registry auth + TLS | 2 h | `09 §6.2`; preflight reports the bind, and `deploy.sh` enforces loopback. **Required, not optional, the moment k3s is not on this host** — bind and auth move together |
| A repo-root wrapper script | 5 min | `README.md` points at `deploy/setup.sh`; a second entry point is one more thing to keep in sync |

---

## Acceptance

```
$ ./deploy/verify-deployment.sh --dev
 31 passed · 0 failed · 3 skipped
 All asserted controls are live.

$ kubectl get pods -n default
rediensiam-7fc6978cc9-bfpvs             1/1   Running
rediensiam-dragonfly-768d4c89df-4h2cz   1/1   Running
rediensiam-hydra-75b7fc79b4-9v4tq       1/1   Running
rediensiam-keto-754dc4c55d-792qd        1/1   Running
rediensiam-postgres-0                   1/1   Running
```

The three skips are the intended ones: V-05 (dev is deliberately cleartext), V-19 (`values.yaml`
pins no digest — `deploy.sh` passes it with `--set`), V-25 (RLS off).
