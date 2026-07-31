# Step 13 — Security Monitoring

**Status:** complete. The agent running this step was stopped mid-repair; its work was verified,
finished and validated by hand. Everything below was run, not asserted.

## The SIEM decision: no SIEM

Single-node k3s, one operator, one IdP. An ELK or Splunk deployment here would compete with the
authentication system for RAM on the same node, and would be watched by nobody. **An alert with no
owner is not a control**, and a monitoring stack that degrades the thing it monitors is a net
negative.

What was built instead: the `audit_log` table already in the schema is the detection substrate, a
fixed set of SQL rules is the detection layer, and a post-deploy script asserts that claimed
controls are actually live. This is proportionate to one operator and can be operated by one.

Revisit if a second operator appears, or if `audit_log` volume makes ad-hoc SQL too slow — at that
point Loki plus the existing rules is the cheapest upgrade, not a full SIEM.

## What already existed

`AuditLogService` and the `audit_log` table predate this audit. Steps 4, 5 and 8 added records:
nine MFA-mutation events where there had been one, `api.introspect.out_of_scope` /
`api.authorize.out_of_scope` on tenant-scoping refusals, and role-change events. That was enough
substrate to build on — nothing was rebuilt.

## Deliverables

| File | What it is |
|---|---|
| `deploy/verify-deployment.sh` | asserts claimed controls are live; `--dev` / `--prod` |
| `deploy/monitoring/audit-detections.sh` | 13 SQL detection rules over `audit_log` |
| `deploy/monitoring/selftest.sh` | seeds data, proves each rule actually fires |

### `verify-deployment.sh` — the highest-value item in this step

Step 12's central finding was that **the running system is not the repository**: nine claimed fixes
were true of files and false of the cluster, because nothing had been deployed. There was no way to
tell the difference. Now there is.

Run against the live cluster on 2026-07-31:

```
 12 passed · 12 failed · 2 skipped
```

Failing — i.e. claimed in `.security-hardening/` but **not live**:

| ID | Finding | Live state |
|---|---|---|
| V-01 | R-16 | registry on `0.0.0.0:5000`, unauthenticated cleartext push from the LAN |
| V-02 | C-3 | `rediensiam-hydra-maester-role` still grants `list/watch/create` on Secrets cluster-wide |
| V-03 | — | hydra-maester still running, though `values.yaml` disables it |
| V-04 | P-04 | public host serves `/admin/` with **200**; `/org`, `/project`, `/service-accounts` reachable (401 — bearer auth is the only control) |
| V-07/V-08 | R-16 | mutable tag `localhost:5000/rediensiam:dev`, `pullPolicy: Always` |
| V-09 | R-32 | no seccomp profile |
| V-15 | — | no default-deny ingress policy |
| V-17 | R-26 | live CSP missing `script-src`, `base-uri`, `form-action` |

Passing: `runAsNonRoot`, `allowPrivilegeEscalation=false`, `readOnlyRootFilesystem`, all
capabilities dropped, `automountServiceAccountToken=false`, the four per-component lockdown
NetworkPolicies, and no external font host in the CSP.

**Every failure above is fixed in the chart and pending a deploy.** The script exists so that
claim is checkable rather than believed.

One design note worth keeping: V-02 originally used a Python f-string whose syntax error was
swallowed by `2>/dev/null`, so **the check silently always passed**. It now uses jsonpath and
`fail`s when enumeration returns empty — an empty result means the query broke, not that the
cluster is clean. A security assertion must never pass by accident; that bug is the same class as
step 9's port narrowing, which was decorative until the pentest noticed.

### Detection rules

13 rules, each labelled with the finding or chain it covers. The ones that matter most are the
compensating detections for residuals that step 11b left open **by decision**:

- **D-01** — forged `ServiceAccountRole` rows predating the P-01 fix. The write path is closed; rows
  written before it are not. This turns 11b's detection SQL into something runnable.
- **D-02** — `api.introspect.out_of_scope` / `api.authorize.out_of_scope` bursts: a service account
  probing other tenants.
- **D-04** — MFA factor mutations, which step 8 made auditable precisely so they could be watched.
- **D-06** — org audit-retention shortened below the global floor: a tenant trying to erase itself.
- **D-09** — trust-anchor and security-parameter writes.
- **D-13** — auth-failure bursts per actor and per source IP.

Validated by `selftest.sh`, which seeds rows and asserts each predicate returns the expected count:

```
  ok    D-01   expected 1, got 1
  ok    D-02   expected 4, got 4
  ok    D-06   expected 1, got 1
  ok    D-04   expected 3, got 3
  ok    D-09   expected 1, got 1
  ok    D-13   expected 1, got 1
 all detection predicates fire as intended
```

A detection rule nobody has ever seen fire is a hypothesis. These have been seen to fire.

### Alert routing, and its honest problem

`ALERT_URL` pushes page-severity hits to an ntfy topic — a phone notification, no infrastructure to
run. Exit codes are `0` clean / `1` page-severity hit / `2` could not run, so cron or a systemd
timer can act on it.

**The ownership problem cannot be solved technically.** One operator means no rotation, no
escalation, and no second pair of eyes on an alert about the operator's own account. Step 12 costed
this as part of the SOC 2 gap; it is the same constraint.

## Operating it

```bash
./deploy/monitoring/audit-detections.sh                  # last 24h
./deploy/monitoring/audit-detections.sh --window 7days   # weekly review
ALERT_URL=https://ntfy.sh/<topic> ./deploy/monitoring/audit-detections.sh
./deploy/verify-deployment.sh --dev                      # after every deploy
```

The weekly review is the control. Daily automated runs catch bursts; the weekly pass is where a
human notices the slow things — a role grant that should not exist, a retention value that drifted.

## What remains unmonitored

- **No backup exists** (step 12). Monitoring detects; it does not restore. A disk failure still
  destroys every tenant permanently. This is the largest single gap in the system and it is not a
  monitoring problem.
- **No log aggregation** — detections run against Postgres directly. If the database is destroyed,
  so is the evidence. Shipping `audit_log` off-node is the natural next step (~half a day).
- **k3s has no audit log** (`k3s server` runs with zero flags), so cluster-level actions are
  invisible. One flag, per step 12.
- **SAML XML processing** remains unassessed by any step — not monitored because not understood.
- **Application logs are not scanned for secret leakage** beyond step 5's fix. A recurring grep for
  token-shaped strings in pod logs would cost an hour.
