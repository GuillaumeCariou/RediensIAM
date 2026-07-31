# Step 12 — Compliance Assessment

**Branch:** `security/hardening-2026-07-30` · **Date:** 2026-07-30
**Scope:** OWASP ASVS 4.0.3 Level 2, CIS Benchmarks (Kubernetes / Docker / PostgreSQL), SOC 2 Type II, PCI-DSS v4.0, HIPAA Security Rule.
**Method:** source review with `file:line` citations, read-only probes against the live k3s cluster, read-only queries against the running PostgreSQL, the SonarQube server's current analysis, `npm audit`, and one full execution of the integration suite.
**Nothing under `src/`, `frontend/`, `sdk/`, `deploy/` or `tests/` was modified. Nothing was committed. No cluster object was created, changed or deleted.**

> **This document assesses. It does not certify anything.** Where a control is met I cite the line
> that meets it. Where I am inferring I say "by inspection". Where I could not test, I say
> "unassessed" rather than assuming either direction. Three of the eleven prior reports in this
> directory overclaimed and the penetration test caught them; the correction for that is evidence,
> not a better tone.

---

## 0. Executive summary — for someone who does not read code

**What this system is.** RediensIAM is a multi-tenant identity provider: it holds the login
credentials, second factors and account records for the users of every application that trusts it.
It runs today on a single-node Kubernetes cluster on one desktop machine, administered by one
person, alongside eight unrelated application pods in the same namespace.

**The one-line verdict.**

| Framework | Applies? | Verdict |
|---|---|---|
| **OWASP ASVS L2** | **Yes — this is the right standard for this system** | **Substantially met on application logic, not met on infrastructure, cryptographic key lifecycle, and build integrity.** Roughly 70 % of applicable L2 requirements met with evidence. |
| **CIS Kubernetes** | Yes, partially (single-node k3s) | Workload hardening is good in the chart; **the chart is not what is running.** Cluster-level controls (audit log, secrets-at-rest encryption, Pod Security Admission) are absent. |
| **CIS Docker** | Yes, build stage only | Image runs as non-root; base images are **not** pinned by digest; the build registry is unauthenticated cleartext and currently listening on all interfaces. |
| **CIS PostgreSQL** | Yes | **Poor.** One shared superuser for three components, `trust` authentication locally, TLS off, no audit logging, no row-level security. |
| **SOC 2 Type II** | Yes in principle | **Not achievable today, and not by writing code.** No evidence period, no change-management record, no monitoring history, and one operator — a segregation-of-duties failure that no technical control can fix. |
| **PCI-DSS v4.0** | **No — this system stores no cardholder data** | Directly out of scope. It would become in scope as a *connected/security-impacting system* only if an application it authenticates handles card data. **That question is open and the operator must answer it.** |
| **HIPAA** | **No — this system stores no PHI, and no Covered Entity / Business Associate relationship exists** | Would apply on the day it is sold to a healthcare customer, at which point a BAA and a set of controls it does not have become mandatory. |

**The three things to act on first, in order.**

1. **Deploy what has been built.** The running cluster is on manifests from before the hardening
   work. Everything steps 6, 9, 10 and 11b claim is true of files on disk, not of the system serving
   traffic. Concretely, right now: the login page answers on **unencrypted HTTP**, the admin console
   is reachable on the **public hostname**, the container registry accepts **unauthenticated
   cleartext pushes from the whole LAN**, and a pod is running with permission to **read every
   Kubernetes Secret in the cluster**. All four are fixed in the repository and none is fixed in
   reality.
2. **Turn on encryption in transit.** No connection anywhere in this system is encrypted today —
   not the browser connection, not the database connection, not the cache connection. This is the
   single largest gap against every framework simultaneously, and it is the one a customer's
   security questionnaire asks about first.
3. **Accept that SOC 2 Type II is a 12-to-18-month, two-person, £25k–£75k programme, not a
   remediation task.** Nothing in the codebase blocks it; the organisation does. Say so to
   prospects rather than starting readiness work that cannot converge.

**What this system is not ready for, stated plainly.** It is not ready to be sold to a regulated
customer. It is not ready to hold payment or health data. It is not ready for a SOC 2 or ISO 27001
attestation. It is not ready for a customer-facing security questionnaire that asks about
encryption in transit, key rotation, backup testing, or segregation of duties. It **is** ready to
be the identity provider for the operator's own applications on a private network, and — after the
deployment gap in item 1 is closed — for a small number of low-risk external tenants under a
contract that discloses the residual risks in §9.

---

## 1. Evidence base

Everything below is one of five kinds of evidence. I label each finding with which.

| Kind | What it means |
|---|---|
| **Executed** | I ran it and observed the result in this session. |
| **Live probe** | Read-only observation of the running cluster or database in this session. |
| **Cited** | A specific `file:line` in the working tree that I read. |
| **By inspection** | I reasoned from code I read but did not execute the path. |
| **Unassessed** | I could not test it and I am not going to guess. |

### 1.1 Evidence produced in this session

```
dotnet test tests/RediensIAM.IntegrationTests -p:SonarQubeTargetsImported=true
Passed!  - Failed: 0, Passed: 1221, Skipped: 0, Total: 1221, Duration: 6 m 11 s
```

**Executed.** This independently confirms step 11b's claim: the three `PENTEST_FAILING_*` proofs
now pass, and no regression was introduced. It is the strongest single piece of evidence in this
directory and it is reproducible.

The same build emits three compiler diagnostics, one of which is a security rule:

```
src/Controllers/IntrospectionController.cs(54,38): warning SCS0016:
  Controller method is potentially vulnerable to Cross Site Request Forgery (CSRF).
```

`security-code-scan` is wired into the build — that is a real SAST control operating on every
compile. This particular warning is a **false positive** (the endpoint is bearer-authenticated per
RFC 7662 and has no ambient cookie authority; `IntrospectionController` gates on
`IsServiceAccountCaller()`), but it is unsuppressed and unreviewed, which is itself an ASVS V14
finding: a security warning that is always present is a warning nobody reads.

### 1.2 Static analysis (SonarQube, server `192.168.1.97:9000`, project `RediensIAM`)

| Metric | Value |
|---|---|
| Lines of code | 23,316 |
| Overall coverage | 72.3 % (10,037 lines to cover, 3,137 uncovered) |
| Bugs / Reliability rating | 0 / A |
| Vulnerabilities / Security rating | 2 / **B** |
| Security hotspots | 12 total, **4 unreviewed (66.7 % reviewed)** |
| **Quality gate** | **ERROR** — `new_coverage` 64.6 % (threshold 80), `new_security_hotspots_reviewed` 66.7 % (threshold 100), `new_violations` 32 (threshold 0) |

The two open vulnerabilities are both `Web:S5725` (no Subresource Integrity) on
`frontend/admin/index.html:12` and `frontend/login/index.html:12`.

The four unreviewed hotspots are all `typescript:S5852` — regular expressions vulnerable to
super-linear backtracking — and **all four are in
`frontend/login/src/lib/sanitizeCss.ts` at lines 25, 29, 30 and 32**. That file is the client half
of the P-03 remediation. Step 11b widened its use from one page to three. Its own header comment
(`sanitizeCss.ts:2-4`) says regex sanitisation of CSS "is inherently fragile" and that the server
must validate too — which it now does (`src/Services/LoginThemeValidator.cs:49`), so the security
consequence is contained. The ReDoS exposure is not: a tenant admin can store a `custom_css` value
that makes every visitor's login page hang. **Unreviewed and open.**

**Caveat on this evidence.** The SonarQube hotspots carry `updateDate` values no later than
`2026-07-28`, and `.sonarqube/` on disk is dated 28 July. The analysis therefore **predates steps 5
through 11b**. It is valid evidence for what it covers and stale for the last five days of changes.
An auditor would reject it as current-state evidence. Re-running `./sonar-scan.sh` is a
five-minute action with a real compliance return.

### 1.3 Dependency posture (`npm audit`, executed this session)

| Workspace | critical | high | moderate | low |
|---|---|---|---|---|
| `frontend/admin` | 0 | **8** | 0 | 1 |
| `frontend/login` | 0 | **8** | 0 | 1 |

The high-severity chains are `react-router` (**twelve** distinct advisories including
GHSA-49rj-9fvp-4h2h, unauthenticated RCE via vendored turbo-stream, and four separate open-redirect
/ XSS advisories), `postcss` (XSS via unescaped `</style>`, arbitrary file read via
`sourceMappingURL`), `js-yaml`, `picomatch`, `brace-expansion`, `@babel/core`. `npm audit fix` is
offered for all of them.

Step 1's downgrade of the `react-router` finding (`R-03`, threat model rank 30) rested on a
navigation-sink analysis of all 32 `useNavigate()` call sites — that analysis is sound for the
open-redirect advisories, and step 2 agreed with it. It does not cover the eleven advisories
published since. **This is now a live, unremediated, one-command-fixable finding in the login page
of an identity provider.**

There is **no SBOM, no dependency-scanning automation, no Dependabot or Renovate configuration, and
no CI pipeline of any kind** — `ls .github` fails, and there is no `.gitlab-ci.yml`, `Jenkinsfile`
or `azure-pipelines.yml`. Every scan in this directory was run by hand.

### 1.4 Live cluster state (read-only probes, this session)

The single most important compliance fact in this document:

**The running system is not the system described by the repository.** Step 11 recorded this as
P-07. It is still true, and it is now true of five steps rather than three.

| Claim in the repo | Live reality | Evidence |
|---|---|---|
| Release-scoped default-deny NetworkPolicy (`network-policies.yaml:9-19`) | **Absent.** Five policies exist, all aged 2d7h, none named `rediensiam-default-deny-ingress` | `kubectl get networkpolicy -A` |
| Pod-level `seccompProfile: RuntimeDefault` (`deployment.yaml:21-23`) | **Absent.** Pod `securityContext` is `{}` | `kubectl get pod … -o jsonpath='{.spec.securityContext}'` |
| Image pinned by digest, `pullPolicy: IfNotPresent` (`values.yaml:7-13`) | **Neither.** `localhost:5000/rediensiam:dev`, `imagePullPolicy: Always` | same |
| `hydra.maester.enabled: false` (`values.yaml:223-224`) | **Running,** with a ClusterRole granting `list, watch, create` on **Secrets in every namespace** | `kubectl get clusterrole rediensiam-hydra-maester-role -o jsonpath='{.rules}'` |
| Admin service is `ClusterIP` (`values.yaml:50`) | **NodePort 30501,** bound on every node interface | `kubectl get svc -n default` |
| Management API denied on the public hostname (`ingress.yaml:110-165`) | **`GET /admin/` → 200**, serving the admin console | `curl -H 'Host: iam.localhost' http://192.168.1.82/admin/` |
| Step 6 CSP with `base-uri`, `form-action`, `script-src` | Live header is `default-src 'self'; style-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none';` — no `script-src`, no `base-uri`, no `form-action` | `curl -D -` against the ingress |
| Registry bound to `127.0.0.1` (step 10, `deploy.sh`) | **`0.0.0.0:5000`**, answering `HTTP 200` on `/v2/` with no authentication and no TLS | `ss -ltnp`; `curl http://localhost:5000/v2/` |
| Self-hosted fonts (step 6) | Deployed `index.html` still carries a meta CSP naming `https://fonts.googleapis.com` and `https://fonts.gstatic.com` | body of `GET /` |

Two probes that came back **200** and look alarming are not: `GET /metrics` and `GET
/api/introspect` on the public ingress both return the 1150-byte login SPA `index.html`, i.e. the
SPA fallback, not the real endpoint. `/org`, `/project` and `/service-accounts` all answered
**401** without a token — authentication holds, exactly as step 11 measured. `GET /admin/` returning
the admin console is real and is P-04 unmitigated in production topology.

**Note on the audit trail's own integrity.** Reports `01`–`08` are tracked in git at commit
`0144e1c` — genuine evidence of process. Two further commits landed during this assessment
(`d6b0714`, `9472e59`) which committed the step 9–11b *code*. But reports
**`09-infra-security.md`, `10-secrets-management.md`, `11-pentest-results.md` and
`11b-pentest-remediation.md` remain untracked** (`git status`). The four reports containing the
penetration test and its remediation — the most important documents in the set — are not under
version control. Every commit in the repository is **unsigned** (`git log --format='%G?'` returns
`N` for all).

### 1.5 Live database state (read-only queries, this session)

```
ssl                     = off
log_connections         = off
log_disconnections      = off
log_statement           = none
logging_collector       = off
log_min_duration_statement = -1
shared_preload_libraries=            (no pgaudit)
data_checksums          = off
password_encryption     = scram-sha-256
row_security            = on
server_version          = 16.14
```

```
rolname=iam  super=true  createrole=true  createdb=true  login=true  validuntil=never
```

```
pg_hba.conf:
  local   all  all                      trust
  host    all  all  127.0.0.1/32        trust
  host    all  all  ::1/128             trust
  host    all  all  all                 scram-sha-256
```

```
select count(*) from pg_policies;  →  0
```

Four facts follow, and each maps to a control failure below:

1. **One PostgreSQL role, `iam`, is a superuser with `CREATEROLE`, `CREATEDB` and no expiry, and it
   is shared by the application, Hydra and Keto.** This is the precondition of threat-model chain
   C-4 and the reason `S-5` was ranked as it was. Compromise of any one of the three components is
   compromise of all three databases, including Hydra's token store.
2. **`local all all trust`.** Anyone who can `kubectl exec` into the Postgres pod gets superuser
   without a password. That is how I ran these queries.
3. **No database audit logging of any kind.** Not connections, not statements, not durations, no
   pgaudit. There is no record that I ran these queries. There would be no record of an attacker
   running them either.
4. **Row-level security is available (`row_security = on`) and there are zero policies.** Structural
   change `S-5` — tenant isolation as a schema property rather than a per-query habit — has not
   started. Tenant isolation rests entirely on roughly 200 hand-written `WHERE` conjuncts
   (architecture review §3.1); every one of them is a place a future edit can omit one.

---

## 2. Applicability verdicts, with reasoning

### 2.1 PCI-DSS v4.0 — **does not apply to this system**

**Determination method.** I enumerated every persisted entity
(`src/Data/Entities/` — 20 classes: `AuditLog`, `BackupCode`, `EmailToken`, `Instance`,
`Organisation`, `OrgRole`, `OrgSmtpConfig`, `PersonalAccessToken`, `Project`, `Role`,
`SamlIdpConfig`, `ServiceAccount`, `ServiceAccountRole`, `User`, `UserList`, `UserProjectRole`,
`UserSocialAccount`, `WebAuthnCredential`, `Webhook`, `WebhookDelivery`) and grepped the whole of
`src/` for the vocabulary of cardholder data (`pan`, `cardholder`, `credit_card`, `card_number`,
`cvv`, `cvc`, `iban`). **Zero hits.** The architecture review's own data-classification matrix
(`03-architecture-review.md` §6) independently enumerates 21 data categories and none is payment
data.

**Verdict: RediensIAM does not store, process or transmit cardholder data. It is not a CDE system
and no PCI-DSS requirement binds it directly.**

**Producing a PCI-DSS attestation for this system would be a fabrication and this report will not
do it.**

**The one open scoping question, which the operator must answer.** PCI DSS v4.0 scoping does not
stop at systems that touch card data. It also covers *connected-to* and *security-impacting*
systems — and an identity provider that authenticates administrators of a CDE is the textbook
example of a security-impacting system. On the live cluster, RediensIAM shares the `default`
namespace with `yandee-products-svc`, `yandee-vitrine`, `yandee-client`, `yandee-tenant-svc` and
`yandee-gateway-svc`, and `yandee-gateway-svc` calls RediensIAM's `/api/introspect` pod-to-pod
(step 9 §0). I did not audit those workloads — they are not in this assessment's scope.

The decision rule is short:

> If **any** application that RediensIAM authenticates accepts, transmits or stores payment card
> data, **and** RediensIAM authenticates the people who administer that application or its
> infrastructure, then RediensIAM is a security-impacting system and falls in PCI-DSS scope.

If that is true, the requirements that bind an identity provider serving a CDE are the following —
and I have assessed them below against the current system, because they are the ones that matter
whether or not the formal scope decision goes that way:

| PCI-DSS v4.0 req. | What it demands of an IdP | Current state | Evidence |
|---|---|---|---|
| **2.2.7** | Non-console admin access encrypted with strong cryptography | **Fail.** Admin console served over HTTP on NodePort 30501 today; prod TLS is opt-in with an unmet cert-manager prerequisite, and the admin ingress issuer is `selfsigned` | live probe; `values.prod.yaml:31-33` |
| **3.5 / 3.6** | Strong crypto for stored authentication data; documented key management with defined cryptoperiods and rotation | **Partial.** Encryption itself is exemplary (§4 V6). **Key management fails:** one HKDF root for the whole deployment, **no rotation implementation for any key** | `src/Config/AppConfig.cs:95-104`; `03` §6, S-10 |
| **4.2.1** | Strong cryptography in transit over open networks | **Fail** as deployed. No TLS anywhere | `kubectl get ingress` → PORTS 80; `ssl = off` |
| **7.2 / 7.3** | Least privilege, role-based, enforced by an access-control system | **Partial pass** at the application layer (Keto ReBAC + `LiveAuthorizationService`); **fail** at the data layer (one shared Postgres superuser) | `src/Services/LiveAuthorizationService.cs`; `pg_roles` query |
| **8.3 / 8.4 / 8.5** | MFA for all administrative access; MFA not susceptible to replay | **Pass.** `RequireAdminMfa` defaults `true`; TOTP replayed codes rejected via `OtpCacheService`; WebAuthn `UserVerification = Required` | `src/Config/AppConfig.cs:72`; `11` §6 |
| **8.6** | Application and system accounts: interactive use restricted, credentials rotated | **Partial.** PAT lifetime is clamped 1–730 days, default 365 (`AppConfig.cs:64`), but **PATs created before the clamp with a null expiry still exist and never expire** | `AppConfig.cs:62-64`; `11` §6 |
| **10.2 / 10.3** | Audit trail of all administrative actions, protected from modification | **Partial.** 98 audit call sites including all seven MFA mutations; retention floored at 90 days. **But the table is ordinary, mutable, deletable, and the app's DB role is superuser. No WORM export.** | `grep RecordAsync`; `AppConfig.cs:113-119`; `pg_roles` |
| **10.4** | Daily log review | **Fail.** No log shipping, no SIEM, no alerting, one operator | no evidence exists |
| **10.6** | Time synchronisation | **Unassessed** |
| **11.3.1** | Internal vulnerability scans quarterly and after significant change | **Partial.** SonarQube exists but was last run 28 July, before five days of security changes; `npm audit` shows 8 unremediated highs; no scheduled scanning | §1.2, §1.3 |
| **12.x** | Policies, roles, awareness, incident response | **Fail.** No policy documents exist | `ls docs/` |

**A PCI attestation for a system in this state would fail on 4.2.1 alone.**

### 2.2 HIPAA — **does not apply to this system**

**Determination method.** Same entity enumeration; grepped for `phi`, `patient`, `diagnosis`,
`medical`, `health_record`, `icd10`, `ssn`. **Zero hits.** The system stores identity data:
email, username, display name, phone number, password hash, encrypted TOTP secret, WebAuthn public
keys, social-account links, and audit entries containing IP address and User-Agent.

**Two separate reasons HIPAA does not bind this system today:**

1. **No PHI.** None of the stored categories is individually identifiable health information.
2. **No covered relationship.** HIPAA's Security Rule binds Covered Entities and their Business
   Associates. RediensIAM is self-hosted by its author for his own applications. No CE exists in
   the picture, so no BA relationship exists, so no obligation attaches. This is not a technical
   fact and no code change alters it.

**The honest caveat, and it is a real one.** `User.Metadata` is a
`Dictionary<string, object>` (`src/Data/Entities/User.cs:24`) with **no schema, no validation, no
size limit and no classification**. `Project.LoginTheme` is the same shape
(`src/Data/Entities/Project.cs:13`). A tenant could put a patient identifier, a diagnosis code or a
member number in either, and the system would store it in plaintext, serve it back through the
management API, replicate it into the audit log's `Metadata` column, and never flag it. The system
does not handle PHI **by design**; it has an unbounded bag that would silently accept it. That is a
data-governance defect independent of HIPAA and it also bites GDPR Article 5(1)(c).

**What would change on the day this is sold to a healthcare customer.** RediensIAM's operator
becomes a Business Associate. A signed BAA becomes mandatory before any data flows. The following
Security Rule requirements would then bind, and here is where they stand today:

| HIPAA Security Rule | Current state | Evidence |
|---|---|---|
| §164.312(a)(1) Access control, unique user ID | **Pass** at the application layer | Keto ReBAC; `RequireManagementLevel` |
| §164.312(a)(2)(iv) Encryption at rest (addressable) | **Fail** below the application layer. Application-layer AES-256-GCM is excellent for the four secret categories that use it; **the database has no encryption, `data_checksums = off`, and no volume encryption is configured or asserted anywhere in the chart** | `AppConfig.cs:95-104`; `pg_settings`; `03` §6 header |
| §164.312(b) Audit controls | **Partial.** Application audit trail is good. **No database audit logging at all, no Kubernetes audit log, no log retention outside the same mutable table** | §1.5; `k3s server` with no flags |
| §164.312(c) Integrity | **Fail.** Audit table is mutable by the app's own superuser role; no WORM, no hash chain, no export | `pg_roles`; `src/Data/Entities/AuditLog.cs` |
| §164.312(e) Transmission security | **Fail.** No TLS anywhere as deployed | live probe |
| §164.308(a)(1) Risk analysis | **Pass, unusually well.** `02-threat-model.md` is a genuine STRIDE-per-boundary analysis with attack trees, chains and a MITRE mapping | tracked at `0144e1c` |
| §164.308(a)(3)/(4) Workforce security, authorisation | **Not applicable / fail.** One person; no onboarding, offboarding or access-review process exists | §1.4 |
| §164.308(a)(5) Security awareness and training | **Fail.** No programme | — |
| §164.308(a)(6) Incident response | **Fail.** No plan, no runbook, no breach-notification procedure | `ls docs/` |
| §164.308(a)(7) Contingency plan | **Fail.** **No backup exists.** The chart contains no backup CronJob or PVC snapshot; `find deploy -type f` shows nine templates and none of them backs anything up. There is no tested restore | `deploy/rediensiam/templates/` |
| §164.316 Policies and documentation, 6-year retention | **Fail.** Four documents in `docs/`, none of them a policy | `ls docs/` |

The absence of any backup is worth calling out separately: it is a HIPAA §164.308(a)(7) failure, a
SOC 2 A1.2 failure, a PCI 12.10 dependency, and — independent of all three — the reason a disk
failure on this one desktop would permanently destroy every tenant's identity data. It is the
highest-severity single omission in this report that no prior step named.

### 2.3 SOC 2 Type II — **applies in principle, is not achievable in this configuration**

Type II is not a configuration audit. It is an auditor's opinion that stated controls **operated
effectively over a defined period**, typically 3 to 12 months. The deliverable is not a hardened
system; it is a body of evidence that a system stayed hardened while people worked on it.

**What this system has that a Type II would credit:**

- A real risk assessment (`02-threat-model.md`), tracked in git, with an explicit methodology.
- A real architecture review (`03-architecture-review.md`) that names its own structural debt.
- A real penetration test (`11-pentest-results.md`) that **found and proved three exploitable holes
  in the previous steps' claimed fixes**, followed by a remediation report that discloses what it
  did not fix. That adversarial loop is exactly what CC4.1 (monitoring of controls) is asking for,
  and it is genuinely rare in a system this size.
- A test suite of 1,221 integration tests, all passing, executed and verified in this session,
  including 23 tests written specifically as security regression proofs.
- An application audit trail with 98 recording points covering authentication, authorisation
  changes, MFA mutations, org/project lifecycle, service accounts and webhooks.

**What it does not have, and cannot obtain by writing code:**

| Trust Services Criterion | Blocker | Why code cannot fix it |
|---|---|---|
| **CC1.3 / CC1.4** Organisational structure, competence | One person, no defined roles | An org chart with one box is not a structure |
| **CC6.3** Access removal, least privilege, **segregation of duties** | `git log --format='%ae' \| sort -u` returns **one address** for 100 % of history | The person who writes the code, reviews it, approves it, deploys it and holds production credentials is the same person. There is no technical control that makes one person two people |
| **CC7.2** Monitoring for anomalies | Prometheus metrics are exposed (`src/Program.cs:381`) and **scraped by nothing**. No alerting, no on-call, no retention | You can build the dashboard; you cannot build the human who looks at it |
| **CC8.1** Change management | No CI, no pull requests, no tickets, no approvals, no signed commits (all `%G? = N`), no release notes tied to changes | Approval requires an approver |
| **CC4.1** Ongoing evaluation | The audit trail here is a one-off exercise, not a recurring control with a schedule and an owner | — |
| **A1.2** Availability / recovery | **No backups exist at all** | This one *is* fixable technically and must be, regardless of SOC 2 |
| **CC9.2** Vendor management | No subprocessor inventory, no vendor risk reviews | — |
| **P / C series** (if in scope) | No privacy notice, no data-retention schedule, no erasure procedure | — |

**What a Type II would actually cost.** Realistic ranges for a single-product SaaS of this size:

| Item | Cost | Elapsed time |
|---|---|---|
| A second person with production access and review authority | one salary | hiring lead time |
| Policy set (18–25 documents: InfoSec, access control, change management, incident response, BC/DR, vendor management, SDLC, data classification, retention, acceptable use, …) | £3k–£10k with a template vendor, or 4–6 weeks of internal writing | 1–2 months |
| Compliance automation platform (Vanta / Drata / Secureframe or equivalent) | £6k–£15k/year | continuous |
| Readiness / gap assessment by the audit firm | £5k–£12k | 4–6 weeks |
| Remediation of the technical gaps in §7 | 6–10 engineer-weeks | 2–3 months |
| Independent penetration test by a named third party (the internal one in `11` does not satisfy an auditor) | £6k–£15k | 2–4 weeks |
| **Observation window** — the irreducible part | £0 | **3 months minimum, 6–12 typical** |
| Type II audit fieldwork and report | £12k–£30k | 6–10 weeks |
| **Total** | **≈ £32k–£82k plus a second headcount** | **12–18 months from today** |

**Recommendation.** Do not start SOC 2 readiness now. Do the three items in §0, get a backup
running, hire or partner with a second person, and revisit. In the meantime, a **SOC 2 Type I** —
a point-in-time design opinion, no observation window — is achievable in roughly 4–6 months for
£15k–£30k and would satisfy some prospects. It still requires the policy set and the second
person for CC6.3, so it is not a shortcut past the organisational blockers, only past the clock.

### 2.4 CIS Benchmarks — which apply

| Benchmark | Applies? | Scope here |
|---|---|---|
| **CIS Kubernetes Benchmark v1.9** | Partially | k3s is a distribution, not upstream Kubernetes; control-plane sections 1.x–3.x mostly map to k3s's own hardening guide, and I could not read `/etc/rancher/k3s/` or `/var/lib/rancher/k3s/server/` (`sudo` requires a password). §4 (worker/kubelet) and §5 (policies) are assessable and assessed below |
| **CIS Docker Benchmark v1.6** | Partially | The runtime is **containerd** under k3s, so §2 (daemon) and §5 (container runtime) apply to Kubernetes rather than Docker. Docker is used for the **build** and the **local registry**, so §4 (image and build file) and the registry's exposure are in scope |
| **CIS PostgreSQL 16 Benchmark** | **Yes, fully** | The bundled PostgreSQL 16.14 StatefulSet is entirely in scope and is the worst-performing area in this report |

Not applicable: CIS for cloud providers (self-hosted), CIS Distribution Independent Linux (host is
the operator's desktop and out of this assessment's scope).

---

## 3. OWASP ASVS 4.0.3 Level 2 — chapter by chapter

**This is the framework that fits.** RediensIAM is an application, ASVS is an application security
standard, and Level 2 is the right level for a system that holds authentication data for other
applications. Everything below is assessed at L2. Requirements that are L1-only are folded in;
L3-only requirements are excluded.

Legend: **✅ Met** (with evidence) · **⚠️ Partial** (met in part, gap named) · **❌ Not met** ·
**➖ N/A** · **❓ Unassessed**

### V1 — Architecture, Design and Threat Modeling

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 1.1.1 | Secure SDLC | ❌ | No CI, no pipeline, no defined process. This 12-step audit is an exercise, not an SDLC |
| 1.1.2 | Threat modelling for every design change | ⚠️ | `02-threat-model.md` is a genuine STRIDE-per-boundary model with 8 trust boundaries, 5 attack trees, 9 chains and a MITRE ATT&CK mapping — better than most commercial products. But it is one snapshot, not a per-change practice |
| 1.1.4 | Trust boundaries documented and justified | ⚠️ | Documented at `02` §2 (TB-1…TB-8) and `03` §1. **The public/admin port split was documented as a trust boundary and is not one** — `src/Program.cs:378` maps every controller on both listeners; only Swagger (`:335`) and `/metrics` (`:381-382`) are port-gated. P-04 proved it live. Now corrected at the ingress (`deploy/rediensiam/templates/ingress.yaml:110-165`) but **undeployed** |
| 1.1.5 | Security analysis for each design change | ❌ | No record exists |
| 1.1.6 | Centralised, simple, vetted security controls | ⚠️ | **Good:** `LoginThemeValidator.Validate` is the single choke point for all three theme write paths (`src/Services/LoginThemeValidator.cs:49`); `PasswordPolicyService.EvaluateAsync` is the single evaluation point for five password write paths (`src/Services/PasswordPolicy.cs:33`); `RequireReauthAsync` is the single guard for all seven MFA mutations (`src/Controllers/AccountController.cs:57`); `GatewayAuthMiddleware` is the single token gate. **Bad:** structural changes S-1 (`GetManagementLevel()` is a public extension method any code can call — `src/Middleware/GatewayAuthMiddleware.cs:88`), S-3 (audit is 98 hand-placed calls, not an action filter) and S-5 (0 RLS policies) were all recommended in step 3 and **none was done** |
| 1.2.1 | Unique low-privilege OS account per component | ✅ | `runAsNonRoot: true`, `runAsUser: 1000`, `capabilities: drop: [ALL]`, `readOnlyRootFilesystem: true` (`deployment.yaml:32-39`); confirmed on the live pod |
| 1.2.2 | Authenticated communication between components | ❌ | **Hydra's admin API `:4445` and Keto's write API `:4467` have no authentication of any kind.** The chart says so in its own comments (`network-policies.yaml:118`, `:144`: "This rule is the entire control on it"). The NetworkPolicies are the only control, and the release-scoped default-deny is **not deployed**. PostgreSQL uses one shared superuser |
| 1.2.3 | Single vetted authentication mechanism | ✅ | `src/Middleware/GatewayAuthMiddleware.cs:10-57` — one path for both token shapes |
| 1.2.4 | All authentication paths have the same strength | ⚠️ | `AdminLogin` recomputes the "has a factor" predicate inline instead of calling `HasAnyFactorAsync` — currently identical, disclosed as a drift risk in `11b` §6. This is the same defect class that produced P-02 |
| 1.4.1 | Trusted enforcement points | ⚠️ | `RequireManagementLevelAttribute` → `LiveAuthorizationService` is a real per-request enforcement point that re-checks Keto and fails closed. **But `src/Services/LiveAuthorizationService.cs:37` returns `true` unconditionally for any service-account caller**, so the entire `/api` surface has no re-check at all |
| 1.4.4 | Single access-control mechanism | ❌ | Two: Keto tuples plus a `db.OrgRoles` fallback (`LiveAuthorizationService.cs:83`, `KetoService.cs:95`). Structural change S-8, not done. Dual-write drift is undetectable by any current code path |
| 1.4.5 | Attribute/relationship-based access control | ✅ | Keto ReBAC with four namespaces (`values.yaml:293-297`) and a `role:` prefix on tenant relations to prevent collision with structural relations (`KetoService.cs:138,168,274`) — correct namespacing discipline |
| 1.5.1–1.5.4 | Serialisation, validation on a trusted layer, output encoding | ✅ | JSON only, no `BinaryFormatter`; `LoginThemeValidator` is server-side; React with no `dangerouslySetInnerHTML` or `innerHTML` anywhere (verified in `03` §9) |
| **1.6.1** | **Explicit key management policy** | ❌ | **No key rotation implementation exists for the HKDF root (`Security:TotpSecretEncryptionKey`), the Argon2 pepper, or the Hydra system secret.** Structural change S-10, not done. This is why threat chain **C-3 is unrecoverable rather than merely severe**: recovery would require rotating the signing key (logging out every user of every tenant simultaneously), re-encrypting every TOTP secret, and rotating every webhook secret, SMTP password and social client secret — none of which has a code path |
| 1.6.2 | Key material protected from unauthorised access | ⚠️ | Kubernetes Secret (`deployment.yaml:94-121`), never in git (verified: `10` §0). **But `k3s server` is running with no flags, so `--secrets-encryption` is off and Secrets sit in the datastore in plaintext** |
| 1.6.3 | Keys and passwords replaceable, no hard-coded | ⚠️ | Only the backup-code format anticipates rotation — `sha256:{keyId}:{hex}` with an explicit key id (`src/Services/PasswordService.cs:54-58`). It is the pattern every other key should copy and none does |
| 1.6.4 | Client-side secrets have no meaningful privilege | ✅ | Browser SDK keeps the access token in memory only, never `localStorage`/`sessionStorage` (verified in `11` §6) |
| 1.7.1 | Common logging format | ✅ | Structured `ILogger` plus the `AuditLog` entity with a fixed shape (`src/Services/AuditLogService.cs:23-38`) |
| **1.7.2** | **Logs transmitted to a remote system** | ❌ | No log shipping, no aggregation, no SIEM. Container logs live and die with the pod; audit rows live in the same database the application can drop |
| 1.8.1 | Sensitive data identified and classified | ✅ | `03-architecture-review.md` §6 — a 21-row classification matrix with sensitivity tier, at-rest state, in-transit state, retention, readership and deviation-from-intent per category. Genuinely audit-grade |
| 1.8.2 | Protection level defined per classification | ⚠️ | Defined; **not achieved for the in-transit and at-rest columns** |
| 1.9.1 | Encrypted communication between components | ❌ | Postgres `ssl = off` (live), Dragonfly plaintext, Hydra and Keto plain HTTP (`AppConfig.cs:146-151`). Chart supports Postgres TLS (`postgres.yaml:98-106`) but it is off by default (`values.yaml:161-162`) and undeployed |
| 1.9.2 | Authenticated communication between components | ❌ | See 1.2.2 |
| **1.10.1** | **Source control enforces authorship and authorisation** | ❌ | Authorship: one address, 100 % of history. Authorisation: **no code review, no pull requests, no signed commits** (`git log --format='%G?'` → `N` on every commit sampled) |
| 1.11.1 | Business logic documented | ✅ | `docs/ARCHITECTURE.md`, `docs/INTEGRATION.md` — the latter is unusually honest, stating "if this and the code disagree, the code wins and this document is a bug" and then writing out its own open weakness |
| 1.11.2 | No race conditions on shared state | ⚠️ | `LoginRateLimiter` uses an atomic Lua `INCR` with expiry, single round-trip, explicitly no TOCTOU (`src/Services/LoginRateLimiter.cs:15-20`) — correct. The Keto/Postgres dual-write has a disclosed unreconciled-crash residual |
| 1.12.x | File upload | ➖ | No upload surface |
| 1.14.1 | Component segregation verified | ⚠️ | NetworkPolicies exist as templates and are well-reasoned; the release-scoped default-deny is **undeployed** |
| **1.14.2** | **Binary signatures / trusted repositories** | ❌ | Base images by mutable tag, not digest (`Dockerfile:4,12,20,29` — the file's own first line says "Pin base images to digests in production" and then does not). App image digest pin exists in the chart (`values.yaml:7-13`) and is **not deployed**: live pod runs `localhost:5000/rediensiam:dev` with `pullPolicy: Always`. No cosign, no admission policy, no SBOM |
| **1.14.3** | **Build pipeline warns on out-of-date or insecure components** | ❌ | **There is no build pipeline.** 8 high-severity npm advisories are live right now |
| 1.14.4 | Build pipeline builds and verifies | ❌ | Same |
| 1.14.5 | Application deployments sandboxed/containerised | ✅ | seccomp `RuntimeDefault` in the chart (`deployment.yaml:21-23`), all capabilities dropped, read-only root filesystem, `automountServiceAccountToken: false`. **Pod-level seccomp is absent on the live pod** |
| 1.14.6 | No unsupported technologies | ✅ | .NET 10, PostgreSQL 16.14, Dragonfly 1.25.0 |

**V1 verdict: ⚠️ Partial.** Design and threat-modelling artefacts are genuinely strong — better
than most audited products. Key lifecycle, build integrity and inter-component authentication are
absent.

### V2 — Authentication

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| **2.1.1** | **Passwords at least 12 characters** | ❌ | `PasswordPolicyService.AbsoluteMinimumLength = 8` (`src/Services/PasswordPolicy.cs:31`), and `Project.MinPasswordLength` is an `int` with **default 0** (`src/Data/Entities/Project.cs:29`), so `Math.Max(0, 8) = 8` is the effective floor for every project that has not been configured. **This is a clean, unambiguous ASVS L1/L2 failure and a one-constant fix** |
| 2.1.2 | Permit at least 64 characters | ⚠️ | No maximum is enforced anywhere (`PasswordPolicy.cs:35` checks only the lower bound), so 64 is permitted. **The absence of any upper bound is its own mild issue** — an unbounded string reaching Argon2id at 64 MiB; bounded in practice by the 1 MiB Traefik body cap (`values.yaml:74`) |
| 2.1.3 | No truncation | ✅ | `Encoding.UTF8.GetBytes(password)` in full (`PasswordService.cs:122-124`) |
| 2.1.4 | Any printable Unicode allowed | ✅ | Same |
| **2.1.7** | **Breached-password check** | ⚠️ | Implemented correctly via HIBP k-anonymity (`BreachCheckService`), **but `Project.CheckBreachedPasswords` defaults to `false`** (`Project.cs:37`, a `bool` with no initialiser), so it is **off for every project until someone turns it on**. Also fails open on HIBP error — `logger.LogWarning(ex, "HIBP breach check failed — allowing password")` (`BreachCheckService.cs:34`), a defensible availability trade that should be a documented decision rather than a log line |
| 2.1.9 | No composition rules required | ⚠️ | Four optional composition toggles exist (`Project.cs:30-33`). ASVS discourages requiring them. Deviation is per-tenant and opt-in |
| 2.1.10 | No periodic rotation | ✅ | None implemented |
| 2.1.11 | Paste and password managers permitted | ✅ | By inspection — no paste-blocking JS in either SPA |
| **2.2.1** | **Anti-automation controls** | ✅ | Three layers: per-IP **and** per-user Redis counters with atomic increment (`LoginRateLimiter.cs:22-47`), persistent DB lockout (`User.FailedLoginCount`, `User.LockedUntil` — `User.cs:20-21`), and a Traefik ingress rate limit of 50/s burst 100 per source IP (`values.yaml:69-73`). The per-IP counter is deliberately **not** reset on successful login, with the reasoning written out (`LoginRateLimiter.cs:49-57`) — correct and subtle |
| 2.2.2 | Weak authenticators not used as the only factor | ⚠️ | TOTP and WebAuthn are available. **SMS is registered as `StubSmsService`** (`src/Program.cs:135`) — SMS OTP does not actually send in this deployment, so any user whose only factor is a phone is locked out of that factor in practice. This is a functional finding with a security consequence |
| 2.2.3 | Secure notifications on authentication changes | ✅ | `User.NewDeviceAlertsEnabled` defaults `true` (`User.cs:23`); `NotificationService` |
| 2.3.1 | Initial passwords random, expiring | ✅ | `EmailToken` with `InviteExpiryHours` default 72 (`AppConfig.cs:122`) |
| 2.5.1 | No default recovery secret | ⚠️ | Step 10 rewrote `deploy.sh` to generate per-install secrets, printed once. **The live cluster was deployed before that**, so its bootstrap credential is whatever the shared-default file held. `src/Program.cs:317` logs "Remove IAM_BOOTSTRAP_PASSWORD" and nothing enforces it |
| 2.5.4 | No shared or default accounts | ⚠️ | The bootstrap super-admin is a real account, not shared. But it lives in the `__system__` user list and there is no offboarding process |
| 2.5.6 | Secure password recovery | ✅ | Token-based with expiry. Note the deliberate design choice recorded in `11b` §2: reset **sets** a password on a passwordless federated user, which is the documented recovery path for the P-02 lockout |
| 2.6.x / 2.7.x | Look-up and out-of-band verifiers | ✅ | Backup codes: 8 hex chars, HMAC-SHA256 with versioned `sha256:{keyId}:{hex}` format and constant-time comparison (`PasswordService.cs:54-95`). The rationale for HMAC over Argon2 here — brute force is bounded by rate limiting, and Argon2 would amplify a DoS pivot — is stated and correct |
| **2.8.x** | **One-time verifiers (TOTP)** | ✅ | Secret encrypted with AES-256-GCM under an HKDF-derived per-purpose subkey (`AppConfig.cs:95`, `TotpEncryptionService.cs:10-18`: fresh 12-byte random nonce and 16-byte tag per encryption — correct). **Replay is prevented**: `VerifyCurrentTotpAsync` routes the proof through the same `OtpCacheService` anti-replay as login, so an observed code cannot be reused (verified in `11` §6) |
| 2.8.7 | Hardware authenticators (WebAuthn) | ✅ | `UserVerification = Required` on both registration and assertion; credential lookup scoped to the user pending MFA; sign counter persisted (verified in `11` §6) |
| 2.10.1–2.10.3 | Service authentication: no defaults, protected storage, not in source | ✅ | PATs are 40 random bytes, stored as SHA-256 (`PersonalAccessToken.cs:8`) — no salt needed at that entropy. **Verified never committed**: `git log --all -- .sonar.env` is empty and no secret-file pattern was ever tracked (`10` §0) |
| **2.10.4** | **Secrets stored in a secret vault** | ❌ | Kubernetes Secrets only, in an **unencrypted** datastore. No Vault, no external secrets operator, no envelope encryption |

**V2 verdict: ⚠️ Partial, but strong.** The authenticator implementations — TOTP, WebAuthn, backup
codes, rate limiting — are among the best-executed parts of this codebase and several are better
than the standard requires. The failures are all **defaults**: an 8-character floor, breach
checking off, tenant MFA off, SMS stubbed. Those are four constants and one service registration.

### V3 — Session Management

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 3.2.1 | New session token on authentication | ✅ | Delegated to Ory Hydra |
| 3.2.3 | Tokens in secure cookies or headers, not URLs | ⚠️ | `GET /auth/logout` takes `logout_challenge`, not a token (`src/Controllers/AuthController.cs:702-708`), so the R-29 shape appears addressed server-side. **I did not re-verify the SPA's construction of the end-session URL.** By inspection, unconfirmed |
| 3.3.1 | Logout terminates the session | ✅ | `HydraService.RevokeSessionsAsync` |
| 3.3.2 | Re-authentication after inactivity | ⚠️ | `access_token: 15m`, `refresh_token: 168h` (`values.yaml:239-244`), with Hydra's default refresh rotation and reuse detection left on. The reasoning is written out in the chart: 15 m is the residual window after a revocation, 7 d is the outer bound on a stolen unrotated chain. Defensible; **document it as a policy decision rather than leaving it as a value** |
| 3.3.4 | Terminate all other active sessions | ✅ | `src/Controllers/AccountController.cs:312` (revoke all) and `:321` (revoke one client) |
| 3.4.1–3.4.2 | Secure, HttpOnly cookie attributes | ✅ | `HttpOnly = true`, `SameSite = Strict`, `SecurePolicy = Always` (`src/Program.cs:70-73`) |
| 3.4.3 | `__Host-` or `__Secure-` cookie prefix | ❌ | Not used. Minor, one line |
| 3.5.2 | Session tokens rather than static API secrets | ⚠️ | PATs are static bearer secrets by design. Mitigated by lifetime clamping (1–730 days, default 365 — `AppConfig.cs:64`) and a cache-hit liveness re-check that skips the join but never the decision (`PatService.cs:57-68`). **Residual: PATs created before the clamp with a null expiry never expire and are still valid** |
| 3.5.3 | Stateless tokens validated by trusted signature | ⚠️ | Validated against Hydra's JWKS. **No SDK validates `aud`** — see V13 and P-06 |
| 3.7.1 | Verify session exists before sensitive operations | ✅ | `LiveAuthorizationService` re-checks Keto per request on privileged paths and fails closed on exception (`:56-62`); the cache carries the org scope in the *value* (`"1|{orgId}"`) so a scope mismatch is treated as a miss (`:45-49`) — this resisted a deliberate cache-poisoning attempt in step 11 |

**V3 verdict: ✅ Substantially met.** Delegating to Hydra is the right call and the cookie and
revocation handling is correct.

### V4 — Access Control

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 4.1.1 | Enforced on a trusted service layer | ✅ | `RequireManagementLevelAttribute` → `LiveAuthorizationService` |
| **4.1.2** | **User/data attributes not manipulable by the user** | ⚠️ | **P-01 was exactly this failure and it is now fixed** — the org-ownership check moved out of the OrgAdmin branch to a single rule that runs for every non-SuperAdmin caller (`src/Controllers/ServiceAccountController.cs:346-347`), verified by two passing regression tests. **P-05 remains open:** `IntrospectionController.Authorize` guards only the `System` namespace and passes caller-supplied `body.Object` to Keto unchecked, so an org-scoped gateway can enumerate relations on objects in any org. Rated 4.3 because the subject is always the presented token's user |
| 4.1.3 | Least privilege | ⚠️ | **`src/Services/LiveAuthorizationService.cs:37` — `if (claims.IsServiceAccount) return true;`** No Keto re-check ever runs for a service-account credential, and `/api/introspect` and `/api/authorize` gate only on `IsServiceAccountCaller()`. There is no per-scope model for service accounts |
| 4.1.5 | Fail securely | ✅ | `KetoService.cs:25` fail-closed on non-2xx; `LiveAuthorizationService` fail-closed on exception; `src/Program.cs:455-460` **throws at startup** if `App__TrustedProxies` is unset in Production rather than silently trusting RFC1918 — a fail-closed decision that costs operator convenience and should stay |
| **4.2.1** | **Protection against IDOR / horizontal escalation** | ⚠️ | Enforced, but **as a habit rather than a property**: ~200 hand-written `WHERE` conjuncts, **0 row-level security policies** (verified live). Every new query is a new opportunity to omit one. Structural change S-5 not started. The 1,221-test suite is what currently substitutes for a schema guarantee |
| 4.2.2 | CSRF protection | ✅ | The management API is bearer-authenticated with no ambient cookie authority. The ASP.NET session holds MFA state only and is `SameSite=Strict`. `SamlController.cs:65` disables antiforgery for the SAML ACS POST binding — correct, and reviewed as SAFE in SonarQube |
| 4.3.1 | Administrative interfaces use MFA | ✅ | `Security:RequireAdminMfa` defaults `true` (`AppConfig.cs:72`), with the reasoning written out: the admin surface where `super_admin` lives previously asked for MFA only when the account happened to have a factor |
| 4.3.2 | No directory browsing | ✅ | |
| 4.3.3 | Additional authorisation for high-value operations | ⚠️ | `RequireReauthAsync` covers all seven MFA mutations (`AccountController.cs:57`). **Org suspension revokes sessions but does not remove authority** — the Keto tuple `Organisations:{orgId}#org_admin` survives, and neither `RequireManagementLevelAttribute` nor `LiveAuthorizationService` consults `Organisation.Active`. A system-list org_admin can log back in and keep managing a suspended org (`11b` §4, explicitly left open) |

**V4 verdict: ⚠️ Partial.** The enforcement points are real and they held against nine attack chains
in step 11. The gaps are structural, not incidental: no schema-level tenant isolation, no
authorisation re-check for service accounts, and suspension that does not suspend.

### V5 — Validation, Sanitization and Encoding

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 5.1.1 | Mass-assignment defences | ✅ | Explicit DTO records per endpoint; no entity binding |
| 5.1.3–5.1.4 | Positive (allow-list) validation | ⚠️ | `login_theme` and `User.Metadata` are free-form `Dictionary<string, object>`. Theme values are guarded by a **deny-list** — `ForbiddenValueChars = ";{}()<>\"'`\\"` plus a 120-char cap (`LoginThemeValidator.cs:27,77-79`) — applied key-agnostically, which is the right call for a growing key set. ASVS prefers allow-lists. **`User.Metadata` has no validation at all** |
| 5.2.1 | Sanitise untrusted HTML | ➖ | No HTML sink in either SPA (verified in `03` §9) |
| 5.2.2 | Sanitise unstructured data | ⚠️ | `sanitizeCss.ts` is a six-stage deny-list regex chain whose own header says it "cannot be relied upon alone". **Four unreviewed ReDoS hotspots at lines 25, 29, 30, 32.** Security containment is provided by the server-side validator; **availability is not** |
| **5.2.6** | **SSRF protection** | ✅ | `WebhookUrlValidator.CreateSsrfSafeHandler` validates the address **actually connected to** rather than a separate DNS lookup, closing the TOCTOU (`src/Program.cs:105-111`), applied to the webhook client, the social-login client and the SAML/HIBP client. Network layer echoes it: egress `except` list covers RFC1918, link-local, loopback **and 100.64.0.0/10 CGNAT** (`values.yaml:119-125`) — the Tailscale range the earlier list omitted. Genuine defence in depth |
| 5.3.1 | Output encoding contextual | ✅ | React |
| 5.3.3 | XSS defences | ⚠️ | CSP is present and reasonably tight (`src/Program.cs:428-436`): `default-src 'self'`, `script-src 'self'` with no inline escape, `object-src 'none'`, `frame-ancestors 'none'`, `base-uri 'self'`, `form-action 'self'`. `style-src 'unsafe-inline'` is a real widening whose safety now rests on `LoginThemeValidator` being key-agnostic (`LoginThemeValidator.cs:67-72`) — which it is. **Residual, disclosed:** `login_theme.providers[]` is an array of objects whose members are **not** validated, and `Login.tsx:229` renders `providers[].logo_url` as `<img src>` under `img-src 'self' data: https:`. A tenant admin can point a provider icon at an arbitrary HTTPS host and harvest the IP and User-Agent of everyone who loads that login page. Costed at ~5 lines in `11b` §6 and left undone |
| 5.3.4 | SQL injection | ✅ | EF Core parameterised; three raw-SQL sites are parameterised or constant (`03` §9) |
| 5.3.8 | OS command injection | ✅ | No process execution |
| 5.3.9 | Local file inclusion | ✅ | |
| 5.5.1 | Deserialisation of untrusted data | ✅ | JSON only |

**V5 verdict: ✅ Substantially met.** Injection posture is genuinely clean. The two open items —
nested provider values and the ReDoS hotspots — are both small and both known.

### V6 — Stored Cryptography

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| **6.1.1** | **Regulated private data stored encrypted at rest** | ⚠️ | **Application-layer encryption is exemplary.** AES-256-GCM under four independent HKDF-SHA256 subkeys with distinct purpose strings — `rediensiam-totp-secret-v1`, `rediensiam-webhook-secret-v1`, `rediensiam-smtp-password-v1`, `rediensiam-theme-secret-v1` (`AppConfig.cs:95-104`) — so compromise of one purpose does not cross to another. **Below that layer there is nothing:** no TDE, `data_checksums = off`, no volume encryption configured or asserted anywhere in the chart. Email, phone, display name, social account links, and audit-log IP addresses and User-Agents are plaintext rows |
| 6.2.1 | Crypto failures do not enable attacks | ✅ | |
| 6.2.2 | Industry-proven algorithms | ✅ | Argon2id, AES-256-GCM, HMAC-SHA256, HKDF-SHA256, SHA-256 |
| 6.2.3 | Random IV per encryption | ✅ | `RandomNumberGenerator.GetBytes(12)` per call, 16-byte tag (`TotpEncryptionService.cs:12-18`) |
| 6.2.4–6.2.5 | No weak modes, no ECB | ✅ | GCM only |
| 6.2.7 | Authenticated encryption | ✅ | AES-GCM |
| 6.2.8 | Constant-time comparison of secrets | ✅ | `CryptographicOperations.FixedTimeEquals` on every secret comparison (`PasswordService.cs:37,92`) |
| 6.3.1 | CSPRNG | ✅ | `RandomNumberGenerator` throughout |
| 6.3.3 | Approved random-number generators | ✅ | Note the Rust SDK's authorisation cache key was FNV-1a and is now SHA-256 with a known-answer test (`sdk/rust/.../lib.rs:271`) — R-28 verified closed in step 11 |
| **6.4.1** | **Key management per a documented standard** | ❌ | **The single largest cryptographic gap.** One HKDF root key for the entire deployment and every tenant. No rotation implementation for it, for the Argon2 pepper, or for the Hydra system secret. No cryptoperiod defined. Only the backup-code format anticipates rotation |
| 6.4.2 | Key material not in code | ✅ | Env-only, verified never committed |

**V6 verdict: ⚠️ Partial.** The primitives are chosen and used correctly — this is the strongest
technical area of the codebase. The **lifecycle** around them does not exist, and that is what turns
chain C-3 from severe into unrecoverable.

### V7 — Error Handling and Logging

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 7.1.1 | No sensitive data in logs | ✅ | Sampled every `Log*` call touching `token`, `password`, `secret`, `pepper` — three hits, all logging exception context or status codes, none logging a value (`BreachCheckService.cs:34`, `WebhookService.cs:263`, `SocialLoginService.cs:255`) |
| **7.1.3** | **Security-relevant events logged** | ✅ | **98 audit call sites.** All seven MFA factor mutations now record — `totp_setup_started`, `totp_enabled`/`totp_replaced`, `backup_codes_regenerated`, `phone_verified`, `phone_removed`, `passkey_registered`, `passkey_removed` (`AccountController.cs:225,264,287,356,370,471,500`). **Threat-model finding T-N2 — "no audit record on any MFA mutation", ranked #4 at L5×I4 — is discharged, verified by reading every call site** |
| 7.1.4 | Log records contain necessary metadata | ✅ | Actor, action, target type and id, org, project, IP, User-Agent, timestamp, arbitrary metadata (`AuditLogService.cs:23-38`) |
| 7.2.1 | All authentication decisions logged | ✅ | 14 sites in `AuthController` |
| 7.2.2 | All access-control failures logged | ⚠️ | Out-of-scope introspection and authorisation attempts are recorded with dedicated actions (`api.introspect.out_of_scope`, `api.authorize.out_of_scope`). A 403 from `RequireManagementLevel` is not universally audited |
| 7.3.1 | Log encoding to prevent injection | ⚠️ | Audit metadata is a structured JSON column, so the audit trail is safe. Plain `ILogger` sinks take raw strings; low risk with no downstream log processor |
| **7.3.3** | **Logs protected from unauthorised modification** | ❌ | `audit_logs` is an ordinary mutable table in a database whose application role is a **superuser**. No append-only constraint, no hash chain, no WORM export, no off-host copy. Retention is now floored at 90 days and clamped at both the write path and the purge path (`AppConfig.cs:113-119`; `AuditLogRetentionService.cs:44` — "this is the only code that deletes, and a row set directly in the database must not be able to make the cutoff be now"), which closes T-N4. **But the floor protects against a bad setting, not against a `DELETE`** |
| 7.4.1 | Generic error message | ✅ | `AppExceptionMiddleware.cs:56` returns `{"error":"internal_error"}` and logs the detail server-side |

**V7 verdict: ⚠️ Partial.** The application audit trail is the best evidence artefact this system
produces and it is now genuinely comprehensive. It is stored in a way that provides no integrity
guarantee and is never copied anywhere.

### V8 — Data Protection

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 8.1.1 | Sensitive data not cached client-side | ❓ | No `Cache-Control` header observed on the live probe of `/`. API responses unassessed |
| 8.2.1 | Anti-caching headers on sensitive responses | ❌ | Not observed |
| 8.3.1 | Sensitive data in the body, not the query string | ⚠️ | See V3 3.2.3 |
| 8.3.4 | Inventory of sensitive data | ✅ | `03` §6 |
| 8.3.5 | Access to sensitive data audited | ⚠️ | Mutations audited; **reads are not** |
| **8.3.7–8.3.8** | **Retention and deletion of personal data** | ❌ | **No retention policy for user PII exists.** `03` §6 row 13: rows persist until an admin deletes them. Hard delete only, no anonymisation path, no erasure schedule — GDPR Article 17 is a manual database operation with no procedure written down. Audit-log retention is bounded (90–3650 days) but **user data is not** |

**V8 verdict: ❌ Not met.** Data is classified, which is the hard part. Nothing acts on the
classification.

### V9 — Communications

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| **9.1.1** | **TLS for all client connectivity** | ❌ | `kubectl get ingress` shows PORTS **80** only. `curl` against the public ingress succeeds over cleartext HTTP and returns no `Strict-Transport-Security` header, because `src/Program.cs:415` correctly gates HSTS on `ctx.Request.IsHttps` and the request never is. `values.prod.yaml:30-33` enables TLS but requires a ClusterIssuer that "must already exist" while `certManager.acme.enabled: false` — **so TLS in production is opt-in with an unmet prerequisite**. `values.dev.yaml` states plainly that dev stays cleartext because `iam.localhost` cannot be certified. **The whole authentication surface of an identity provider answers on unencrypted HTTP today** |
| 9.1.2–9.1.3 | Strong ciphers only, old TLS disabled | ❓ | Traefik defaults, not configured, not assessable without TLS being on |
| 9.2.1–9.2.2 | TLS on backend connections | ❌ | Postgres `ssl = off` (live); Dragonfly plaintext with password auth only; Hydra and Keto plain HTTP with an explicit `#pragma warning disable S5332` (`AppConfig.cs:146`). Chart support exists for Postgres (`postgres.yaml:98-106`) and Dragonfly, both off by default, both undeployed. The Dragonfly one is flagged in the chart as a **hard cutover** — `--tls` stops it accepting cleartext, so the `cacheUrl` secret must change in the same operation |
| 9.2.3 | Authenticated inter-component communication | ❌ | See V1 1.2.2 |

**V9 verdict: ❌ Not met.** This is the single largest gap in the report and it fails every one of
the five frameworks simultaneously.

### V10 — Malicious Code

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 10.2.1 | No unauthorised phone-home | ⚠️ | Outbound calls are intentional and documented (HIBP range API, social IdPs, SAML metadata, tenant webhooks), all through the SSRF-safe handler. **The deployed login page still loads Google Fonts** — its `index.html` meta CSP names `fonts.googleapis.com` and `fonts.gstatic.com`. Step 6 self-hosted them; that build is not deployed |
| 10.3.2 | Signed integrity for code and updates | ❌ | Unsigned commits, no cosign, no admission policy, **no Subresource Integrity** (SonarQube `Web:S5725` on both `index.html:12`) |
| 10.3.3 | Subdomain-takeover protection | ❓ | Unassessed |

**V10 verdict: ❌ Not met** on integrity; the phone-home posture is fine.

### V11 — Business Logic

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 11.1.1 | Sequential processing of business flows | ✅ | Login → MFA is session-state-bound; step 11 could not desynchronise it |
| 11.1.2 | Business limits enforced | ✅ | Login rate limits, SMS window (`MaxSmsPerWindow` 3 / 10 min), PAT lifetime clamp, audit-retention clamp, invite expiry, export rate limit — all in `AppConfig.cs` and all clamped rather than merely defaulted, specifically so a mutable DB row cannot disable a control (`AppConfig.cs:44-46`) |
| 11.1.4 | Anti-automation | ✅ | See V2 2.2.1 |
| 11.1.5 | Business logic limits documented | ⚠️ | `docs/INTEGRATION.md` documents the token contract and its weaknesses honestly; limits themselves are documented in code comments rather than a spec |

**V11 verdict: ✅ Met.** The clamping discipline in `AppConfig` — every operator-tunable security
parameter bounded to a range in which it is still a control — is a pattern worth naming as a
strength.

### V12 — Files and Resources

Largely ➖ **N/A**: no file upload surface, no user-supplied file storage, no dynamic includes.
12.6.1 (SSRF) is ✅ — see V5 5.2.6. CSV export exists (`src/Services/CsvWriter.cs`); its
`Content-Disposition` handling is ❓ **unassessed**.

### V13 — API and Web Service

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 13.1.1 | Consistent encoding | ✅ | JSON, `SnakeCaseLower` (`src/Program.cs:139`) |
| 13.1.3 | URLs do not expose sensitive information | ✅ | |
| 13.1.4 | Authorisation at URI and resource level | ⚠️ | Both exist; the resource-level check is bypassed for service accounts (V4 4.1.3) |
| 13.1.5 | Unexpected content types rejected | ⚠️ | `[FromForm]` on introspect per RFC 7662; general content-type strictness not enforced |
| 13.2.1 | Valid RESTful methods | ✅ | |
| 13.2.2 | Schema validation | ⚠️ | DTO binding only; no JSON Schema |
| 13.2.5 | REST rate limiting | ✅ | Traefik plus application layer |
| 13.2.6 | Message payload signing | ✅ | Webhooks HMAC-signed from an encrypted per-webhook secret (`Webhook.cs:9`) |
| **13.1.x** | **Token audience binding for relying parties** | ❌ | **P-06 / structural change S-2, open.** `aud` is not mandatory, and **no SDK offers an expected-project or audience binding**: neither the .NET `RediensIamOptions` nor the Rust `Config` has such a field (verified by grep across `sdk/`). A resource server behind a deployment-scoped `__system__` gateway credential receives `active: true` for **every tenant's** token and must remember to compare `project_id` itself. `TokenInfo.HasProjectRole` makes *role* checks safe by construction; **nothing makes the *tenant* check safe by construction.** This is the residual half of chain C-1 — the finding the product cannot detect and cannot remediate at its relying parties |
| 13.4.x | GraphQL | ➖ | Not used |
| — | SAML XML processing (XXE, signature wrapping) | ❓ | **Unassessed.** `SamlService` uses ITfoxtec; entity-resolution settings and signature-wrapping resistance were not reviewed by any step in this directory. **This is a named blind spot** |

**V13 verdict: ⚠️ Partial**, with one important open item (audience binding) and one blind spot
(SAML XML).

### V14 — Configuration

| Req | Requirement | Status | Evidence |
|---|---|---|---|
| 14.1.1 | Automated, repeatable build and deploy | ⚠️ | `deploy/deploy.sh` is a real, idempotent script that generates per-install secrets and refuses to deploy without a resolved image digest. **There is no CI**, so nothing runs it but a human |
| 14.1.3 | Hardened deployment builds | ✅ | `values.prod.yaml` correctly differs: ClusterIP admin service, TLS on, Hydra `dev: false` and `dangerousForceHttp: false`, and the `http://localhost:30501` CORS origin removed (chain C-9 closed) |
| 14.1.4 | Environment separation | ⚠️ | Separated by values files. **One cluster, one namespace, dev configuration live** |
| **14.2.1** | **Components up to date** | ❌ | 8 high-severity npm advisories per SPA, `npm audit fix` available |
| 14.2.2 | Unneeded features removed | ⚠️ | Swagger gated to the admin port (`src/Program.cs:335`); `hydra.maester.enabled: false` in the chart with an excellent written rationale (`values.yaml:216-224`). **The live cluster still runs hydra-maester with a ClusterRole granting `list, watch, create` on Secrets in every namespace** — a cluster-wide Secret read for a reconciliation loop with nothing to reconcile |
| 14.2.3 | Third-party asset integrity | ❌ | No SRI (`Web:S5725` ×2) |
| **14.2.4–14.2.5** | **SBOM / component inventory** | ❌ | None |
| 14.3.1–14.3.2 | No debug info, debug modes off in prod | ✅ | `internal_error` only; `dev: false` in prod values |
| 14.3.3 | No version/technology disclosure | ❌ | `Server: Kestrel` present on every live response |
| 14.4.1 | Content-Type with charset | ⚠️ | |
| 14.4.3 | Content Security Policy | ✅ | `src/Program.cs:428-436` — see V5 5.3.3 |
| 14.4.4 | `X-Content-Type-Options: nosniff` | ✅ | `src/Program.cs:411`, confirmed live |
| 14.4.5 | HSTS | ⚠️ | Correctly implemented (`:415-416`) and **never fires**, because nothing is HTTPS |
| 14.4.6 | Referrer-Policy | ✅ | `strict-origin-when-cross-origin` (`:412`), confirmed live |
| 14.4.7 | Frame-ancestors / X-Frame-Options | ✅ | `DENY` plus `frame-ancestors 'none'` (`:417`, `:431`), confirmed live |
| 14.5.1 | Unexpected HTTP methods rejected | ❓ | |
| 14.5.2–14.5.3 | CORS Origin validation | ✅ | Single named origin, no wildcard (`src/Program.cs:174-176`, `AppConfig.cs:32`) |
| **14.5.4** | **Forwarded headers only from trusted proxies** | ✅ | `ConfigureForwardedHeaders` clears defaults and **throws at startup in Production** if `App__TrustedProxies` is unset, with the reasoning written out: silently trusting RFC1918 lets any in-cluster pod spoof `X-Forwarded-For` and bypass per-IP rate limiting (`src/Program.cs:443-462`). Correct, fail-closed, and inconvenient on purpose |

**V14 verdict: ❌ Not met.** Security headers and CORS are done well. Build integrity, dependency
currency and component inventory are absent.

### ASVS L2 summary

| Chapter | Verdict |
|---|---|
| V1 Architecture | ⚠️ Partial — excellent artefacts, absent key lifecycle and build integrity |
| V2 Authentication | ⚠️ Partial — strong implementations, weak defaults |
| V3 Session Management | ✅ Substantially met |
| V4 Access Control | ⚠️ Partial — real enforcement points, no structural isolation |
| V5 Validation & Encoding | ✅ Substantially met |
| V6 Stored Cryptography | ⚠️ Partial — exemplary primitives, no lifecycle |
| V7 Error Handling & Logging | ⚠️ Partial — comprehensive trail, no integrity or off-host copy |
| V8 Data Protection | ❌ Not met — no retention or erasure |
| V9 Communications | ❌ Not met — no TLS anywhere |
| V10 Malicious Code | ❌ Not met — no code-integrity controls |
| V11 Business Logic | ✅ Met |
| V12 Files & Resources | ➖ Mostly N/A |
| V13 API & Web Service | ⚠️ Partial — audience binding open, SAML unassessed |
| V14 Configuration | ❌ Not met — no pipeline, no SBOM, stale dependencies |

**Approximately 70 % of applicable L2 requirements are met with evidence.** An ASVS L2 verification
report today would be issued with material exceptions in V8, V9, V10 and V14 — and V9 alone is
disqualifying for any verification claim, because "encrypted in transit" is not a control you can
be partially compliant with.

---

## 4. CIS Kubernetes Benchmark

**Scope note.** k3s consolidates the control plane into one binary; upstream sections 1.x
(API server, controller manager, scheduler), 2.x (etcd) and 3.x (control-plane configuration) map
to k3s's own hardening guide rather than to file-permission checks on `/etc/kubernetes/manifests`,
which does not exist here. I could not read `/etc/rancher/k3s/config.yaml`,
`/etc/rancher/k3s/registries.yaml` or `/var/lib/rancher/k3s/server/` because `sudo` requires a
password. **Those are marked ❓ and are not assumed to be either compliant or non-compliant.**

The single most informative control-plane fact I could establish: `ps -eo args` shows the server
process is exactly **`/usr/local/bin/k3s server`** — no flags. k3s defaults therefore apply.

| Section | Control | Status | Evidence |
|---|---|---|---|
| 1.2.x | API server flags (anonymous-auth, admission plugins, audit) | ❓ | Not readable. But no `--kube-apiserver-arg` was passed, so k3s defaults apply |
| **1.2.22–1.2.24** | **Audit logging enabled with retention** | ❌ | k3s does **not** enable API audit logging by default and no flag was passed. **There is no record of any change made to this cluster, by anyone, ever.** This is simultaneously a CIS failure, a SOC 2 CC7.2 failure and a HIPAA §164.312(b) failure |
| **2.x** | **etcd / datastore encryption at rest** | ❌ | `--secrets-encryption` was not passed. **Every Kubernetes Secret — including the HKDF root key, the Argon2 pepper, the database password, the Hydra system secret and the bootstrap admin password — is stored unencrypted in the datastore** |
| 3.2.1 | Minimal audit policy | ❌ | See 1.2.22 |
| 4.2.1 | Kubelet anonymous auth disabled | ✅ | `authentication.anonymous.enabled = false` (kubelet `configz`) |
| 4.2.2 | Kubelet authorization not `AlwaysAllow` | ✅ | `authorization.mode = "Webhook"` |
| 4.2.4 | Read-only port disabled | ✅ | `readOnlyPort = null` |
| 4.2.5 | `streamingConnectionIdleTimeout` non-zero | ✅ | `4h0m0s` (CIS wants non-zero; 4 h is generous but compliant) |
| 4.2.6 | `protectKernelDefaults` | ❌ | `null` |
| 4.2.7 | `makeIPTablesUtilChains` | ✅ | `true` |
| 4.2.9 | `eventRecordQPS` | ✅ | `50` |
| 4.2.10 | Kubelet TLS cert/key | ✅ | x509 client CA configured |
| 4.2.12 | Strong cipher suites | ❌ | `tlsCipherSuites = null` (Go defaults) |
| 4.2.13 | Certificate rotation | ⚠️ | `rotateCertificates = null`, `serverTLSBootstrap = null` — k3s manages its own rotation |
| — | `seccompDefault` | ❌ | `false`. Workloads must opt in individually |
| 5.1.1 | `cluster-admin` used only where required | ✅ | Three bindings, all legitimate: `system:masters`, and two Helm traefik service accounts |
| **5.1.2** | **Minimise access to Secrets** | ❌ | **`rediensiam-hydra-maester-role` grants `list, watch, create` on `secrets` in `apiGroups: [""]` cluster-wide,** to a pod with a mounted service-account token and zero `OAuth2Client` custom resources to reconcile. Disabled in the chart (`values.yaml:223-224`), **live in the cluster** |
| 5.1.5 | Default service account not used | ⚠️ | `automountServiceAccountToken: false` on the app (verified live), Postgres, and Keto (`values.yaml:288-290`, with a good explanation of the subchart ternary bug that made the obvious fix not work). hydra-maester still mounts one |
| 5.1.6 | Service-account tokens mounted only where needed | ⚠️ | Same |
| **5.2.x** | **Pod Security Standards enforced** | ❌ | **The `default` namespace has no Pod Security Admission labels at all** (`kubectl get ns default -o jsonpath='{.metadata.labels}'` returns only `kubernetes.io/metadata.name`). Nothing prevents a privileged, host-networked, root container from being scheduled |
| 5.2.2–5.2.9 | No privileged / hostPID / hostNetwork / root, capabilities dropped | ✅ | For RediensIAM's own pods: `allowPrivilegeEscalation: false`, `runAsNonRoot: true`, `runAsUser: 1000`, `readOnlyRootFilesystem: true`, `drop: [ALL]` (`deployment.yaml:32-39`, confirmed live). **Enforced by the chart's own discipline, not by an admission control** |
| **5.3.2** | **Network policies defined for all namespaces** | ⚠️ | Five well-designed policies are live and cover app, Hydra, Keto, Postgres and Dragonfly egress and ingress. **The release-scoped default-deny is not deployed**, so a pod of this release with no explicit policy is unrestricted. Namespace-wide default-deny is deliberately **not** used because `default` also holds eight unrelated pods — the right call, and a symptom of the real problem: **this release should be in its own namespace** |
| 5.4.1 | Prefer secrets as files over env vars | ❌ | All nine secrets are injected as environment variables (`deployment.yaml:84-121`). Env vars appear in `/proc/<pid>/environ`, crash dumps and debug endpoints |
| 5.4.2 | External secret storage | ❌ | Kubernetes Secrets only, unencrypted at rest |
| 5.7.1 | Namespace boundaries between workloads | ❌ | RediensIAM shares `default` with eight `yandee-*` pods |
| 5.7.2 | seccomp profile on pods | ⚠️ | `RuntimeDefault` in the chart (`deployment.yaml:21-23`, `postgres.yaml:78-79`) — **absent on the live pod** (`securityContext: {}`) |
| 5.7.3 | SecurityContext applied to pods and containers | ✅ | Container level, live |
| 5.7.4 | Default namespace not used | ❌ | It is |

**CIS Kubernetes verdict: ⚠️ Partial, and the gap between chart and cluster is the story.** The
Helm chart is a good piece of Kubernetes security engineering — the reasoning in
`network-policies.yaml` and `values.yaml` is better than most production charts. The cluster is
running something older and weaker. **Four of the seven ❌ rows above are already fixed in the
repository and unfixed in reality.**

---

## 5. CIS Docker Benchmark

Applies to the image build and the local registry. Runtime controls are covered by CIS Kubernetes
§5 above, since the runtime is containerd.

| Section | Control | Status | Evidence |
|---|---|---|---|
| 4.1 | Container runs as a non-root user | ✅ | `Dockerfile:34` — `USER app` |
| **4.2** | **Trusted, verified base images** | ⚠️ | `node:20-alpine` ×2 and `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0` — reputable publishers, **all four referenced by mutable tag, none by digest** (`Dockerfile:4,12,20,29`). The file's own line 1 says "Pin base images to digests in production" |
| 4.3 | No unnecessary packages | ✅ | Multi-stage build; the runtime stage copies only `/publish` |
| 4.6 | HEALTHCHECK instruction | ⚠️ | Absent; Kubernetes startup, readiness and liveness probes cover it (`deployment.yaml:122-148`) |
| 4.7 | No `update` instructions alone | ✅ | |
| 4.9 | COPY rather than ADD | ✅ | |
| 4.10 | No secrets in the image | ✅ | All nine secrets injected at runtime |
| 4.11 | Verified packages only | ❌ | `npm ci` without `--ignore-scripts` (`Dockerfile:7,17`) — an install-time script in any transitive dependency executes in the build. Structural change S-7 named this and it was not done |
| — | **Registry authentication and TLS** | ❌ | The local registry answers `HTTP 200` on `/v2/` with **no authentication and no TLS**, and `ss -ltnp` confirms it is bound to **`0.0.0.0:5000`**, i.e. reachable from the whole LAN. Step 10's `REGISTRY_BIND=127.0.0.1` fix exists in `deploy.sh` and has not been applied to the running container. Combined with the live pod's `imagePullPolicy: Always` on a mutable tag, **whoever answers for that registry owns every pod restart** — this is threat chain C-3, the only chain that reaches both root secrets in one step, and it is live |
| 5.x | Runtime hardening | ✅ | See CIS Kubernetes §5.2 |

**CIS Docker verdict: ❌ Not met.** Image construction is clean; supply-chain integrity is absent
and the registry exposure is the highest-severity live infrastructure finding in this report.

---

## 6. CIS PostgreSQL 16 Benchmark

Assessed by direct read-only query against the running instance.

| Section | Control | Status | Evidence |
|---|---|---|---|
| 1.x | Installation and patches | ✅ | 16.14, current |
| **2.x** | **Directory and file permissions** | ⚠️ | Container runs as uid/gid 70, `runAsNonRoot`, `fsGroup: 70` (`postgres.yaml:73-79`). **`readOnlyRootFilesystem: false`** with the reason given as "keep RW for simplicity" (`postgres.yaml:85`) |
| **3.1.x** | **Logging: `logging_collector`, destination, retention** | ❌ | `logging_collector = off`. Nothing is collected |
| **3.1.x** | **`log_connections`, `log_disconnections`** | ❌ | Both `off`. **There is no record of who connected to this database or when** |
| 3.1.x | `log_statement` | ❌ | `none` |
| 3.1.x | `log_min_duration_statement` | ❌ | `-1` (disabled) |
| 3.1.x | `log_line_prefix` includes user, database, host | ❌ | `%m [%p]` — timestamp and PID only. No user, no database, no application name, no remote host |
| 3.1.x | `log_hostname` | ❌ | `off` |
| **3.2** | **pgaudit installed and configured** | ❌ | `shared_preload_libraries` is empty. **No database audit trail of any kind exists** |
| **4.x** | **Least privilege: no superuser for applications** | ❌ | **One role, `iam`, with `SUPERUSER`, `CREATEROLE`, `CREATEDB`, no `VALID UNTIL`, shared by the application, Hydra and Keto.** This is the precondition of threat chain C-4 and the reason structural change S-5's phase 2 (per-component roles) was recommended |
| 4.x | Role password expiry | ❌ | `validuntil = never` |
| **5.x** | **Authentication: `pg_hba.conf` uses no `trust`** | ❌ | **`local all all trust`** plus `trust` on both loopback lines. Anyone who can exec into the pod is superuser without a password — which is how the queries in this report were run. Remote connections use `scram-sha-256`, which is correct |
| 6.2 | `ssl = on` | ❌ | `off`. Chart supports it (`postgres.yaml:2,98-106` via a cert-manager Certificate) and it is disabled by default (`values.yaml:161-162`) with an honest note that `sslmode=require` is "the honest ceiling here" under a self-signed issuer |
| 6.7 | `data_checksums` | ❌ | `off`. Silent corruption is undetectable |
| 7.x | Replication | ➖ | Single instance, no replication configured |
| **8.x** | **Row-level security for multi-tenant separation** | ❌ | `row_security = on` (the feature is available); **`select count(*) from pg_policies` returns 0**. Tenant isolation is entirely in application code |
| — | **Backup and recovery** | ❌ | **No backup exists.** No CronJob, no `pg_dump`, no PVC snapshot, no WAL archiving. Nine chart templates and none of them backs anything up |

**CIS PostgreSQL verdict: ❌ Not met.** This is the worst-performing area of the assessment.
Thirteen of sixteen assessable controls fail. Three of them — the shared superuser, `trust`
authentication, and the total absence of both audit logging and backups — are individually serious
and jointly mean that the database holding every tenant's identity data has no access control below
the application, no forensic record, and no recovery path.

**This section should be read alongside the fact that the same database holds Hydra's OAuth2 token
store and Keto's authorisation tuples.** Compromise of the `iam` role is compromise of
authentication, authorisation and the audit trail simultaneously — including the ability to delete
the record of having done it.

---

## 7. Gap analysis

Ranked by **severity × effort**: highest severity per unit of effort first. Effort is **S** (under
a day), **M** (under a week), **L** (multiple weeks).

### 7.1 Technical gaps

| # | Gap | Severity | Effort | Frameworks | Evidence |
|---|---|---|---|---|---|
| **T-01** | **Deploy the hardened manifests.** Five steps of work exist only on disk: default-deny NetworkPolicy, pod seccomp, digest pinning, `pullPolicy: IfNotPresent`, hydra-maester off, ClusterIP admin service, ingress admin-path deny, step 6 CSP and self-hosted fonts | **Critical** | **S** | All five | §1.4 |
| **T-02** | **Rebind the registry to `127.0.0.1`.** Live on `0.0.0.0:5000`, no auth, no TLS, with `pullPolicy: Always` on a mutable tag. This is chain C-3 and it is armed | **Critical** | **S** | CIS Docker, ASVS V1.14, PCI 6.3 | `ss -ltnp`; `curl` |
| **T-03** | **Take a backup.** None exists. A disk failure destroys every tenant's identity data permanently | **Critical** | **S** | SOC 2 A1.2, HIPAA §164.308(a)(7), CIS PG | `deploy/rediensiam/templates/` |
| **T-04** | **Fix the PostgreSQL role model:** remove `trust` from `pg_hba.conf`, drop `SUPERUSER` from `iam`, split into per-component roles | **High** | **M** | CIS PG 4.x/5.x, PCI 7.2, HIPAA §164.312(a) | §1.5 |
| **T-05** | **Enable TLS end to end:** public ingress (needs cert-manager + a real issuer), Postgres `sslmode`, Dragonfly, and a plan for Hydra/Keto | **High** | **M** | ASVS V9 (all), PCI 4.2.1, HIPAA §164.312(e) | §1.4, §1.5 |
| **T-06** | **`npm audit fix` in both SPAs.** 8 high advisories including `react-router` RCE and open-redirect chains | **High** | **S** | ASVS 14.2.1, PCI 6.3.3 | §1.3 |
| **T-07** | **Fix the four authentication defaults:** password floor 8 → 12, `CheckBreachedPasswords` → true, `Project.RequireMfa` → true, and either implement or remove `StubSmsService` | **High** | **S** | ASVS 2.1.1, 2.1.7, 2.2.2 | `PasswordPolicy.cs:31`; `Project.cs:29,33,17`; `Program.cs:135` |
| **T-08** | **Enable k3s secrets-at-rest encryption and API audit logging** (`--secrets-encryption`, `--kube-apiserver-arg=audit-log-path=…`) | **High** | **S** | CIS K8s 1.2.22/2.x, SOC 2 CC7.2, HIPAA §164.312(b) | `ps -eo args` |
| **T-09** | **Enable PostgreSQL logging** (`logging_collector`, `log_connections`, `log_disconnections`, a real `log_line_prefix`) and install `pgaudit` | **High** | **S** | CIS PG 3.x, PCI 10.2, HIPAA §164.312(b) | §1.5 |
| **T-10** | **Label the namespace for Pod Security Admission** (`restricted`) | Medium | **S** | CIS K8s 5.2 | `kubectl get ns default` |
| **T-11** | **Close P-05:** scope `body.Object` to the caller's org in `IntrospectionController.Authorize` | Medium | **S** | ASVS 4.1.2 | `11` §7 |
| **T-12** | **Close the P-08 residual:** consult `Organisation.Active` in the live authorisation re-check, with a super-admin carve-out so unsuspension stays possible | Medium | **S** | ASVS 4.3.3 | `11b` §4 |
| **T-13** | **Validate nested `providers[]` theme values** | Medium | **S** | ASVS 5.3.3 | `11b` §6 |
| **T-14** | **Review the four ReDoS hotspots** in `sanitizeCss.ts` | Medium | **S** | ASVS 5.2.2 | §1.2 |
| **T-15** | **Run the stale-`ServiceAccountRole` detection query** supplied at `11b` §1 and act on any hits. P-01's write path is closed; rows written before the fix are not | Medium | **S** | ASVS 4.1.2 | `11b` §1 |
| **T-16** | **Re-run SonarQube.** Current analysis predates five days of security changes and the quality gate is ERROR | Medium | **S** | ASVS 14.x, SOC 2 CC4.1 | §1.2 |
| **T-17** | **Move the release to its own namespace** with a namespace-wide default-deny | Medium | **M** | CIS K8s 5.7.1/5.7.4 | `09` §0 |
| **T-18** | **Implement key rotation** for the HKDF root, the Argon2 pepper and the Hydra system secret. Copy the versioned backup-code pattern. Makes C-3 and C-1 recoverable | **High** | **L** | ASVS 1.6.1/6.4.1, PCI 3.6, HIPAA §164.312(a)(2)(iv) | S-10; `03` §6 |
| **T-19** | **Row-level security for tenant isolation** (structural change S-5) | **High** | **L** | ASVS 4.2.1, PCI 7.2 | `pg_policies` = 0 |
| **T-20** | **Append-only or exported audit log**, plus off-host shipping | **High** | **M** | ASVS 7.3.3/1.7.2, SOC 2 CC7.2, PCI 10.3, HIPAA §164.312(c) | §1.5 |
| **T-21** | **Audience/expected-project binding in all three SDKs** (S-2 / P-06) — the residual half of chain C-1 | **High** | **M-L** | ASVS 13.x | grep across `sdk/` |
| **T-22** | **A CI pipeline**: build, test, `npm audit`, `dotnet list package --vulnerable`, SBOM generation, SAST gate | Medium | **M** | ASVS 1.14.3/14.2.4, SOC 2 CC8.1 | no `.github` |
| **T-23** | **Sign commits and images** (cosign + an admission policy) | Medium | **M** | ASVS 10.3.2/1.10.1 | `%G? = N` |
| **T-24** | **A data-retention and erasure implementation** for user PII | Medium | **M** | ASVS 8.3.7-8, GDPR Art. 17 | `03` §6 row 13 |
| **T-25** | **Secrets as mounted files rather than env vars**; consider an external secret store | Low | **M** | CIS K8s 5.4.1-2, ASVS 2.10.4 | `deployment.yaml:84-121` |
| **T-26** | **Assess SAML XML processing** for XXE and signature wrapping — currently a blind spot | Unknown | **S** to assess | ASVS 13.x | no step covered it |

**If only five ship: T-01, T-02, T-03, T-06, T-07.** All are S-effort, four of the five are already
written and merely undeployed, and together they close the three live critical exposures and the
four worst defaults.

### 7.2 Organisational gaps — no amount of code fixes these

| # | Gap | Blocks | Only remedy |
|---|---|---|---|
| **O-01** | **One operator.** `git log --format='%ae' \| sort -u` returns one address for 100 % of history. Author, reviewer, approver, deployer and production credential holder are the same person | SOC 2 CC6.3/CC8.1, PCI 6.4.2/7.x/12.x, ISO 27001 A.5.3 | A second person with review authority and production access. There is no technical substitute |
| **O-02** | **No evidence period.** A Type II opinion covers a window that has not started | SOC 2 Type II | 3 months minimum, 6–12 typical, starting after the controls are in place |
| **O-03** | **No policy set.** `docs/` holds four documents: two architecture/integration guides and two French audit notes. None is a policy | SOC 2 CC1–CC9, PCI 12.x, HIPAA §164.316, ISO 27001 all | 18–25 documents; £3k–£10k with a template vendor or 4–6 weeks of writing |
| **O-04** | **No change-management record.** No CI, no pull requests, no tickets, no approvals, no signed commits, no release notes tied to changes | SOC 2 CC8.1, PCI 6.5 | A process with an approver, which requires O-01 |
| **O-05** | **No monitoring history.** Prometheus metrics are exposed and scraped by nothing. No alerting, no on-call, no retention | SOC 2 CC7.2, PCI 10.4, HIPAA §164.308(a)(1)(ii)(D) | A monitoring stack **and** a human rota, which requires O-01 |
| **O-06** | **No incident response plan**, no breach-notification procedure, no forensic readiness (and no logs to be forensic with — see T-08, T-09, T-20) | SOC 2 CC7.3-7.5, PCI 12.10, HIPAA §164.308(a)(6), GDPR Art. 33 | A written, tested plan |
| **O-07** | **No business continuity or disaster recovery**, no tested restore (and, per T-03, nothing to restore from) | SOC 2 A1.2-1.3, HIPAA §164.308(a)(7) | Backups first, then a tested runbook |
| **O-08** | **No vendor/subprocessor management, no risk register, no HR controls** (background checks, onboarding, offboarding, training) | SOC 2 CC1.4/CC9.2, ISO 27001 A.6 | Process, which requires O-01 |
| **O-09** | **Incomplete evidence chain.** Reports `09`, `10`, `11` and `11b` — the infrastructure, secrets, penetration test and remediation documents — are **untracked in git**. The four most important documents in the audit trail are not under version control | SOC 2 CC4.1 evidence quality | One `git add`. This is the cheapest item in the entire report |

**The asymmetry is the point.** Every technical gap in §7.1 is fixable by the person already doing
the work. Not one organisational gap in §7.2 is. O-01 is the root of six of the nine, and its cost
is a salary, not a sprint.

---

## 8. Audit evidence: what exists and what an auditor would ask for

### 8.1 Evidence that exists today and would survive scrutiny

| Evidence | Where | Strength |
|---|---|---|
| Risk assessment / threat model | `.security-hardening/02-threat-model.md`, tracked at `0144e1c` | **Strong.** STRIDE per trust boundary, 5 attack trees, 9 escalation chains, MITRE ATT&CK mapping, an explicit disagreement section arguing against the previous step's scores |
| Architecture and design review | `03-architecture-review.md`, tracked | **Strong.** Names its own structural debt (S-1…S-10) and ranks it by findings prevented |
| Penetration test | `11-pentest-results.md`, **untracked** | **Strong in content, weak in provenance.** It found three exploitable holes in the previous steps' claimed fixes and proved each with an executing test. Not independent — same author as the code |
| Remediation with residuals disclosed | `11b-pentest-remediation.md`, **untracked** | **Strong.** §8 is a table of what was deliberately left undone and what it costs |
| Regression test suite | `tests/RediensIAM.IntegrationTests` | **Strong.** 1,221 tests, 0 failures, **executed and verified in this session**, including 23 tests written as security proofs |
| Application audit trail | `AuditLog` table, 98 recording points | **Moderate.** Comprehensive coverage; no integrity guarantee |
| Static analysis | SonarQube project `RediensIAM` | **Moderate, and stale.** Quality gate ERROR; last analysis predates five days of changes |
| Data classification | `03` §6 | **Strong.** 21 categories with sensitivity, at-rest, in-transit, retention, readership |
| Infrastructure as code | `deploy/rediensiam/` | **Strong as an artefact, weak as evidence of state.** The chart does not describe what is running |
| Version-controlled history | git, 15 commits on the branch | **Weak as change-management evidence.** One author, unsigned, no reviews, no approvals |

### 8.2 What an auditor would ask for and would not receive

| Request | Status |
|---|---|
| "Show me the information security policy." | Does not exist |
| "Show me your access-review records for the last two quarters." | Do not exist. There is one user |
| "Show me a change ticket, its approval, and the deployment that resulted." | Do not exist |
| "Show me your last restore test." | No backup has ever been taken |
| "Show me 90 days of security monitoring alerts and their dispositions." | No monitoring exists |
| "Show me the Kubernetes audit log for the period." | Not enabled |
| "Show me the database access log." | Not enabled |
| "Show me evidence the audit log cannot be altered." | It can be, by the application's own superuser role |
| "Show me your independent penetration test report." | The internal one in `11` is high quality but is not independent — same author as the code under test |
| "Show me your SBOM and dependency scan results for the release." | No SBOM; dependency scanning is manual and currently shows 8 unremediated highs |
| "Show me your key rotation records and defined cryptoperiods." | No rotation implementation exists for any key |
| "Show me who approved this production change." | Same person who made it |
| "Show me your incident response plan and its last tabletop exercise." | Neither exists |
| "Show me the subprocessor list and your vendor risk assessments." | Do not exist |
| "Show me evidence that production and development are separated." | One cluster, one namespace, dev configuration currently live |

### 8.3 The cheapest evidence improvements

1. **`git add .security-hardening/09 10 11 11b` and commit.** Five seconds. Puts the penetration
   test under version control, which is the difference between "we did a pen test" and "here is
   the immutable record of the pen test and the remediation that followed."
2. **Enable the Kubernetes API audit log and the PostgreSQL connection log.** Two flags and four
   settings. Starts the clock on the only evidence type that cannot be back-filled.
3. **Sign commits.** One `git config` and a key. Converts every future commit into attributable
   evidence.
4. **Re-run `sonar-scan.sh`.** Five minutes. Makes the SAST evidence current.
5. **Schedule the scans.** A weekly cron running `npm audit`, `dotnet list package --vulnerable`
   and `sonar-scan.sh`, writing to a dated file, is a SOC 2 CC7.1 control with an evidence artefact
   for roughly an hour of work.

---

## 9. What this system is not ready for

Stated without hedging, because this is the section that matters most.

**Not ready:**

- **Any regulated customer.** Healthcare, financial services, payments, government. The absence of
  encryption in transit, backups, key rotation and audit-log integrity would each independently
  fail a vendor security review.
- **Holding cardholder data or PHI.** It holds neither today. It should not begin to until §7.1
  T-01 through T-09 are done and a BAA or PCI scope decision is in place.
- **A SOC 2 (Type I or Type II) or ISO 27001 attestation.** Blocked by organisational gaps, not
  technical ones. Type II is 12–18 months and a second headcount away.
- **A customer security questionnaire.** The standard first four questions — encryption in transit,
  encryption at rest, backup and recovery, segregation of duties — are four honest "no"s today.
- **Multi-tenant production with tenants who do not trust each other.** Tenant isolation is enforced
  by ~200 hand-written query conjuncts and zero database-level policies. It held against every
  attack step 11 attempted, and P-01 showed how one missing conjunct becomes a full cross-tenant
  token disclosure. The 1,221-test suite is currently the only thing standing between a future edit
  and that outcome.
- **Relying parties that validate tokens locally.** Chain C-1's remaining half is open: no SDK binds
  an audience or an expected project, so a downstream resource server behind a deployment-scoped
  credential must remember to compare `project_id` itself. Nothing makes that safe by construction.
- **Being the identity provider for anything the operator cannot afford to lose.** There is no
  backup.

**Ready, with the deployment gap closed:**

- The operator's own applications on a private network.
- A small number of external tenants under a contract that discloses: no encryption in transit
  until T-05 lands, no independent penetration test, no SOC 2, no backup SLA, no 24/7 monitoring,
  and a single operator.

**Genuinely strong, and worth saying because a compliance report that only lists failures is as
misleading as one that only lists successes:**

- Cryptographic primitives: Argon2id with an optional HMAC pepper, AES-256-GCM under per-purpose
  HKDF subkeys, constant-time comparison everywhere, a versioned backup-code format that makes
  pepper rotation detectable.
- Injection posture: no SQL injection, no unsafe deserialisation, no command execution, no path
  traversal, no `innerHTML` in either SPA, no tokens in `localStorage`, no secrets in git history.
- SSRF defence at two layers, with the network layer covering the CGNAT range the application layer
  already refused.
- The clamping discipline in `AppConfig`: every operator-tunable security parameter bounded to a
  range in which it remains a control, specifically so that a single database write cannot disable
  lockout, weaken future password hashing, neutralise PAT revocation or purge the evidence.
- Fail-closed defaults in the places that matter, including a startup exception when
  `App__TrustedProxies` is unset in production.
- An audit trail — 12 reports, an adversarial penetration test that broke three of its own
  predecessors' claims, and 1,221 passing tests — that is more rigorous than most systems ten times
  this size ever receive.

The distance between that engineering and this compliance verdict is almost entirely **deployment
and organisation**, not code quality.

---

## 10. Limits of this assessment

1. **I could not read the k3s server configuration.** `/etc/rancher/k3s/config.yaml`,
   `/etc/rancher/k3s/registries.yaml` and `/var/lib/rancher/k3s/server/` all require `sudo`.
   Control-plane CIS sections 1.x–3.x are marked ❓ and are **not** assumed non-compliant. The
   inference that secrets-at-rest encryption and API audit logging are off rests on the server
   process running with no flags, which is strong but indirect.
2. **I did not audit the `yandee-*` workloads.** They are out of scope. The PCI-DSS
   connected-system question in §2.1 is therefore left open, with the decision rule stated.
3. **I did not execute a browser-based test of the CSP or the theme-injection path.** Like steps 11
   and 11b, that half is by inspection.
4. **SonarQube evidence is stale** by five days and one major remediation step. I flagged it rather
   than re-running the scan, which would have modified `.sonarqube/`.
5. **I did not assess the SAML XML processing path** for XXE or signature wrapping. No step in this
   directory did. It is recorded as T-26 and as a named blind spot rather than as a pass.
6. **The penetration test in `11` is not independent.** It is high quality and it found real holes,
   but the same author wrote the code, the fixes and the test. An auditor will not accept it as a
   third-party assessment and neither should a customer.
7. **Two commits (`d6b0714`, `9472e59`) landed during this assessment**, committing the step 9–11b
   code. Every code citation in this report was read from the working tree and the content is
   unchanged by those commits; the cluster state was and remains unaffected.
8. **This document is an assessment, not an attestation.** No statement in it should be presented
   to a customer as certification, and §9 should be shown alongside any part of it that is.
